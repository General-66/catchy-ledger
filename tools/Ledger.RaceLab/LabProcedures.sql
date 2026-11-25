-- The race lab's own procedures. The lab installs them into its throwaway database only; no migration does, so
-- nothing here can reach the real books. Each lab.usp_PlaceBet_* places one bet: it checks the wallet can cover the
-- stake, then moves the stake from the wallet to Open Bets. The four differ only in how they read and lock the
-- wallet's balance, which is the whole experiment.
--
-- A wallet is a liability, so its stored balance is zero or below: R100 is -10000. A bet adds the stake to it.
-- "Balance + stake > 0" therefore means "this bet would overdraw the wallet".

CREATE SCHEMA lab;
GO

-- The part every approach shares: record a two-line transaction. It runs inside the caller's database transaction,
-- and it doesn't touch balances: each approach moves those itself, because that's where they differ.
CREATE PROCEDURE lab.usp_Record
    @DebitId        bigint,
    @CreditId       bigint,
    @AmountMinor    bigint,
    @Description    nvarchar(200),
    @IdempotencyKey nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.Transactions (CreatedBy, IdempotencyKey, RequestHash, Description, EffectiveDate)
    VALUES (N'race-lab', @IdempotencyKey, HASHBYTES('SHA2_256', @IdempotencyKey), @Description, CAST(SYSUTCDATETIME() AS date));

    DECLARE @TransactionId bigint = SCOPE_IDENTITY();

    INSERT INTO dbo.Postings (TransactionId, AccountId, AmountMinor, Currency)
    VALUES (@TransactionId, @DebitId, @AmountMinor, 'ZAR'),
           (@TransactionId, @CreditId, -@AmountMinor, 'ZAR');
END;
GO

-- Funds a wallet before a race: debit Bank, credit the wallet, and move both balances.
CREATE PROCEDURE lab.usp_Deposit
    @BankId         bigint,
    @WalletId       bigint,
    @AmountMinor    bigint,
    @IdempotencyKey nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    EXECUTE lab.usp_Record @DebitId = @BankId, @CreditId = @WalletId, @AmountMinor = @AmountMinor,
        @Description = N'Lab deposit', @IdempotencyKey = @IdempotencyKey;

    UPDATE dbo.AccountBalances SET BalanceMinor = BalanceMinor + @AmountMinor WHERE AccountId = @BankId;
    UPDATE dbo.AccountBalances SET BalanceMinor = BalanceMinor - @AmountMinor WHERE AccountId = @WalletId;

    COMMIT TRANSACTION;
END;
GO

-- 1. READ COMMITTED: read the balance, check it, then act. The bug this phase is about.
--    Being inside a transaction doesn't help. READ COMMITTED only promises not to show you uncommitted changes;
--    it doesn't stop anyone changing what you read the moment after you read it.
CREATE PROCEDURE lab.usp_PlaceBet_ReadCommitted
    @WalletId       bigint,
    @OpenBetsId     bigint,
    @StakeMinor     bigint,
    @IdempotencyKey nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON; -- also rolls the transaction back when THROW refuses the bet

    BEGIN TRANSACTION;

    -- Read-committed snapshot hands back the last committed balance without waiting, and holds no lock on it.
    DECLARE @Balance bigint = (SELECT BalanceMinor FROM dbo.AccountBalances WHERE AccountId = @WalletId);

    IF @Balance + @StakeMinor > 0
        THROW 50006, N'Insufficient funds.', 1;

    EXECUTE lab.usp_Record @DebitId = @WalletId, @CreditId = @OpenBetsId, @AmountMinor = @StakeMinor,
        @Description = N'Lab bet', @IdempotencyKey = @IdempotencyKey;

    -- Each UPDATE adds to the balance as it is now, so no bet's money goes missing. The check above is what's wrong.
    UPDATE dbo.AccountBalances SET BalanceMinor = BalanceMinor + @StakeMinor WHERE AccountId = @WalletId;
    UPDATE dbo.AccountBalances SET BalanceMinor = BalanceMinor - @StakeMinor WHERE AccountId = @OpenBetsId;

    COMMIT TRANSACTION;
END;
GO

-- 2. SERIALIZABLE: the same code, at the strictest isolation level.
--    Every read now keeps a shared lock until COMMIT. Two bets can both read the wallet, and then neither can update
--    it, because each holds a lock the other's update must wait for. SQL Server ends that deadlock by rolling one
--    back with error 1205, and the lab runs that bet again.
CREATE PROCEDURE lab.usp_PlaceBet_Serializable
    @WalletId       bigint,
    @OpenBetsId     bigint,
    @StakeMinor     bigint,
    @IdempotencyKey nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; -- for this procedure only; the caller's level returns afterwards

    BEGIN TRANSACTION;

    DECLARE @Balance bigint = (SELECT BalanceMinor FROM dbo.AccountBalances WHERE AccountId = @WalletId);

    IF @Balance + @StakeMinor > 0
        THROW 50006, N'Insufficient funds.', 1;

    EXECUTE lab.usp_Record @DebitId = @WalletId, @CreditId = @OpenBetsId, @AmountMinor = @StakeMinor,
        @Description = N'Lab bet', @IdempotencyKey = @IdempotencyKey;

    UPDATE dbo.AccountBalances SET BalanceMinor = BalanceMinor + @StakeMinor WHERE AccountId = @WalletId;
    UPDATE dbo.AccountBalances SET BalanceMinor = BalanceMinor - @StakeMinor WHERE AccountId = @OpenBetsId;

    COMMIT TRANSACTION;
END;
GO

-- 3. UPDLOCK, HOLDLOCK: lock the wallet's balance as you read it.
--    Only one bet at a time can hold an update lock on the row. The others queue at this SELECT until the holder
--    commits, then read the balance as it is after that bet.
CREATE PROCEDURE lab.usp_PlaceBet_Pessimistic
    @WalletId       bigint,
    @OpenBetsId     bigint,
    @StakeMinor     bigint,
    @IdempotencyKey nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @Balance bigint = (SELECT BalanceMinor FROM dbo.AccountBalances WITH (UPDLOCK, HOLDLOCK) WHERE AccountId = @WalletId);

    IF @Balance + @StakeMinor > 0
        THROW 50006, N'Insufficient funds.', 1;

    EXECUTE lab.usp_Record @DebitId = @WalletId, @CreditId = @OpenBetsId, @AmountMinor = @StakeMinor,
        @Description = N'Lab bet', @IdempotencyKey = @IdempotencyKey;

    UPDATE dbo.AccountBalances SET BalanceMinor = BalanceMinor + @StakeMinor WHERE AccountId = @WalletId;
    UPDATE dbo.AccountBalances SET BalanceMinor = BalanceMinor - @StakeMinor WHERE AccountId = @OpenBetsId;

    COMMIT TRANSACTION;
END;
GO

-- 4. rowversion + retry: read without locking, then update only if nobody else has since.
--    SQL Server changes RowVersion on every update to the row. If it's no longer the value this bet read, another
--    bet got there first: nothing is written, error 50030 says so, and the lab reads again and retries.
CREATE PROCEDURE lab.usp_PlaceBet_Optimistic
    @WalletId       bigint,
    @OpenBetsId     bigint,
    @StakeMinor     bigint,
    @IdempotencyKey nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Read the balance and its version. Nothing is locked or held: other bets carry on freely meanwhile.
    DECLARE @Balance bigint, @SeenVersion binary(8);

    SELECT @Balance = BalanceMinor, @SeenVersion = RowVersion
    FROM dbo.AccountBalances
    WHERE AccountId = @WalletId;

    IF @Balance + @StakeMinor > 0
        THROW 50006, N'Insufficient funds.', 1;

    BEGIN TRANSACTION;

    UPDATE dbo.AccountBalances
    SET BalanceMinor = BalanceMinor + @StakeMinor
    WHERE AccountId = @WalletId AND RowVersion = @SeenVersion;

    IF @@ROWCOUNT = 0
        THROW 50030, N'The wallet changed after it was read. Read it again and retry.', 1;

    EXECUTE lab.usp_Record @DebitId = @WalletId, @CreditId = @OpenBetsId, @AmountMinor = @StakeMinor,
        @Description = N'Lab bet', @IdempotencyKey = @IdempotencyKey;

    UPDATE dbo.AccountBalances SET BalanceMinor = BalanceMinor - @StakeMinor WHERE AccountId = @OpenBetsId;

    COMMIT TRANSACTION;
END;
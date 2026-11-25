-- 0009: posting locks the balances it moves, and refuses to take an account below zero.
--
-- usp_WriteTransaction is the one place a transaction is written from now on, for posting and for reversing. It takes
-- every lock, and makes every decision that depends on what's already in the books, while it holds them. The two
-- public procedures check the request itself first, then call it. The app can't call it directly: ledger_app has no
-- EXECUTE on it, and reaches it only through those two procedures, by ownership chaining, as in 0006.

CREATE PROCEDURE dbo.usp_WriteTransaction
    @CreatedBy             nvarchar(100),
    @IdempotencyKey        nvarchar(100),
    @RequestHash           binary(32),
    @Description           nvarchar(200),
    @EffectiveDate         date,
    @ReversesTransactionId bigint,  -- NULL for an ordinary transaction; the original's Id for a reversal
    @Lines                 dbo.PostingLineList READONLY,
    @TransactionId         bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Message nvarchar(2048), @AccountId bigint, @ReversedBy bigint,
            @ShortCode varchar(20), @HasMinor bigint, @TakesMinor bigint;

    -- What the transaction does to each account: the total of its lines on that account. Usually one line each.
    DECLARE @Moves TABLE (AccountId bigint NOT NULL PRIMARY KEY, DeltaMinor bigint NOT NULL);

    INSERT INTO @Moves (AccountId, DeltaMinor)
    SELECT AccountId, SUM(AmountMinor)
    FROM @Lines
    GROUP BY AccountId;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- 1. Lock every balance this transaction moves, one at a time, in ascending account Id order.
        --    UPDLOCK: only one transaction at a time may hold a balance row; the next one waits here until COMMIT.
        --    HOLDLOCK: keep the lock until COMMIT, serializably, even for a row that isn't there.
        --    Every transaction locks in the same order, so two of them can never each hold a row the other is
        --    waiting for. That's what stops deadlocks between them.
        SET @AccountId = (SELECT MIN(AccountId) FROM @Moves);

        WHILE @AccountId IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.AccountBalances WITH (UPDLOCK, HOLDLOCK) WHERE AccountId = @AccountId)
            BEGIN
                SET @Message = CONCAT(N'Account ', @AccountId, N' has no balance row. Open accounts with usp_CreateAccount.');
                THROW 50021, @Message, 1;
            END;

            SET @AccountId = (SELECT MIN(AccountId) FROM @Moves WHERE AccountId > @AccountId);
        END;

        -- From here on nobody else can move these balances until COMMIT, so whatever is read now stays true.

        -- 2. A reversal: check again that nobody reversed the original while this one waited for the locks.
        --    The caller checked before it had any locks, which is quick but may be out of date by now.
        IF @ReversesTransactionId IS NOT NULL
        BEGIN
            SET @ReversedBy = (SELECT Id FROM dbo.Transactions WHERE ReversesTransactionId = @ReversesTransactionId);

            IF @ReversedBy IS NOT NULL
            BEGIN
                SET @Message = CONCAT(N'Transaction ', @ReversesTransactionId, N' was already reversed by transaction ',
                    @ReversedBy, N'.');
                THROW 50012, @Message, 1;
            END;
        END;

        -- 3. The insufficient-funds rule. An account that doesn't allow overdrafts can't end below zero on its normal
        --    side, if this transaction takes money out of it. NormalSign reads a stored balance the way a person
        --    would, like Account.BalanceOnNormalSide: +1 for assets and expenses, -1 for liabilities, equity and income.
        --    A wallet holding R20 is stored as -2000: times -1, that's 2000 on its normal side.
        SELECT TOP (1)
            @ShortCode = a.Code,
            @HasMinor = b.BalanceMinor * s.NormalSign,
            @TakesMinor = -m.DeltaMinor * s.NormalSign
        FROM @Moves AS m
        JOIN dbo.Accounts AS a ON a.Id = m.AccountId
        JOIN dbo.AccountBalances AS b ON b.AccountId = m.AccountId
        CROSS APPLY (SELECT CASE WHEN a.Type IN ('Asset', 'Expense') THEN 1 ELSE -1 END AS NormalSign) AS s
        WHERE a.AllowOverdraft = 0
          AND m.DeltaMinor * s.NormalSign < 0                      -- this transaction takes money out of it
          AND (b.BalanceMinor + m.DeltaMinor) * s.NormalSign < 0   -- and that would leave it below zero
        ORDER BY m.AccountId;

        IF @ShortCode IS NOT NULL
        BEGIN
            SET @Message = CONCAT(N'Insufficient funds in account ', @ShortCode, N': it holds ', @HasMinor,
                N' minor units and this transaction takes ', @TakesMinor, N'.');
            THROW 50006, @Message, 1;
        END;

        -- 4. Write it all: the transaction, its lines in the caller's order, and the balances they move.
        --    A duplicate Idempotency-Key fails here with error 2627, and a second reversal with 2601, as before.
        INSERT INTO dbo.Transactions (CreatedBy, IdempotencyKey, RequestHash, Description, EffectiveDate, ReversesTransactionId)
        VALUES (@CreatedBy, @IdempotencyKey, @RequestHash, @Description, @EffectiveDate, @ReversesTransactionId);

        SET @TransactionId = SCOPE_IDENTITY();

        INSERT INTO dbo.Postings (TransactionId, AccountId, AmountMinor, Currency)
        SELECT @TransactionId, AccountId, AmountMinor, Currency
        FROM @Lines
        ORDER BY LineNumber;

        -- Adds to what's there: each balance moves by exactly what was posted to it, so it always equals its postings.
        UPDATE b
        SET BalanceMinor = b.BalanceMinor + m.DeltaMinor
        FROM dbo.AccountBalances AS b
        JOIN @Moves AS m ON m.AccountId = b.AccountId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        THROW; -- pass the original error on, with its own number
    END CATCH;
END;
GO

-- The same checks on the request as 0004, unchanged. What used to follow them, the writing, is now
-- usp_WriteTransaction's job. CREATE OR ALTER keeps the GRANT EXECUTE from 0006.
CREATE OR ALTER PROCEDURE dbo.usp_PostTransaction
    @CreatedBy      nvarchar(100),
    @IdempotencyKey nvarchar(100),
    @RequestHash    binary(32),
    @Description    nvarchar(200),
    @EffectiveDate  date,
    @Lines          dbo.PostingLineList READONLY,
    @TransactionId  bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;  -- don't send "1 row affected" messages back to the caller
    SET XACT_ABORT ON; -- any error rolls the whole transaction back, never half of it

    DECLARE @Message nvarchar(2048);

    IF NULLIF(@CreatedBy, N'') IS NULL OR NULLIF(@IdempotencyKey, N'') IS NULL OR @RequestHash IS NULL
        OR NULLIF(@Description, N'') IS NULL OR @EffectiveDate IS NULL
        THROW 50020, N'CreatedBy, IdempotencyKey, RequestHash, Description and EffectiveDate are all required.', 1;

    -- The rules, checked in the same order as Transaction.Create in C#. Each THROW uses its own
    -- error number (listed in LedgerErrors.cs), so the caller can tell the reasons apart.
    DECLARE @LineCount int, @ZeroLines int, @CurrencyCount int, @Sum bigint;

    SELECT @LineCount = COUNT(*),
           @ZeroLines = SUM(CASE WHEN AmountMinor = 0 THEN 1 ELSE 0 END),
           @CurrencyCount = COUNT(DISTINCT Currency),
           @Sum = SUM(AmountMinor)
    FROM @Lines;

    IF @LineCount < 2
    BEGIN
        SET @Message = CONCAT(N'A transaction needs at least two postings; this one has ', @LineCount, N'.');
        THROW 50001, @Message, 1;
    END;

    IF @ZeroLines > 0
        THROW 50002, N'Every posting must move money; a posting of zero was sent.', 1;

    IF @CurrencyCount > 1
        THROW 50003, N'A transaction uses one currency; the postings are in more than one.', 1;

    IF EXISTS (SELECT 1 FROM @Lines AS l WHERE NOT EXISTS (SELECT 1 FROM dbo.Accounts AS a WHERE a.Id = l.AccountId))
        THROW 50010, N'A posting refers to an account that does not exist.', 1;

    IF EXISTS (SELECT 1 FROM @Lines AS l JOIN dbo.Accounts AS a ON a.Id = l.AccountId WHERE a.Currency <> l.Currency)
        THROW 50004, N'A posting is in a different currency from its account.', 1;

    IF @Sum <> 0
    BEGIN
        SET @Message = CONCAT(N'Postings must sum to zero, but they sum to ', @Sum, N' minor units.');
        THROW 50005, @Message, 1;
    END;

    -- Everything that depends on what's already in the books happens in there, under locks.
    EXECUTE dbo.usp_WriteTransaction
        @CreatedBy = @CreatedBy,
        @IdempotencyKey = @IdempotencyKey,
        @RequestHash = @RequestHash,
        @Description = @Description,
        @EffectiveDate = @EffectiveDate,
        @ReversesTransactionId = NULL,
        @Lines = @Lines,
        @TransactionId = @TransactionId OUTPUT;
END;
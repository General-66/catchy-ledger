-- 0004: the only way money gets into the books.
-- The C# domain already refuses an unbalanced transaction. This procedure checks the same rules again,
-- inside the database, so nothing that bypasses the C# code can post a bad transaction either.

-- One row per posting line, passed in as a table-valued parameter: all the lines arrive together,
-- so the procedure can check them as a set before inserting any of them.
CREATE TYPE dbo.PostingLineList AS TABLE
(
    LineNumber  int     NOT NULL PRIMARY KEY, -- keeps the lines in the order the caller sent them
    AccountId   bigint  NOT NULL,
    AmountMinor bigint  NOT NULL,
    Currency    char(3) COLLATE Latin1_General_BIN2 NOT NULL -- COLLATE is there to enforce the uppercase rule at the binary level in the Currency table for standardization of currency code storage
);
GO

CREATE PROCEDURE dbo.usp_PostTransaction
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

    BEGIN TRY
        BEGIN TRANSACTION;

        -- The idempotency key goes in with the postings, in the same transaction. If a second request with the
        -- same key arrives at the same moment, it waits on the unique constraint, then fails with error 2627.
        INSERT INTO dbo.Transactions (CreatedBy, IdempotencyKey, RequestHash, Description, EffectiveDate)
        VALUES (@CreatedBy, @IdempotencyKey, @RequestHash, @Description, @EffectiveDate);

        SET @TransactionId = SCOPE_IDENTITY();

        INSERT INTO dbo.Postings (TransactionId, AccountId, AmountMinor, Currency)
        SELECT @TransactionId, AccountId, AmountMinor, Currency
        FROM @Lines
        ORDER BY LineNumber;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        THROW; -- pass the original error on, with its own number
    END CATCH;
END;
-- 0005: fixing a mistake without editing anything.
-- A reversal is a new transaction whose lines are the original's lines with the signs flipped.
-- Together the two add up to nothing, and both stay in the books for anyone to read.

CREATE PROCEDURE dbo.usp_ReverseTransaction
    @OriginalTransactionId bigint,
    @CreatedBy             nvarchar(100),
    @IdempotencyKey        nvarchar(100),
    @RequestHash           binary(32),
    @EffectiveDate         date,
    @TransactionId         bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Message nvarchar(2048), @OriginalDescription nvarchar(200), @OriginalReverses bigint, @ReversedBy bigint;

    IF NULLIF(@CreatedBy, N'') IS NULL OR NULLIF(@IdempotencyKey, N'') IS NULL OR @RequestHash IS NULL
        OR @EffectiveDate IS NULL
        THROW 50020, N'CreatedBy, IdempotencyKey, RequestHash and EffectiveDate are all required.', 1;

    SELECT @OriginalDescription = Description, @OriginalReverses = ReversesTransactionId
    FROM dbo.Transactions
    WHERE Id = @OriginalTransactionId;

    -- Description is never NULL in the table, so NULL here means no row was found.
    IF @OriginalDescription IS NULL
    BEGIN
        SET @Message = CONCAT(N'Transaction ', @OriginalTransactionId, N' does not exist.');
        THROW 50011, @Message, 1;
    END;

    -- Reversing a reversal would bring the mistake back. Post a new, correct transaction instead.
    IF @OriginalReverses IS NOT NULL
    BEGIN
        SET @Message = CONCAT(N'Transaction ', @OriginalTransactionId, N' is itself a reversal of transaction ',
            @OriginalReverses, N', so it can''t be reversed.');
        THROW 50013, @Message, 1;
    END;

    SELECT @ReversedBy = Id FROM dbo.Transactions WHERE ReversesTransactionId = @OriginalTransactionId;

    IF @ReversedBy IS NOT NULL
    BEGIN
        SET @Message = CONCAT(N'Transaction ', @OriginalTransactionId, N' was already reversed by transaction ', @ReversedBy, N'.');
        THROW 50012, @Message, 1;
    END;

    DECLARE @Description nvarchar(200) = LEFT(CONCAT(N'Reversal of ', @OriginalTransactionId, N': ', @OriginalDescription), 200);

    BEGIN TRY
        BEGIN TRANSACTION;

        INSERT INTO dbo.Transactions (CreatedBy, IdempotencyKey, RequestHash, Description, EffectiveDate, ReversesTransactionId)
        VALUES (@CreatedBy, @IdempotencyKey, @RequestHash, @Description, @EffectiveDate, @OriginalTransactionId);

        SET @TransactionId = SCOPE_IDENTITY();

        -- The mirror image: every line of the original, with its sign flipped, in the same order.
        INSERT INTO dbo.Postings (TransactionId, AccountId, AmountMinor, Currency)
        SELECT @TransactionId, AccountId, -AmountMinor, Currency
        FROM dbo.Postings
        WHERE TransactionId = @OriginalTransactionId
        ORDER BY Id;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        -- Two reversals of the same transaction at the same moment can both pass the check above.
        -- The unique index lets only one in; the other lands here and gets the same answer as a late arrival.
        IF ERROR_NUMBER() = 2601 AND ERROR_MESSAGE() LIKE N'%UX_Transactions_ReversesTransactionId%'
        BEGIN
            SET @Message = CONCAT(N'Transaction ', @OriginalTransactionId, N' was already reversed.');
            THROW 50012, @Message, 1;
        END;

        THROW;
    END CATCH;
END;
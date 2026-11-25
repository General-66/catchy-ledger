-- 0010: a reversal takes the same locks and follows the same insufficient-funds rule as any other posting.
-- The checks on the original are 0005's, unchanged. The writing is now usp_WriteTransaction's job (0009), which
-- checks "already reversed?" again once it holds the locks. CREATE OR ALTER keeps the GRANT EXECUTE from 0006.

CREATE OR ALTER PROCEDURE dbo.usp_ReverseTransaction
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

    -- A quick check before any locks, so most second reversals are refused without waiting for anything.
    -- usp_WriteTransaction checks again under the locks, and the unique index is the final guarantee.
    SELECT @ReversedBy = Id FROM dbo.Transactions WHERE ReversesTransactionId = @OriginalTransactionId;

    IF @ReversedBy IS NOT NULL
    BEGIN
        SET @Message = CONCAT(N'Transaction ', @OriginalTransactionId, N' was already reversed by transaction ', @ReversedBy, N'.');
        THROW 50012, @Message, 1;
    END;

    DECLARE @Description nvarchar(200) = LEFT(CONCAT(N'Reversal of ', @OriginalTransactionId, N': ', @OriginalDescription), 200);

    -- The mirror image: every line of the original, with its sign flipped, in the same order.
    DECLARE @Mirror dbo.PostingLineList;

    INSERT INTO @Mirror (LineNumber, AccountId, AmountMinor, Currency)
    SELECT ROW_NUMBER() OVER (ORDER BY Id), AccountId, -AmountMinor, Currency
    FROM dbo.Postings
    WHERE TransactionId = @OriginalTransactionId;

    BEGIN TRY
        EXECUTE dbo.usp_WriteTransaction
            @CreatedBy = @CreatedBy,
            @IdempotencyKey = @IdempotencyKey,
            @RequestHash = @RequestHash,
            @Description = @Description,
            @EffectiveDate = @EffectiveDate,
            @ReversesTransactionId = @OriginalTransactionId,
            @Lines = @Mirror,
            @TransactionId = @TransactionId OUTPUT;
    END TRY
    BEGIN CATCH
        -- If two reversals of the same transaction ever both got past the check under the locks, the unique index
        -- would let only one in. The other lands here, and gets the same answer as a late arrival.
        IF ERROR_NUMBER() = 2601 AND ERROR_MESSAGE() LIKE N'%UX_Transactions_ReversesTransactionId%'
        BEGIN
            SET @Message = CONCAT(N'Transaction ', @OriginalTransactionId, N' was already reversed.');
            THROW 50012, @Message, 1;
        END;

        THROW;
    END CATCH;
END;
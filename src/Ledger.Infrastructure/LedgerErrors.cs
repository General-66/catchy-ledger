namespace Ledger.Infrastructure;

/// <summary>
/// The error numbers the stored procedures THROW, and the SQL Server errors the ledger relies on.
/// A <see cref="Microsoft.Data.SqlClient.SqlException"/>'s Number property holds one of these.
/// The first five mirror <see cref="Ledger.Domain.LedgerRule"/>: the database checks the same rules again.
/// </summary>
public static class LedgerErrors
{
    public const int TooFewPostings = 50001;
    public const int ZeroAmountPosting = 50002;
    public const int MixedCurrencies = 50003;
    public const int AccountCurrencyMismatch = 50004;
    public const int Unbalanced = 50005;

    public const int UnknownAccount = 50010;
    public const int TransactionNotFound = 50011;
    public const int AlreadyReversed = 50012;
    public const int ReversalOfReversal = 50013;

    /// <summary>A required value such as CreatedBy or the Idempotency-Key was missing.</summary>
    public const int MissingRequestDetails = 50020;

    /// <summary>SQL Server's own error for a duplicate value in a UNIQUE constraint, such as a reused Idempotency-Key.</summary>
    public const int DuplicateKey = 2627;

    /// <summary>SQL Server's own error for a duplicate value in a unique index, such as a second reversal.</summary>
    public const int DuplicateIndexKey = 2601;

    /// <summary>SQL Server's own error when a user lacks a permission, or has been denied one.</summary>
    public const int PermissionDenied = 229;

    /// <summary>SQL Server's own error for an UPDATE on an append-only ledger table, whoever runs it.</summary>
    public const int AppendOnlyLedgerTable = 37359;
}
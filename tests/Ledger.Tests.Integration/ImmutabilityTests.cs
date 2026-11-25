using Dapper;
using Ledger.Infrastructure;
using Ledger.Tests.Integration.Support;
using Microsoft.Data.SqlClient;

namespace Ledger.Tests.Integration;

/// <summary>
/// Once posted, a transaction can't be changed or removed, by the app or by an administrator,
/// and SQL Server can prove nothing was tampered with.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ImmutabilityTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private static readonly long[] DepositAmounts = [50_000, -50_000];

    private LedgerDatabase _database = null!;
    private long _depositId;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.DatabaseForAsync(nameof(ImmutabilityTests));
        var ledger = _database.AsApp();
        var books = await TestBooks.CreateAsync(ledger);
        _depositId = await ledger.PostAsync(books.Deposit(500_00), TestRequests.NewRequest());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_app_cannot_update_a_posting()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAppAsync(
            "UPDATE dbo.Postings SET AmountMinor = AmountMinor * 2 WHERE TransactionId = @Id;", new { Id = _depositId }));

        Assert.Equal(LedgerErrors.AppendOnlyLedgerTable, error.Number);
        Assert.Equal(DepositAmounts, await _database.AmountsOfAsync(_depositId));
    }

    [Fact]
    public async Task The_app_cannot_delete_a_posting()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAppAsync(
            "DELETE FROM dbo.Postings WHERE TransactionId = @Id;", new { Id = _depositId }));

        Assert.Equal(LedgerErrors.AppendOnlyLedgerTable, error.Number);
        Assert.Equal(DepositAmounts, await _database.AmountsOfAsync(_depositId));
    }

    [Fact]
    public async Task The_app_cannot_slip_in_an_extra_posting_without_the_procedure()
    {
        // A direct INSERT would skip every check in usp_PostTransaction and unbalance the deposit.
        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAppAsync(
            """
            INSERT INTO dbo.Postings (TransactionId, AccountId, AmountMinor, Currency)
            SELECT TOP (1) TransactionId, AccountId, 1, Currency FROM dbo.Postings WHERE TransactionId = @Id;
            """,
            new { Id = _depositId }));

        Assert.Equal(LedgerErrors.PermissionDenied, error.Number);
        Assert.Equal(DepositAmounts, await _database.AmountsOfAsync(_depositId));
    }

    [Fact]
    public async Task The_app_cannot_rewrite_a_transaction()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAppAsync(
            "UPDATE dbo.Transactions SET Description = N'Nothing to see here' WHERE Id = @Id;", new { Id = _depositId }));

        Assert.Equal(LedgerErrors.AppendOnlyLedgerTable, error.Number);
    }

    [Fact]
    public async Task The_app_role_is_denied_every_direct_write_to_the_books()
    {
        // The ledger table refuses UPDATE and DELETE before the DENY is ever checked, so read the DENYs from
        // db/0006 and db/0008 straight out of SQL Server's permission catalog instead.
        await using var connection = new SqlConnection(_database.AdminConnectionString);
        var denied = await connection.QueryAsync<string>(
            """
        SELECT CONCAT(OBJECT_NAME(p.major_id), ' ', p.permission_name)
        FROM sys.database_permissions AS p
        JOIN sys.database_principals AS r ON r.principal_id = p.grantee_principal_id
        WHERE r.name = N'ledger_app' AND p.state_desc = N'DENY'
        ORDER BY 1;
        """);

        Assert.Equal(
            new[]
            {
                 "AccountBalances DELETE", "AccountBalances INSERT", "AccountBalances UPDATE", "Accounts INSERT",
                 "Postings DELETE", "Postings INSERT", "Postings UPDATE", "Transactions DELETE", "Transactions INSERT", "Transactions UPDATE",
            },
            denied);
    }

    [Fact]
    public async Task Even_the_administrator_cannot_update_a_posting()
    {
        // sa can do almost anything in SQL Server, but an append-only ledger table refuses updates from everyone.
        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAdminAsync(
            "UPDATE dbo.Postings SET AmountMinor = AmountMinor * 2 WHERE TransactionId = @Id;", new { Id = _depositId }));

        Assert.Equal(LedgerErrors.AppendOnlyLedgerTable, error.Number);
        Assert.Equal(DepositAmounts, await _database.AmountsOfAsync(_depositId));
    }

    [Fact]
    public async Task Even_the_administrator_cannot_delete_a_posting()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAdminAsync(
            "DELETE FROM dbo.Postings WHERE TransactionId = @Id;", new { Id = _depositId }));

        Assert.Equal(LedgerErrors.AppendOnlyLedgerTable, error.Number);
        Assert.Equal(DepositAmounts, await _database.AmountsOfAsync(_depositId));
    }

    [Fact]
    public async Task The_ledger_records_which_database_user_inserted_each_posting()
    {
        // Every ledger table gets a ledger view (here dbo.Postings_Ledger). Joined to SQL Server's list of ledger
        // transactions, it shows who inserted each row, whatever the application itself recorded.
        await using var connection = new SqlConnection(_database.AdminConnectionString);
        var insertedBy = await connection.QueryAsync<string>(
            """
            SELECT DISTINCT t.principal_name
            FROM dbo.Postings_Ledger AS p
            JOIN sys.database_ledger_transactions AS t ON t.transaction_id = p.ledger_transaction_id
            WHERE p.TransactionId = @Id;
            """,
            new { Id = _depositId });

        Assert.Equal(SqlServerFixture.AppLogin, Assert.Single(insertedBy));
    }

    [Fact]
    public async Task The_ledger_verifies_against_a_digest_taken_just_now()
    {
        await using var connection = new SqlConnection(_database.AdminConnectionString);

        // A digest is a hash of the newest block of ledger transactions, which covers every row before it.
        // A real system would store it somewhere the database can't write to, such as immutable storage.
        var digest = await connection.ExecuteScalarAsync<string>("EXECUTE sys.sp_generate_database_ledger_digest;");
        Assert.False(string.IsNullOrWhiteSpace(digest));

        // Recomputes every hash and compares. It throws if any row changed since the digest was taken.
        await connection.ExecuteAsync("EXECUTE sys.sp_verify_database_ledger @digests = @Digest;", new { Digest = digest });
    }
}
using Dapper;
using Ledger.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Ledger.Tests.Integration.Support;

/// <summary>
/// One test class's database, with two ways in: as the administrator (sa), and as the application,
/// whose user belongs to the ledger_app role from db/0006_create_app_role.sql.
/// </summary>
public sealed record LedgerDatabase(string AdminConnectionString, string AppConnectionString)
{
    /// <summary>The ledger as the application sees it: only the permissions ledger_app has.</summary>
    public SqlLedger AsApp() => new(AppConnectionString);

    /// <summary>Runs one SQL statement as the application.</summary>
    public Task ExecuteAsAppAsync(string sql, object? parameters = null) => ExecuteAsync(AppConnectionString, sql, parameters);

    /// <summary>Runs one SQL statement as the administrator.</summary>
    public Task ExecuteAsAdminAsync(string sql, object? parameters = null) => ExecuteAsync(AdminConnectionString, sql, parameters);

    /// <summary>The sum of every posting in the database. If it isn't zero, something is broken.</summary>
    public Task<long> TrialBalanceAsync() =>
        ScalarAsync<long>("SELECT COALESCE(SUM(AmountMinor), 0) FROM dbo.Postings;");

    public Task<int> PostingCountAsync() => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Postings;");

    public Task<int> TransactionCountAsync() => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Transactions;");

    /// <summary>A transaction's stored amounts, in posting order, read straight from the table.</summary>
    public async Task<long[]> AmountsOfAsync(long transactionId)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        var amounts = await connection.QueryAsync<long>(
            "SELECT AmountMinor FROM dbo.Postings WHERE TransactionId = @Id ORDER BY Id;", new { Id = transactionId });
        return [.. amounts];
    }

    /// <summary>Runs a query as the administrator and returns the first column of the first row.</summary>
    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        var value = await connection.ExecuteScalarAsync<T>(sql, parameters);
        return value ?? throw new InvalidOperationException($"The query returned no value: {sql}");
    }

    private static async Task ExecuteAsync(string connectionString, string sql, object? parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.ExecuteAsync(sql, parameters);
    }
}
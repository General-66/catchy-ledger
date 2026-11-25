using Ledger.Infrastructure;
using Ledger.Tests.Integration.Support;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Ledger.Tests.Integration;

/// <summary>The migrations build the database the plan describes, and running them again changes nothing.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SchemaTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private LedgerDatabase _database = null!;

    public async Task InitializeAsync() => _database = await sqlServer.DatabaseForAsync(nameof(SchemaTests));

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Transactions_and_Postings_are_append_only_ledger_tables_and_Accounts_is_not()
    {
        const string sql = "SELECT ledger_type_desc FROM sys.tables WHERE object_id = OBJECT_ID(@Table);";

        Assert.Equal("APPEND_ONLY_LEDGER_TABLE", await _database.ScalarAsync<string>(sql, new { Table = "dbo.Transactions" }));
        Assert.Equal("APPEND_ONLY_LEDGER_TABLE", await _database.ScalarAsync<string>(sql, new { Table = "dbo.Postings" }));
        Assert.Equal("NON_LEDGER_TABLE", await _database.ScalarAsync<string>(sql, new { Table = "dbo.Accounts" }));
    }

    [Fact]
    public async Task Read_committed_snapshot_and_snapshot_isolation_are_on()
    {
        Assert.True(await _database.ScalarAsync<bool>(
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();"));
        Assert.Equal("ON", await _database.ScalarAsync<string>(
            "SELECT snapshot_isolation_state_desc FROM sys.databases WHERE database_id = DB_ID();"));
    }

    [Fact]
    public async Task Every_script_in_db_has_run_exactly_once()
    {
        // DbUp writes one row per script it has run into dbo.SchemaVersions.
        var scriptsRun = await _database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SchemaVersions;");
        var scriptsInDb = typeof(LedgerMigrator).Assembly.GetManifestResourceNames()
            .Count(name => name.StartsWith(LedgerMigrator.ScriptPrefix, StringComparison.Ordinal));

        Assert.Equal(8, scriptsInDb);
        Assert.Equal(scriptsInDb, scriptsRun);
    }

    [Fact]
    public async Task Running_the_migrations_again_changes_nothing()
    {
        LedgerMigrator.Migrate(_database.AdminConnectionString);

        Assert.Equal(8, await _database.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SchemaVersions;"));
    }

    [Fact]
    public async Task The_four_house_accounts_are_seeded_with_ids_1_to_4()
    {
        await using var connection = new SqlConnection(_database.AdminConnectionString);
        var accounts = await connection.QueryAsync<string>(
            "SELECT CONCAT(Id, ' ', Code, ' ', Name, ' ', Type) FROM dbo.Accounts WHERE Id <= 4 ORDER BY Id;");

        Assert.Equal(
            new[] { "1 1000 Bank Asset", "2 2100 Open Bets Liability", "3 4000 Betting Revenue Income", "4 5000 Payouts Expense" },
            accounts);
    }
}
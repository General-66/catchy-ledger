using System.Collections.Concurrent;
using Dapper;
using Ledger.Infrastructure;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Ledger.Tests.Integration.Support;

/// <summary>
/// One throwaway SQL Server container for the whole test run, and a fresh, fully migrated database
/// for each test class that asks for one. Ledger tables can't be emptied, so tests never share books
/// with another class.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>The same image as compose.yaml, so the tests and your laptop run the same SQL Server build.</summary>
    public const string Image = "mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04";

    /// <summary>The login the tests use to act as the application. Its user in each database has the same name.</summary>
    public const string AppLogin = "ledger_app_test";

    private readonly MsSqlContainer _container = new MsSqlBuilder(Image).Build();

    // A new password every run: upper and lower case, digits and symbols, as SQL Server's policy requires.
    private readonly string _appPassword = $"App_{Guid.NewGuid():N}!";

    private readonly ConcurrentDictionary<string, Lazy<Task<LedgerDatabase>>> _databases = new();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // A server login for the app, like the one the API gets in Phase 3. Each database maps a user to it.
        await using var connection = new SqlConnection(_container.GetConnectionString());
        await connection.ExecuteAsync($"CREATE LOGIN {AppLogin} WITH PASSWORD = '{_appPassword}';");
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// The database for one test class, created and migrated the first time the class asks.
    /// Every test in the class gets the same database, so each test creates its own accounts.
    /// </summary>
    public Task<LedgerDatabase> DatabaseForAsync(string testClassName) =>
        _databases.GetOrAdd(testClassName, name => new Lazy<Task<LedgerDatabase>>(() => CreateDatabaseAsync(name))).Value;

    private async Task<LedgerDatabase> CreateDatabaseAsync(string testClassName)
    {
        var admin = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = $"Ledger_{testClassName}",
        };

        // Creates the database, then runs every script in db/ against it: exactly what Phase 3 will do for real.
        LedgerMigrator.Migrate(admin.ConnectionString);

        await using (var connection = new SqlConnection(admin.ConnectionString))
        {
            await connection.ExecuteAsync($"""
                CREATE USER {AppLogin} FOR LOGIN {AppLogin};
                ALTER ROLE ledger_app ADD MEMBER {AppLogin};
                """);
        }

        var app = new SqlConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = AppLogin,
            Password = _appPassword,
        };

        return new LedgerDatabase(admin.ConnectionString, app.ConnectionString);
    }
}
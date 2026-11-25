using System.Collections.Concurrent;
using Ledger.Infrastructure;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Ledger.Tests.Integration.Support;

/// <summary>
/// One throwaway SQL Server container for the whole test run, and a fresh, fully migrated database
/// for each test class that asks for one. Ledger tables can't be emptied, so tests never share books
/// with another class. Each class can also have the whole API, running in memory against its database.
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

    private readonly ConcurrentDictionary<string, Lazy<Task<LedgerApiFactory>>> _apis = new();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var api in _apis.Values.Where(api => api.IsValueCreated))
        {
            await (await api.Value).DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    /// The database for one test class, created and migrated the first time the class asks.
    /// Every test in the class gets the same database, so each test creates its own accounts.
    /// </summary>
    public Task<LedgerDatabase> DatabaseForAsync(string testClassName) =>
        _databases.GetOrAdd(testClassName, name => new Lazy<Task<LedgerDatabase>>(() => Task.Run(() => CreateDatabase(name)))).Value;

    /// <summary>
    /// The API for one test class, running in memory against that class's database, started the first time the class
    /// asks. Every test in the class shares it, so each test opens its own accounts.
    /// </summary>
    public Task<LedgerApiFactory> ApiForAsync(string testClassName) =>
        _apis.GetOrAdd(testClassName, name => new Lazy<Task<LedgerApiFactory>>(async () =>
            new LedgerApiFactory((await DatabaseForAsync(name)).AppConnectionString))).Value;

    private LedgerDatabase CreateDatabase(string testClassName)
    {
        var admin = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = $"Ledger_{testClassName}",
        };

        var app = new SqlConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = AppLogin,
            Password = _appPassword,
            MaxPoolSize = 200,
        };

        // Exactly what Ledger.Migrator does on your laptop: create the database, run every script in db/,
        // then create the app's login and add its user to the ledger_app role.
        LedgerMigrator.Migrate(admin.ConnectionString);
        LedgerMigrator.EnsureAppUser(admin.ConnectionString, app.ConnectionString);

        return new LedgerDatabase(admin.ConnectionString, app.ConnectionString);
    }
}
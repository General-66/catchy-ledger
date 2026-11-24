using Ledger.Infrastructure;
using Microsoft.Extensions.Configuration;

// Builds or upgrades the ledger database, then makes sure the API's own login can use it.
// On your laptop both connection strings come from this project's User Secrets. In CI and Azure (Phase 6)
// they come from environment variables named ConnectionStrings__LedgerAdmin and ConnectionStrings__Ledger.
var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

// The administrator (sa on your laptop): allowed to create the database, its tables and logins.
var adminConnectionString = configuration.GetConnectionString("LedgerAdmin")
    ?? throw new InvalidOperationException("ConnectionStrings:LedgerAdmin is missing. Add it with Manage User Secrets on Ledger.Migrator.");

// The API's own login, which this program creates: it can only read the books and call the two procedures.
var appConnectionString = configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("ConnectionStrings:Ledger is missing. Add it with Manage User Secrets on Ledger.Migrator.");

LedgerMigrator.Migrate(adminConnectionString);
LedgerMigrator.EnsureAppUser(adminConnectionString, appConnectionString);

Console.WriteLine();
Console.WriteLine("Done: the database is up to date, and the API's login can use it.");
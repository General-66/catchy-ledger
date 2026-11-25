using System.Text.RegularExpressions;
using Dapper;
using Ledger.Infrastructure;
using Ledger.RaceLab;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

// The race lab. It builds a throwaway copy of the ledger database, runs the same two races against each way of
// handling concurrency, writes what happened to docs/race-lab, and drops the copy again.
// Its connection string comes from this project's User Secrets, or an environment variable named
// ConnectionStrings__LedgerAdmin, the same setting the migrator uses.
var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var adminConnectionString = configuration.GetConnectionString("LedgerAdmin")
    ?? throw new InvalidOperationException("ConnectionStrings:LedgerAdmin is missing. Add it with Manage User Secrets on Ledger.RaceLab.");

var startedAt = DateTimeOffset.Now;
var databaseName = $"RaceLab_{startedAt:yyyyMMdd_HHmmss}";
var lab = new SqlConnectionStringBuilder(adminConnectionString)
{
    InitialCatalog = databaseName,
    // A hundred bets at once need a hundred connections at once, which is the pool's default limit. Leave room.
    MaxPoolSize = 300,
}.ConnectionString;

Console.WriteLine($"Building {databaseName}: every script in db/, then the lab's own procedures.");
LedgerMigrator.Migrate(lab);
await LabDatabase.InstallLabProceduresAsync(lab);
await LabDatabase.WarmUpConnectionPoolAsync(lab, Scenarios.RaceBets);

var results = new List<ApproachResult>();
foreach (var approach in Approach.All(lab))
{
    Console.WriteLine();
    Console.WriteLine($"{approach.Name}");

    var race = await Scenarios.TheRaceAsync(lab, approach);
    Console.WriteLine($"  The race:      {race.Accepted} accepted, {race.Refused} refused, {race.GaveUp + race.Failed} errors, {race.Deadlocks} deadlocks, {race.Conflicts} conflicts, {race.Elapsed.TotalSeconds:0.00} s");

    var evening = await Scenarios.BusyEveningAsync(lab, approach);
    Console.WriteLine($"  A busy evening: {evening.Accepted} accepted, {evening.GaveUp + evening.Failed} errors, {evening.Deadlocks} deadlocks, {evening.Conflicts} conflicts, {evening.Elapsed.TotalSeconds:0.00} s");

    results.Add(new ApproachResult(approach, race, evening));
}

var environment = await LabEnvironment.DescribeAsync(lab, startedAt);
var path = Report.Write(environment, results);
await LabDatabase.DropAsync(adminConnectionString, databaseName);

Console.WriteLine();
Console.WriteLine($"Done. The results are in {path}");

/// <summary>Setting up and clearing away the lab's throwaway database.</summary>
internal static partial class LabDatabase
{
    /// <summary>Runs LabProcedures.sql, which is compiled into the program, one batch at a time.</summary>
    public static async Task InstallLabProceduresAsync(string connectionString)
    {
        await using var stream = typeof(LabDatabase).Assembly.GetManifestResourceStream("Ledger.RaceLab.LabProcedures.sql")
            ?? throw new InvalidOperationException("LabProcedures.sql isn't compiled in. Check its EmbeddedResource line in Ledger.RaceLab.csproj.");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync();

        // GO isn't SQL: it's how a script marks where one batch ends and the next begins, so it's split off here.
        await using var connection = new SqlConnection(connectionString);
        foreach (var batch in GoLine().Split(script).Where(batch => !string.IsNullOrWhiteSpace(batch)))
        {
            await connection.ExecuteAsync(batch);
        }
    }

    /// <summary>Opens and hands back enough connections for the biggest race, so the first approach doesn't pay to fill the pool.</summary>
    public static async Task WarmUpConnectionPoolAsync(string connectionString, int connections)
    {
        await Task.WhenAll(Enumerable.Range(0, connections).Select(async _ =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }));
    }

    /// <summary>Drops the throwaway database. If that fails, delete it by hand in SQL Server Object Explorer.</summary>
    public static async Task DropAsync(string adminConnectionString, string databaseName)
    {
        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(adminConnectionString) { InitialCatalog = "master" }.ConnectionString;

        try
        {
            await using var connection = new SqlConnection(master);
            await connection.ExecuteAsync($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];");
        }
        catch (SqlException error)
        {
            Console.WriteLine($"Couldn't drop {databaseName} ({error.Message}). Delete it in SQL Server Object Explorer.");
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
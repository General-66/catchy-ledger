using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Dapper;
using Ledger.Domain;
using Microsoft.Data.SqlClient;

namespace Ledger.RaceLab;

/// <summary>Where a run happened. The plan asks for every number to say so: lock behaviour differs between servers.</summary>
internal sealed record LabEnvironment(DateTimeOffset StartedAt, string Where, string SqlServer, string OptimizedLocking, string Computer)
{
    public static async Task<LabEnvironment> DescribeAsync(string connectionString, DateTimeOffset startedAt)
    {
        const string sql = """
            SELECT CAST(SERVERPROPERTY('EngineEdition') AS int) AS EngineEdition,
                   CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS ProductVersion,
                   CAST(SERVERPROPERTY('ProductUpdateLevel') AS nvarchar(128)) AS UpdateLevel,
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS Edition,
                   CAST(DATABASEPROPERTYEX(DB_NAME(), 'IsOptimizedLockingOn') AS int) AS OptimizedLocking;
            """;

        await using var connection = new SqlConnection(connectionString);
        var server = await connection.QuerySingleAsync<ServerRow>(sql);
        var dataSource = new SqlConnectionStringBuilder(connectionString).DataSource;

        // SERVERPROPERTY('EngineEdition') is 5 for Azure SQL Database and 8 for Azure SQL Managed Instance.
        var where = server.EngineEdition switch
        {
            5 => "Azure SQL Database",
            8 => "Azure SQL Managed Instance",
            _ when dataSource.StartsWith("127.0.0.1", StringComparison.Ordinal) || dataSource.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                => $"Laptop: SQL Server on this computer, at {dataSource}",
            _ => $"SQL Server at {dataSource}",
        };

        return new LabEnvironment(
            startedAt,
            where,
            $"{server.ProductVersion} {server.UpdateLevel} ({server.Edition})".Replace("  ", " ", StringComparison.Ordinal),
            server.OptimizedLocking switch { 1 => "On", 0 => "Off", _ => "Not available (SQL Server 2022 doesn't have it)" },
            $"{Environment.ProcessorCount} logical processors, {RuntimeInformation.OSDescription}");
    }

    private sealed class ServerRow
    {
        public int EngineEdition { get; init; }

        public string ProductVersion { get; init; } = "";

        public string? UpdateLevel { get; init; }

        public string Edition { get; init; } = "";

        public int? OptimizedLocking { get; init; }
    }
}

/// <summary>One approach's results in both scenarios.</summary>
internal sealed record ApproachResult(Approach Approach, ScenarioResult Race, ScenarioResult Evening);

/// <summary>Writes a run's results as Markdown, into docs/race-lab in the repo, ready to commit and to quote in an ADR.</summary>
internal static class Report
{
    public static string Write(LabEnvironment environment, IReadOnlyList<ApproachResult> results)
    {
        var folder = Path.Combine(FindRepoRoot() ?? Directory.GetCurrentDirectory(), "docs", "race-lab");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, $"{environment.StartedAt:yyyy-MM-dd-HHmm}.md");
        File.WriteAllText(path, Format(environment, results));
        return path;
    }

    public static string Format(LabEnvironment environment, IReadOnlyList<ApproachResult> results)
    {
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');

        Line($"# Race lab, {environment.StartedAt.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)}");
        Line();
        Line($"- **Where it ran:** {environment.Where}");
        Line($"- **SQL Server:** {environment.SqlServer}");
        Line($"- **Optimized locking:** {environment.OptimizedLocking}");
        Line($"- **Computer running the lab:** {environment.Computer}");
        Line("- **Retries:** up to 10 attempts per bet, for deadlocks (1205) and conflicts (50030). The first wait is about 20 ms, then each doubles, up to 1 s, half of it random.");
        Line();

        Line($"## The race: {Scenarios.RaceBets} {Short(Scenarios.RaceStakeMinor)} bets at once against a {Short(Scenarios.RaceDepositMinor)} wallet");
        Line();
        Line($"Correct is exactly 1 bet accepted, {Scenarios.RaceBets - 1} refused, the wallet ending at {Rand(Scenarios.RaceDepositMinor - Scenarios.RaceStakeMinor)}, and the stored balances agreeing with the postings.");
        Line();
        Line("| Approach | Accepted | Refused | Gave up | Other errors | Deadlocks | Conflicts | Time | Wallet ends at | Balances agree |");
        Line("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |");
        foreach (var (approach, race, _) in results)
        {
            var wallet = race.LowestWalletMinor < 0 ? $"{Rand(race.LowestWalletMinor)}, overdrawn" : Rand(race.LowestWalletMinor);
            Line($"| {approach.Name} | {race.Accepted} | {race.Refused} | {race.GaveUp} | {race.Failed} | {race.Deadlocks} | {race.Conflicts} | {Seconds(race.Elapsed)} | {wallet} | {YesNo(race.BalancesAgree)} |");
        }

        Line();
        Line($"## A busy evening: {Scenarios.EveningBets} {Short(Scenarios.EveningStakeMinor)} bets from {Scenarios.EveningWallets} wallets of {Short(Scenarios.EveningDepositMinor)}, {Scenarios.EveningAtOnce} at a time");
        Line();
        Line("No wallet can run out, so every bet should be accepted. Every bet also moves the one shared Open Bets account.");
        Line();
        Line("| Approach | Bets per second | Time | Accepted | Gave up | Other errors | Deadlocks | Conflicts | Overdrawn wallets | Balances agree |");
        Line("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var (approach, _, evening) in results)
        {
            var perSecond = (evening.Accepted / evening.Elapsed.TotalSeconds).ToString("N0", CultureInfo.InvariantCulture);
            Line($"| {approach.Name} | {perSecond} | {Seconds(evening.Elapsed)} | {evening.Accepted} | {evening.GaveUp} | {evening.Failed} | {evening.Deadlocks} | {evening.Conflicts} | {evening.OverdrawnWallets} | {YesNo(evening.BalancesAgree)} |");
        }

        Line();
        Line("## How each approach works");
        Line();
        foreach (var (approach, _, _) in results)
        {
            Line($"- **{approach.Name}:** {approach.HowItWorks}");
        }

        var failures = results
            .SelectMany(result => new[] { (result.Approach.Name, result.Race.FirstFailure), (result.Approach.Name, result.Evening.FirstFailure) })
            .Where(failure => failure.FirstFailure is not null)
            .Distinct()
            .ToList();
        if (failures.Count > 0)
        {
            Line();
            Line("## Other errors");
            Line();
            foreach (var (name, failure) in failures)
            {
                Line($"- **{name}:** {failure}");
            }
        }

        return text.ToString();
    }

    private static string Rand(long minorUnits) => new Money(minorUnits, Currency.ZAR).ToString();

    // "R80" for a whole number of rand, as people say it in a heading.
    private static string Short(long minorUnits) => $"R{(minorUnits / 100m).ToString("#,0.##", CultureInfo.InvariantCulture)}";

    private static string Seconds(TimeSpan elapsed) => $"{elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s";

    private static string YesNo(bool value) => value ? "Yes" : "**No**";

    // The repo's root is the folder holding Ledger.slnx. The lab runs from tools/Ledger.RaceLab/bin/Debug/net10.0.
    private static string? FindRepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Ledger.slnx")))
            {
                return folder.FullName;
            }
        }

        return null;
    }
}
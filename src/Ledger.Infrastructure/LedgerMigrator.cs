using DbUp;

namespace Ledger.Infrastructure;

/// <summary>
/// Brings a database up to date by running the numbered scripts from db/, each one once, in order.
/// DbUp records every script it has run in a dbo.SchemaVersions table, so running this again only
/// runs the scripts added since.
/// </summary>
public static class LedgerMigrator
{
    /// <summary>Every script's resource name starts with this. See the EmbeddedResource line in the .csproj.</summary>
    public const string ScriptPrefix = "Ledger.Migrations.";

    /// <summary>Creates the database if it doesn't exist, then runs every script it hasn't run yet.</summary>
    public static void Migrate(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        EnsureDatabase.For.SqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(LedgerMigrator).Assembly,
                name => name.StartsWith(ScriptPrefix, StringComparison.Ordinal))
            // DbUp normally replaces $name$ in scripts with variables. The scripts use none, so switch it off
            // rather than risk a $ in a comment or string being read as one.
            .WithVariablesDisabled()
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed in script {result.ErrorScript?.Name}. Nothing after it ran.",
                result.Error);
        }
    }
}
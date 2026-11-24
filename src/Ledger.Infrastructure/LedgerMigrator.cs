using DbUp;
using Dapper;
using Microsoft.Data.SqlClient;

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

    /// <summary>
    /// Makes sure the application's own login exists with the password in its connection string, and that its user in
    /// this database belongs to the ledger_app role from db/0006. Passwords never go in the scripts, so this runs after
    /// them, from C#. It's safe to run again: it creates only what's missing, and changes the password only if it differs.
    /// </summary>
    public static void EnsureAppUser(string adminConnectionString, string appConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(appConnectionString);

        var admin = new SqlConnectionStringBuilder(adminConnectionString);
        var app = new SqlConnectionStringBuilder(appConnectionString);
        var login = app.UserID;

        if (!string.Equals(admin.InitialCatalog, app.InitialCatalog, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The two connection strings name different databases, '{admin.InitialCatalog}' and '{app.InitialCatalog}'. Use the same Database= in both.");
        }

        // The login's name ends up inside dynamic SQL, so allow only plain names. QUOTENAME below is the second guard.
        if (login.Length is 0 or > 128 || !char.IsAsciiLetter(login[0]) || !login.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidOperationException(
                $"The app's User Id '{login}' must be letters, digits and underscores, starting with a letter, such as ledger_api.");
        }

        if (string.Equals(login, admin.UserID, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The app must not connect as the administrator. Give it its own User Id, such as ledger_api.");
        }

        if (app.Password.Length is 0 or > 128)
        {
            throw new InvalidOperationException("The app's Password must be 1 to 128 characters.");
        }

        // CREATE LOGIN, CREATE USER and ALTER ROLE can't take parameters, so each is built as a string and run with
        // sp_executesql. QUOTENAME wraps the name in [brackets] and the password in 'quotes', escaping anything inside,
        // so neither can break out of the statement.
        const string sql = """
            DECLARE @Sql nvarchar(max) = NULL;

            -- PWDCOMPARE checks a password against the stored hash, so an unchanged password is left alone.
            IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = @Login)
                SET @Sql = N'CREATE LOGIN ' + QUOTENAME(@Login) + N' WITH PASSWORD = ' + QUOTENAME(@Password, N'''') + N';';
            ELSE IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = @Login AND PWDCOMPARE(@Password, password_hash) = 1)
                SET @Sql = N'ALTER LOGIN ' + QUOTENAME(@Login) + N' WITH PASSWORD = ' + QUOTENAME(@Password, N'''') + N';';

            IF @Sql IS NOT NULL
                EXECUTE sys.sp_executesql @Sql;

            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @Login)
            BEGIN
                SET @Sql = N'CREATE USER ' + QUOTENAME(@Login) + N' FOR LOGIN ' + QUOTENAME(@Login) + N';';
                EXECUTE sys.sp_executesql @Sql;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.database_role_members AS m
                JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
                JOIN sys.database_principals AS u ON u.principal_id = m.member_principal_id
                WHERE r.name = N'ledger_app' AND u.name = @Login)
            BEGIN
                SET @Sql = N'ALTER ROLE ledger_app ADD MEMBER ' + QUOTENAME(@Login) + N';';
                EXECUTE sys.sp_executesql @Sql;
            END;
            """;

        using var connection = new SqlConnection(admin.ConnectionString);
        connection.Execute(sql, new { Login = login, app.Password });
    }
}
namespace Ledger.Tests.Integration.Support;

/// <summary>
/// Every test class marked [Collection(SqlServerCollection.Name)] shares one <see cref="SqlServerFixture"/>,
/// so the container starts once per test run, not once per class. xUnit runs the classes in a collection one
/// after another, never at the same time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
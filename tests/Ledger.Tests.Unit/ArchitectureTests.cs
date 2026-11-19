using Ledger.Domain;

namespace Ledger.Tests.Unit;

public class ArchitectureTests
{
    [Fact]
    public void Domain_does_not_depend_on_any_other_ledger_project()
    {
        var domainAssembly = typeof(DomainAssemblyMarker).Assembly;

        var ledgerReferences = domainAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("Ledger.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(ledgerReferences);
    }
}
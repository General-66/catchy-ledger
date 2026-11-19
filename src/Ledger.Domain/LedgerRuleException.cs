namespace Ledger.Domain;

/// <summary>
/// Thrown when something would break one of the ledger's accounting rules
/// </summary>
public sealed class LedgerRuleException(LedgerRule rule, string message) : Exception(message)
{
    public LedgerRule Rule { get; } = rule;
}
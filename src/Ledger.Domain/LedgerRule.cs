namespace Ledger.Domain;

/// <summary>The accounting rules the domain enforces. Every rejection names the rule it broke.</summary>
public enum LedgerRule
{
    CurrencyMismatch,
    TooFewPostings,
    ZeroAmountPosting,
    MixedCurrencies,
    AccountCurrencyMismatch,
    Unbalanced,
}
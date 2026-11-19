namespace Ledger.Domain;

/// <summary>
/// One business event, such as "customer deposits R500", recorded as postings that sum to zero.
/// The only way to make one is <see cref="Create"/>, which refuses anything that breaks the rules.
/// So every Transaction object that exists is balanced.
/// </summary>
public sealed class Transaction
{
    private Transaction(string description, DateOnly effectiveDate, Currency currency, IReadOnlyList<Posting> postings)
    {
        Description = description;
        EffectiveDate = effectiveDate;
        Currency = currency;
        Postings = postings;
    }

    /// <summary>What happened, in words: "Customer deposit", "Bet placed".</summary>
    public string Description { get; }

    /// <summary>The day the event counts for in the books, which can differ from the day it was recorded.</summary>
    public DateOnly EffectiveDate { get; }

    /// <summary>The one currency every posting is in.</summary>
    public Currency Currency { get; }

    /// <summary>The lines of the transaction. Read-only: nothing can change them after Create.</summary>
    public IReadOnlyList<Posting> Postings { get; }

    /// <summary>
    /// Checks the postings against every rule and returns the transaction, or throws a
    /// <see cref="LedgerRuleException"/> naming the first rule broken.
    /// </summary>
    public static Transaction Create(string description, DateOnly effectiveDate, params IEnumerable<Posting> postings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(postings);

        // Copy the postings first, so the caller can't change the list after the checks have passed.
        Posting[] lines = [.. postings];
        if (Array.Exists(lines, line => line is null))
        {
            throw new ArgumentException("A posting is null.", nameof(postings));
        }

        // Money always moves from somewhere to somewhere, so one line can't be a transaction.
        if (lines.Length < 2)
        {
            throw new LedgerRuleException(
                LedgerRule.TooFewPostings,
                $"A transaction needs at least two postings; this one has {lines.Length}.");
        }

        // A zero line moves nothing. Without this rule, two zero lines would pass as a "balanced" transaction.
        var zeroLine = Array.Find(lines, line => line.Amount.IsZero);
        if (zeroLine is not null)
        {
            throw new LedgerRuleException(
                LedgerRule.ZeroAmountPosting,
                $"The posting to {zeroLine.Account.Code} is zero. Every posting must move money.");
        }

        // R100 and $100 can't cancel out. Converting currencies is a transaction of its own.
        var currency = lines[0].Amount.Currency;
        var otherCurrency = Array.Find(lines, line => line.Amount.Currency != currency);
        if (otherCurrency is not null)
        {
            throw new LedgerRuleException(
                LedgerRule.MixedCurrencies,
                $"Postings are in {currency} and {otherCurrency.Amount.Currency}. A transaction uses one currency.");
        }

        // Every amount must be in its account's currency: no dollars in a rand wallet.
        var wrongAccount = Array.Find(lines, line => line.Account.Currency != currency);
        if (wrongAccount is not null)
        {
            throw new LedgerRuleException(
                LedgerRule.AccountCurrencyMismatch,
                $"Account {wrongAccount.Account.Code} holds {wrongAccount.Account.Currency}, not {currency}.");
        }

        // The double-entry rule: debits (+) and credits (-) cancel out exactly.
        var sum = lines.Aggregate(Money.Zero(currency), (total, line) => total + line.Amount);
        if (!sum.IsZero)
        {
            throw new LedgerRuleException(
                LedgerRule.Unbalanced,
                $"Postings must sum to zero, but they sum to {sum}.");
        }

        return new Transaction(description, effectiveDate, currency, Array.AsReadOnly(lines));
    }
}
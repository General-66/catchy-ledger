using Ledger.Domain;

namespace Ledger.Tests.Unit.Support;

/// <summary>
/// A tiny in-memory set of books for tests: a list of posted transactions.
/// It stands in for the database until Phase 2.
/// </summary>
internal sealed class Books
{
    private readonly List<Transaction> _transactions = [];

    public void Post(Transaction transaction) => _transactions.Add(transaction);

    /// <summary>The balance as the database stores it: every posting on the account added up, signs included.</summary>
    public Money StoredBalance(Account account) =>
        _transactions
            .SelectMany(transaction => transaction.Postings)
            .Where(posting => posting.Account == account)
            .Aggregate(Money.Zero(account.Currency), (total, posting) => total + posting.Amount);

    /// <summary>Every posting ever recorded in one currency, added up. If this isn't zero, something is broken.</summary>
    public Money TrialBalance(Currency currency) =>
        _transactions
            .SelectMany(transaction => transaction.Postings)
            .Where(posting => posting.Amount.Currency == currency)
            .Aggregate(Money.Zero(currency), (total, posting) => total + posting.Amount);
}
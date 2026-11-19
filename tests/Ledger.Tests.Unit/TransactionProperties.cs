using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Ledger.Domain;
using Ledger.Tests.Unit.Support;

namespace Ledger.Tests.Unit;

/// <summary>
/// Property tests: instead of one hand-picked example, FsCheck generates hundreds of random transactions and checks that a rule holds for every one of them.
/// </summary>
public class TransactionProperties
{
    private static readonly DateOnly Day = new(2026, 11, 19);

    [Property(MaxTest = 1000)]
    public Property Balanced_postings_are_always_accepted_and_sum_to_zero() =>
        Prop.ForAll(LedgerGenerators.BalancedPostings().ToArbitrary(), postings =>
        {
            var transaction = Transaction.Create("Generated", Day, postings);

            return transaction.Postings.Count == postings.Length
                && transaction.Postings.Sum(posting => posting.Amount.AmountMinor) == 0;
        });

    [Property(MaxTest = 1000)]
    public Property Unbalanced_postings_are_always_rejected() =>
        Prop.ForAll(LedgerGenerators.UnbalancedPostings().ToArbitrary(), postings =>
        {
            var error = Assert.Throws<LedgerRuleException>(() => Transaction.Create("Generated", Day, postings));

            Assert.Equal(LedgerRule.Unbalanced, error.Rule);
        });

    [Property(MaxTest = 200)]
    public Property The_books_balance_after_any_number_of_transactions() =>
        Prop.ForAll(LedgerGenerators.BalancedPostings().ArrayOf().ToArbitrary(), batches =>
        {
            var books = new Books();
            foreach (var postings in batches)
            {
                books.Post(Transaction.Create("Generated", Day, postings));
            }

            return books.TrialBalance(Currency.ZAR).IsZero;
        });
}
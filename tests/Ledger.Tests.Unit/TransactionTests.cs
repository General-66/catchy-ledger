using Ledger.Domain;
using static Ledger.Tests.Unit.Support.BettingChart;
using static Ledger.Tests.Unit.Support.TestMoney;

namespace Ledger.Tests.Unit;

public class TransactionTests
{
    private static readonly DateOnly Day = new(2026, 11, 19);

    private static readonly Account DollarBank =
        new(10, "1010", "Bank (USD)", AccountType.Asset, Currency.USD, allowOverdraft: true);

    [Fact]
    public void Debit_stores_a_positive_amount_and_credit_a_negative_one()
    {
        var debit = Posting.Debit(Bank, Zar(500_00));
        var credit = Posting.Credit(CustomerWallet, Zar(500_00));

        Assert.Equal(Zar(500_00), debit.Amount);
        Assert.True(debit.IsDebit);
        Assert.Equal(Zar(-500_00), credit.Amount);
        Assert.True(credit.IsCredit);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-100_00L)]
    public void Debit_and_credit_take_only_positive_amounts(long cents)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Posting.Debit(Bank, Zar(cents)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Posting.Credit(Bank, Zar(cents)));
    }

    [Fact]
    public void A_balanced_transaction_is_created()
    {
        var transaction = Transaction.Create(
            "Customer deposit",
            Day,
            Posting.Debit(Bank, Zar(500_00)),
            Posting.Credit(CustomerWallet, Zar(500_00)));

        Assert.Equal(2, transaction.Postings.Count);
        Assert.Equal(Currency.ZAR, transaction.Currency);
        Assert.Equal(Day, transaction.EffectiveDate);
    }

    [Fact]
    public void No_postings_at_all_is_rejected()
    {
        var error = Assert.Throws<LedgerRuleException>(() => Transaction.Create("Nothing", Day));

        Assert.Equal(LedgerRule.TooFewPostings, error.Rule);
    }

    [Fact]
    public void A_single_posting_is_rejected()
    {
        var error = Assert.Throws<LedgerRuleException>(
            () => Transaction.Create("One line", Day, Posting.Debit(Bank, Zar(500_00))));

        Assert.Equal(LedgerRule.TooFewPostings, error.Rule);
    }

    [Fact]
    public void A_zero_posting_is_rejected_even_though_the_total_is_zero()
    {
        var error = Assert.Throws<LedgerRuleException>(() => Transaction.Create(
            "Moves nothing",
            Day,
            new Posting(Bank, Zar(0)),
            new Posting(CustomerWallet, Zar(0))));

        Assert.Equal(LedgerRule.ZeroAmountPosting, error.Rule);
    }

    [Fact]
    public void Postings_in_two_currencies_are_rejected()
    {
        var error = Assert.Throws<LedgerRuleException>(() => Transaction.Create(
            "Rand in, dollars out",
            Day,
            Posting.Debit(DollarBank, Usd(100_00)),
            Posting.Credit(CustomerWallet, Zar(100_00))));

        Assert.Equal(LedgerRule.MixedCurrencies, error.Rule);
    }

    [Fact]
    public void An_amount_in_a_different_currency_from_its_account_is_rejected()
    {
        // Both amounts are in dollars, but the wallet holds rand.
        var error = Assert.Throws<LedgerRuleException>(() => Transaction.Create(
            "Dollars into a rand wallet",
            Day,
            Posting.Debit(DollarBank, Usd(100_00)),
            Posting.Credit(CustomerWallet, Usd(100_00))));

        Assert.Equal(LedgerRule.AccountCurrencyMismatch, error.Rule);
    }

    [Fact]
    public void Postings_that_do_not_sum_to_zero_are_rejected()
    {
        var error = Assert.Throws<LedgerRuleException>(() => Transaction.Create(
            "One cent short",
            Day,
            Posting.Debit(Bank, Zar(500_00)),
            Posting.Credit(CustomerWallet, Zar(499_99))));

        Assert.Equal(LedgerRule.Unbalanced, error.Rule);
        Assert.Contains("ZAR 0.01", error.Message);
    }

    [Fact]
    public void A_description_is_required()
    {
        Assert.Throws<ArgumentException>(() => Transaction.Create(
            "  ",
            Day,
            Posting.Debit(Bank, Zar(500_00)),
            Posting.Credit(CustomerWallet, Zar(500_00))));
    }

    [Fact]
    public void Changing_the_original_list_afterwards_does_not_change_the_transaction()
    {
        List<Posting> postings = [Posting.Debit(Bank, Zar(500_00)), Posting.Credit(CustomerWallet, Zar(500_00))];
        var transaction = Transaction.Create("Customer deposit", Day, postings);

        postings.Add(Posting.Debit(Bank, Zar(1_000_000_00)));

        Assert.Equal(2, transaction.Postings.Count);
    }

    [Fact]
    public void The_postings_of_a_transaction_cannot_be_edited()
    {
        var transaction = Transaction.Create(
            "Customer deposit",
            Day,
            Posting.Debit(Bank, Zar(500_00)),
            Posting.Credit(CustomerWallet, Zar(500_00)));

        var asList = (IList<Posting>)transaction.Postings;

        Assert.Throws<NotSupportedException>(() => asList[0] = Posting.Debit(Bank, Zar(1_000_000_00)));
    }
}
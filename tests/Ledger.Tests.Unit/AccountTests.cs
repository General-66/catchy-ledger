using Ledger.Domain;
using static Ledger.Tests.Unit.Support.BettingChart;
using static Ledger.Tests.Unit.Support.TestMoney;

namespace Ledger.Tests.Unit;

public class AccountTests
{
    [Theory]
    [InlineData(AccountType.Asset, NormalSide.Debit)]
    [InlineData(AccountType.Expense, NormalSide.Debit)]
    [InlineData(AccountType.Liability, NormalSide.Credit)]
    [InlineData(AccountType.Equity, NormalSide.Credit)]
    [InlineData(AccountType.Income, NormalSide.Credit)]
    public void The_normal_side_follows_from_the_account_type(AccountType type, NormalSide expected)
    {
        var account = new Account(1, "9999", "Any account", type, Currency.ZAR, allowOverdraft: true);

        Assert.Equal(expected, account.NormalSide);
    }

    [Fact]
    public void A_debit_side_account_reads_its_stored_balance_as_it_is()
    {
        Assert.Equal(Zar(500_00), Bank.BalanceOnNormalSide(Zar(500_00)));
    }

    [Fact]
    public void A_credit_side_account_reads_its_stored_balance_with_the_sign_flipped()
    {
        Assert.Equal(Zar(500_00), CustomerWallet.BalanceOnNormalSide(Zar(-500_00)));
    }

    [Fact]
    public void An_overdrawn_wallet_reads_as_a_negative_balance()
    {
        // A wallet stored as +2000 has had R20 more taken out than was put in.
        Assert.Equal(Zar(-20_00), CustomerWallet.BalanceOnNormalSide(Zar(20_00)));
    }

    [Fact]
    public void Reading_a_balance_in_another_currency_is_rejected()
    {
        var error = Assert.Throws<LedgerRuleException>(() => Bank.BalanceOnNormalSide(Usd(500_00)));

        Assert.Equal(LedgerRule.AccountCurrencyMismatch, error.Rule);
    }

    [Fact]
    public void An_account_needs_a_code()
    {
        Assert.Throws<ArgumentException>(
            () => new Account(1, " ", "Bank", AccountType.Asset, Currency.ZAR, allowOverdraft: true));
    }
}
using Ledger.Domain;
using static Ledger.Tests.Unit.Support.TestMoney;

namespace Ledger.Tests.Unit;

public class MoneyTests
{
    [Fact]
    public void Floating_point_gets_ten_plus_twenty_cents_wrong_but_minor_units_do_not()
    {
        // The reason money is never a double: 0.1 and 0.2 have no exact binary form.
        Assert.NotEqual(0.3, 0.1 + 0.2);

        Assert.Equal(Zar(30), Zar(10) + Zar(20));
    }

    [Fact]
    public void Adding_amounts_in_the_same_currency_adds_the_minor_units()
    {
        Assert.Equal(Zar(350_50), Zar(100_00) + Zar(250_50));
    }

    [Fact]
    public void Subtracting_and_negating_work_on_the_minor_units()
    {
        Assert.Equal(Zar(400_00), Zar(500_00) - Zar(100_00));
        Assert.Equal(Zar(-250_00), -Zar(250_00));
    }

    [Fact]
    public void Two_amounts_with_the_same_value_and_currency_are_equal()
    {
        Assert.Equal(Zar(500_00), Zar(500_00));
        Assert.NotEqual(Zar(500_00), Usd(500_00));
    }

    [Fact]
    public void Adding_different_currencies_is_rejected()
    {
        var error = Assert.Throws<LedgerRuleException>(() => Zar(100_00) + Usd(100_00));

        Assert.Equal(LedgerRule.CurrencyMismatch, error.Rule);
    }

    [Fact]
    public void An_amount_too_big_for_a_long_throws_instead_of_wrapping_round()
    {
        var biggest = new Money(long.MaxValue, Currency.ZAR);

        Assert.Throws<OverflowException>(() => biggest + Zar(1));
    }

    [Theory]
    [InlineData(50_000L, "ZAR", "ZAR 500.00")]
    [InlineData(-125_050L, "ZAR", "ZAR -1,250.50")]
    [InlineData(500L, "JPY", "JPY 500")]
    public void ToString_shows_major_units_with_the_currency_code(long amountMinor, string code, string expected)
    {
        var money = new Money(amountMinor, Currency.FromCode(code));

        Assert.Equal(expected, money.ToString());
    }

    [Fact]
    public void FromCode_returns_the_known_currency()
    {
        Assert.Same(Currency.ZAR, Currency.FromCode("ZAR"));
    }

    [Theory]
    [InlineData("zar")]
    [InlineData("XYZ")]
    [InlineData("")]
    public void FromCode_rejects_unknown_codes(string code)
    {
        Assert.Throws<ArgumentException>(() => Currency.FromCode(code));
    }
}
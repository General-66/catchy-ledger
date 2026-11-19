using System.Globalization;

namespace Ledger.Domain;

/// <summary>
/// An amount of money: a whole number of minor units (cents, for ZAR) and its currency.
/// R500.00 is stored as 50000. Never a float or a decimal.
/// </summary>
public sealed record Money
{
    public Money(long amountMinor, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        AmountMinor = amountMinor;
        Currency = currency;
    }

    /// <summary>The amount in minor units. In a posting, positive is a debit and negative is a credit.</summary>
    public long AmountMinor { get; }

    public Currency Currency { get; }

    public bool IsZero => AmountMinor == 0;

    public bool IsPositive => AmountMinor > 0;

    public bool IsNegative => AmountMinor < 0;

    public static Money Zero(Currency currency) => new(0, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        // checked: if the sum is too big for a long, throw instead of silently wrapping round to a negative number.
        return new Money(checked(left.AmountMinor + right.AmountMinor), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.AmountMinor - right.AmountMinor), left.Currency);
    }

    public static Money operator -(Money money) => new(checked(-money.AmountMinor), money.Currency);

    /// <summary>For people and test messages: "ZAR 500.00", "ZAR -1,250.50", "JPY 500".</summary>
    public override string ToString()
    {
        // decimal is used only to print the number. The amount itself is always the long above.
        var major = AmountMinor / (decimal)Math.Pow(10, Currency.MinorUnitDigits);
        var number = major.ToString("N" + Currency.MinorUnitDigits, CultureInfo.InvariantCulture);
        return $"{Currency.Code} {number}";
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new LedgerRuleException(
                LedgerRule.CurrencyMismatch,
                $"Can't combine {left.Currency} and {right.Currency}. Converting currencies is a transaction of its own.");
        }
    }
}
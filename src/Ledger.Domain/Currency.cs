namespace Ledger.Domain;

/// <summary>
/// An ISO 4217 currency, such as ZAR, and how many decimal places its minor unit has.
/// Only the currencies listed here exist; any other code is rejected.
/// </summary>
public sealed record Currency
{
    public static readonly Currency ZAR = new("ZAR", 2); // South African rand: 100 cents to the rand
    public static readonly Currency USD = new("USD", 2); // US dollar: 100 cents to the dollar
    public static readonly Currency EUR = new("EUR", 2); // euro: 100 cents to the euro
    public static readonly Currency GBP = new("GBP", 2); // British pound: 100 pence to the pound
    public static readonly Currency JPY = new("JPY", 0); // Japanese yen: no minor unit at all

    private Currency(string code, int minorUnitDigits)
    {
        Code = code;
        MinorUnitDigits = minorUnitDigits;
    }

    /// <summary>The three-letter ISO 4217 code, for example "ZAR".</summary>
    public string Code { get; }

    /// <summary>Decimal places in one major unit: 2 for ZAR (cents), 0 for JPY.</summary>
    public int MinorUnitDigits { get; }

    /// <summary>Finds a currency by its ISO code. Codes are upper case; unknown codes are rejected.</summary>
    public static Currency FromCode(string code) => code switch
    {
        "ZAR" => ZAR,
        "USD" => USD,
        "EUR" => EUR,
        "GBP" => GBP,
        "JPY" => JPY,
        _ => throw new ArgumentException($"'{code}' is not a supported ISO 4217 currency code.", nameof(code)),
    };

    public override string ToString() => Code;
}
using Ledger.Domain;

namespace Ledger.Tests.Unit.Support;

/// <summary>Short ways to write amounts in tests. Zar(500_00) reads as R500.00.</summary>
internal static class TestMoney
{
    public static Money Zar(long cents) => new(cents, Currency.ZAR);

    public static Money Usd(long cents) => new(cents, Currency.USD);
}
using Ledger.Domain;

namespace Ledger.Tests.Unit.Support;

/// <summary>
/// The chart of accounts for the betting wallet example in the plan, seen from the operator's books.
/// Codes follow the usual numbering: 1xxx assets, 2xxx liabilities, 3xxx income, 4xxx expenses.
/// </summary>
internal static class BettingChart
{
    // The operator's "house" accounts, shared by every customer.
    public static readonly Account Bank =
        new(1, "1000", "Bank", AccountType.Asset, Currency.ZAR, allowOverdraft: true);

    public static readonly Account OpenBets =
        new(2, "2100", "Open Bets", AccountType.Liability, Currency.ZAR, allowOverdraft: true);

    public static readonly Account BettingRevenue =
        new(3, "4000", "Betting Revenue", AccountType.Income, Currency.ZAR, allowOverdraft: true);

    public static readonly Account Payouts =
        new(4, "5000", "Payouts", AccountType.Expense, Currency.ZAR, allowOverdraft: true);

    // One customer's wallet. Every customer gets their own, and it may never be overdrawn.
    public static readonly Account CustomerWallet =
        new(5, "2000-0001", "Customer Wallet 0001", AccountType.Liability, Currency.ZAR, allowOverdraft: false);
}
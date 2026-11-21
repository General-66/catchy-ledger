using Ledger.Domain;
using Ledger.Infrastructure;

namespace Ledger.Tests.Integration.Support;

/// <summary>
/// The betting chart of accounts from the plan, created in the database. Tests in a class share one database,
/// so every chart gets codes ending in its own short random suffix, such as "1000-3f9a2c1b".
/// </summary>
internal sealed record TestBooks(Account Bank, Account OpenBets, Account BettingRevenue, Account Payouts, Account CustomerWallet)
{
    public static readonly DateOnly Day = new(2026, 11, 19);

    public static async Task<TestBooks> CreateAsync(SqlLedger ledger)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        Task<Account> Open(string code, string name, AccountType type, bool allowOverdraft) =>
            ledger.CreateAccountAsync($"{code}-{suffix}", name, type, Currency.ZAR, allowOverdraft);

        return new TestBooks(
            Bank: await Open("1000", "Bank", AccountType.Asset, allowOverdraft: true),
            OpenBets: await Open("2100", "Open Bets", AccountType.Liability, allowOverdraft: true),
            BettingRevenue: await Open("4000", "Betting Revenue", AccountType.Income, allowOverdraft: true),
            Payouts: await Open("5000", "Payouts", AccountType.Expense, allowOverdraft: true),
            CustomerWallet: await Open("2000", "Customer Wallet", AccountType.Liability, allowOverdraft: false));
    }

    public static Money Zar(long cents) => new(cents, Currency.ZAR);

    // The plan's worked example, as domain transactions: the same four journal entries as Phase 1's tests.
    public Transaction Deposit(long cents) => Transaction.Create(
        "Customer deposit", Day, Posting.Debit(Bank, Zar(cents)), Posting.Credit(CustomerWallet, Zar(cents)));

    public Transaction PlaceBet(long stake) => Transaction.Create(
        "Bet placed", Day, Posting.Debit(CustomerWallet, Zar(stake)), Posting.Credit(OpenBets, Zar(stake)));

    public Transaction BetLoses(long stake) => Transaction.Create(
        "Bet lost", Day, Posting.Debit(OpenBets, Zar(stake)), Posting.Credit(BettingRevenue, Zar(stake)));

    public Transaction BetWins(long stake, long payout) => Transaction.Create(
        "Bet won",
        Day,
        Posting.Debit(OpenBets, Zar(stake)),
        Posting.Debit(Payouts, Zar(payout - stake)),
        Posting.Credit(CustomerWallet, Zar(payout)));
}
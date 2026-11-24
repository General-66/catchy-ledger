using System.Net;
using Ledger.Api.Accounts;
using Ledger.Api.Transactions;
using Ledger.Domain;

namespace Ledger.Tests.Integration.Support;

/// <summary>
/// The betting chart of accounts, opened through the API, and the plan's journal entries as API requests. Tests in a
/// class share one database, so every chart gets codes ending in its own random suffix, such as "2000-3f9a2c1b".
/// </summary>
internal sealed record ApiBooks(long Bank, long OpenBets, long BettingRevenue, long Payouts, long CustomerWallet)
{
    public static async Task<ApiBooks> OpenAsync(HttpClient writer)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        async Task<long> Open(string code, string name, AccountType type, bool allowOverdraft)
        {
            var response = await writer.PostJsonAsync("/accounts", new CreateAccountRequest($"{code}-{suffix}", name, type, "ZAR", allowOverdraft));
            await response.ShouldBeAsync(HttpStatusCode.Created);
            return (await response.ReadJsonAsync<AccountResponse>()).Id;
        }

        return new ApiBooks(
            Bank: await Open("1000", "Bank", AccountType.Asset, allowOverdraft: true),
            OpenBets: await Open("2100", "Open Bets", AccountType.Liability, allowOverdraft: true),
            BettingRevenue: await Open("4000", "Betting Revenue", AccountType.Income, allowOverdraft: true),
            Payouts: await Open("5000", "Payouts", AccountType.Expense, allowOverdraft: true),
            CustomerWallet: await Open("2000", "Customer Wallet", AccountType.Liability, allowOverdraft: false));
    }

    public static PostingLineRequest Debit(long accountId, long cents) => new(accountId, PostingSide.Debit, cents);

    public static PostingLineRequest Credit(long accountId, long cents) => new(accountId, PostingSide.Credit, cents);

    // The same four journal entries as Phase 1's tests, as the API receives them.
    public PostTransactionRequest Deposit(long cents) =>
        new("Customer deposit", "ZAR", [Debit(Bank, cents), Credit(CustomerWallet, cents)], TestBooks.Day);

    public PostTransactionRequest PlaceBet(long stake) =>
        new("Bet placed", "ZAR", [Debit(CustomerWallet, stake), Credit(OpenBets, stake)], TestBooks.Day);

    public PostTransactionRequest BetLoses(long stake) =>
        new("Bet lost", "ZAR", [Debit(OpenBets, stake), Credit(BettingRevenue, stake)], TestBooks.Day);

    public PostTransactionRequest BetWins(long stake, long payout) =>
        new("Bet won", "ZAR", [Debit(OpenBets, stake), Debit(Payouts, payout - stake), Credit(CustomerWallet, payout)], TestBooks.Day);
}
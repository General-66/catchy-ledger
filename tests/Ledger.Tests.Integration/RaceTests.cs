using Ledger.Infrastructure;
using Ledger.Tests.Integration.Support;
using static Ledger.Tests.Integration.Support.TestBooks;

namespace Ledger.Tests.Integration;

/// <summary>
/// The plan's race: many bets at once against one wallet. However they interleave, the wallet is never overdrawn,
/// the stored balances agree with the postings, and the books sum to zero.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RaceTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private const int Bets = 100;
    private const int Rounds = 5;

    private LedgerDatabase _database = null!;
    private SqlLedger _ledger = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.DatabaseForAsync(nameof(RaceTests));
        _ledger = _database.AsApp();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_hundred_R80_bets_at_once_against_a_R100_wallet_let_exactly_one_through_every_time()
    {
        // Races are luck. One clean run proves little, so run it five times, each on a fresh wallet.
        for (var round = 1; round <= Rounds; round++)
        {
            var books = await TestBooks.CreateAsync(_ledger);
            await _ledger.PostAsync(books.Deposit(100_00), TestRequests.NewRequest());

            var outcomes = await Race.AllAtOnceAsync(Bets, _ => Race.OutcomeOfAsync(
                () => _ledger.PostAsync(books.PlaceBet(80_00), TestRequests.NewRequest())));

            Assert.Single(outcomes, outcome => outcome is null);
            Assert.Equal(Bets - 1, outcomes.Count(outcome => outcome == LedgerErrors.InsufficientFunds));
            Assert.Equal(Zar(20_00), books.CustomerWallet.BalanceOnNormalSide(await _ledger.GetStoredBalanceAsync(books.CustomerWallet)));
            Assert.Equal(Zar(80_00), books.OpenBets.BalanceOnNormalSide(await _ledger.GetStoredBalanceAsync(books.OpenBets)));
        }

        Assert.Empty(await _database.AccountsWithWrongBalancesAsync());
        Assert.Equal(0L, await _database.TrialBalanceAsync());
    }

    [Fact]
    public async Task Bets_and_wins_that_list_the_same_accounts_in_opposite_orders_never_deadlock()
    {
        // No retries: a deadlock would come back as error 1205 here, instead of being quietly run again.
        var ledger = _database.AsApp(SqlRetryPolicy.None);
        var books = await TestBooks.CreateAsync(ledger);
        await ledger.PostAsync(books.Deposit(10_000_00), TestRequests.NewRequest());

        // A bet's lines name the wallet, then Open Bets. A win's name Open Bets, Payouts, then the wallet. Locking in
        // the order the lines are written would let a bet hold the wallet while a win holds Open Bets, each waiting
        // for the other. Locking in ascending account Id order, as usp_WriteTransaction does, can't.
        var outcomes = await Race.AllAtOnceAsync(Bets, runner => Race.OutcomeOfAsync(() => ledger.PostAsync(
            runner % 2 == 0 ? books.PlaceBet(10_00) : books.BetWins(stake: 10_00, payout: 20_00),
            TestRequests.NewRequest())));

        Assert.All(outcomes, outcome => Assert.Null(outcome));
        Assert.Equal(Zar(10_500_00), books.CustomerWallet.BalanceOnNormalSide(await ledger.GetStoredBalanceAsync(books.CustomerWallet)));
        Assert.Empty(await _database.AccountsWithWrongBalancesAsync());
    }
}
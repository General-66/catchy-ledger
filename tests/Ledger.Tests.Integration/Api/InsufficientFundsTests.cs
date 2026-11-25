using System.Net;
using Ledger.Api.Errors;
using Ledger.Api.Transactions;
using Ledger.Tests.Integration.Support;

namespace Ledger.Tests.Integration.Api;

/// <summary>
/// The insufficient-funds rule through the API: a bet the wallet can't cover is a 422, a hundred bets at once still
/// let only one through, and a refused bet doesn't use up its Idempotency-Key.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class InsufficientFundsTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private LedgerDatabase _database = null!;
    private HttpClient _writer = null!;
    private ApiBooks _books = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.DatabaseForAsync(nameof(InsufficientFundsTests));
        _writer = (await sqlServer.ApiForAsync(nameof(InsufficientFundsTests))).ClientWith(TestApiKeys.Writer);
        _books = await ApiBooks.OpenAsync(_writer);
    }

    public Task DisposeAsync()
    {
        _writer.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_bet_bigger_than_the_wallet_is_refused_with_422_and_nothing_moves()
    {
        await _writer.PostTransactionAsync(_books.Deposit(100_00));

        var response = await _writer.PostJsonAsync("/transactions", _books.PlaceBet(100_01), Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ProblemCodes.InsufficientFunds, await response.ProblemCodeAsync());
        Assert.Equal(100_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task A_hundred_R80_bets_at_once_through_the_api_let_exactly_one_through()
    {
        await _writer.PostTransactionAsync(_books.Deposit(100_00));

        var responses = await Race.AllAtOnceAsync(100, _ =>
            _writer.PostJsonAsync("/transactions", _books.PlaceBet(80_00), Guid.NewGuid().ToString()));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        var refused = responses.Where(response => response.StatusCode == HttpStatusCode.UnprocessableEntity).ToArray();
        Assert.Equal(99, refused.Length);
        foreach (var response in refused)
        {
            Assert.Equal(ProblemCodes.InsufficientFunds, await response.ProblemCodeAsync());
        }

        Assert.Equal(20_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
        Assert.Empty(await _database.AccountsWithWrongBalancesAsync());
    }

    [Fact]
    public async Task A_bet_refused_for_insufficient_funds_can_be_sent_again_with_the_same_key_once_the_money_is_there()
    {
        var key = Guid.NewGuid().ToString();
        var tooSoon = await _writer.PostJsonAsync("/transactions", _books.PlaceBet(80_00), key);
        await tooSoon.ShouldBeAsync(HttpStatusCode.UnprocessableEntity);

        // Only a transaction that was posted stores its key, so the same bet can be sent again after a deposit.
        await _writer.PostTransactionAsync(_books.Deposit(100_00));
        var retry = await _writer.PostJsonAsync("/transactions", _books.PlaceBet(80_00), key);

        await retry.ShouldBeAsync(HttpStatusCode.Created);
        Assert.False(retry.Headers.Contains(IdempotentPosting.ReplayedHeader));
        Assert.Equal(20_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
    }
}
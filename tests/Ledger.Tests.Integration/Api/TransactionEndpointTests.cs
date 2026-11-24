using System.Net;
using Ledger.Api;
using Ledger.Api.Errors;
using Ledger.Api.Transactions;
using Ledger.Domain;
using Ledger.Tests.Integration.Support;
using static Ledger.Tests.Integration.Support.ApiBooks;

namespace Ledger.Tests.Integration.Api;

/// <summary>Posting, reading and reversing through the API, including every way a transaction can be refused.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class TransactionEndpointTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private LedgerDatabase _database = null!;
    private HttpClient _writer = null!;
    private ApiBooks _books = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.DatabaseForAsync(nameof(TransactionEndpointTests));
        _writer = (await sqlServer.ApiForAsync(nameof(TransactionEndpointTests))).ClientWith(TestApiKeys.Writer);
        _books = await ApiBooks.OpenAsync(_writer);
    }

    public Task DisposeAsync()
    {
        _writer.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task The_betting_example_runs_end_to_end_through_the_api()
    {
        await _writer.PostTransactionAsync(_books.Deposit(500_00));
        await _writer.PostTransactionAsync(_books.PlaceBet(100_00));
        var win = await _writer.PostTransactionAsync(_books.BetWins(stake: 100_00, payout: 250_00));

        // Phase 1's table for step 3b, now read back through the API.
        Assert.Equal(500_00, await _writer.BalanceOnNormalSideAsync(_books.Bank));
        Assert.Equal(650_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
        Assert.Equal(0, await _writer.BalanceOnNormalSideAsync(_books.OpenBets));
        Assert.Equal(150_00, await _writer.BalanceOnNormalSideAsync(_books.Payouts));
        Assert.Equal(3, win.Lines.Count);
        Assert.Equal(0L, await _database.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_posted_transaction_reads_back_exactly_as_it_was_returned()
    {
        var response = await _writer.PostJsonAsync("/transactions", _books.Deposit(500_00), Guid.NewGuid().ToString());
        await response.ShouldBeAsync(HttpStatusCode.Created);
        var posted = await response.ReadJsonAsync<TransactionResponse>();

        var read = await _writer.GetAsync($"/transactions/{posted.Id}");

        await read.ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal($"/transactions/{posted.Id}", response.Headers.Location?.OriginalString);
        var stored = await read.ReadJsonAsync<TransactionResponse>();
        Assert.Equal(
            (posted.Id, posted.CreatedBy, posted.CreatedAt, posted.EffectiveDate),
            (stored.Id, stored.CreatedBy, stored.CreatedAt, stored.EffectiveDate));
        Assert.Equal(posted.Lines, stored.Lines);
        Assert.Equal(("Customer deposit", TestBooks.Day, "ZAR", (long?)null), (stored.Description, stored.EffectiveDate, stored.Currency, stored.ReversesTransactionId));
        Assert.Collection(
            stored.Lines,
            line => Assert.Equal((_books.Bank, PostingSide.Debit, 500_00L), (line.AccountId, line.Side, line.AmountMinor)),
            line => Assert.Equal((_books.CustomerWallet, PostingSide.Credit, 500_00L), (line.AccountId, line.Side, line.AmountMinor)));
    }

    [Fact]
    public async Task Without_an_effective_date_a_transaction_counts_for_today_in_South_Africa()
    {
        var posted = await _writer.PostTransactionAsync(_books.Deposit(100_00) with { EffectiveDate = null });

        Assert.Equal(new BusinessCalendar(TimeProvider.System).Today, posted.EffectiveDate);
    }

    [Fact]
    public async Task An_unbalanced_transaction_is_rejected_with_422_and_nothing_is_posted()
    {
        var transactionsBefore = await _database.TransactionCountAsync();
        var oneCentShort = new PostTransactionRequest("One cent short", "ZAR",
            [Debit(_books.Bank, 500_00), Credit(_books.CustomerWallet, 499_99)], TestBooks.Day);

        var response = await _writer.PostJsonAsync("/transactions", oneCentShort, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(nameof(LedgerRule.Unbalanced), await response.ProblemCodeAsync());
        Assert.Equal(transactionsBefore, await _database.TransactionCountAsync());
    }

    [Fact]
    public async Task A_single_line_is_rejected_with_422()
    {
        var oneLine = new PostTransactionRequest("One line", "ZAR", [Debit(_books.Bank, 500_00)], TestBooks.Day);

        var response = await _writer.PostJsonAsync("/transactions", oneLine, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(nameof(LedgerRule.TooFewPostings), await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Dollars_posted_to_rand_accounts_are_rejected_with_422()
    {
        var dollars = _books.Deposit(100_00) with { Currency = "USD" };

        var response = await _writer.PostJsonAsync("/transactions", dollars, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(nameof(LedgerRule.AccountCurrencyMismatch), await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_line_on_an_account_that_does_not_exist_is_rejected_with_422()
    {
        var ghost = new PostTransactionRequest("Ghost account", "ZAR",
            [Debit(_books.Bank, 100_00), Credit(long.MaxValue, 100_00)], TestBooks.Day);

        var response = await _writer.PostJsonAsync("/transactions", ghost, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ProblemCodes.UnknownAccount, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_negative_or_zero_amount_is_rejected_with_400_because_the_side_gives_the_sign()
    {
        var negative = new PostTransactionRequest("Negative", "ZAR",
            [Debit(_books.Bank, -100_00), Credit(_books.CustomerWallet, 0)], TestBooks.Day);

        var response = await _writer.PostJsonAsync("/transactions", negative, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ProblemCodes.ValidationFailed, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_misspelled_field_is_rejected_with_400_instead_of_being_ignored()
    {
        // "effectiveDay" instead of "effectiveDate". Ignoring it would quietly post the deposit for today instead.
        var misspelled = new
        {
            description = "Misspelled",
            currency = "ZAR",
            lines = new[] { Debit(_books.Bank, 100_00), Credit(_books.CustomerWallet, 100_00) },
            effectiveDay = "2026-11-19",
        };

        var response = await _writer.PostJsonAsync("/transactions", misspelled, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ProblemCodes.MalformedRequest, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task An_amount_sent_as_a_string_is_rejected_with_400()
    {
        var quoted = new
        {
            description = "Quoted amounts",
            currency = "ZAR",
            lines = new object[]
            {
                new { accountId = _books.Bank, side = "Debit", amountMinor = "50000" },
                new { accountId = _books.CustomerWallet, side = "Credit", amountMinor = "50000" },
            },
        };

        var response = await _writer.PostJsonAsync("/transactions", quoted, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ProblemCodes.MalformedRequest, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_rejected_with_415()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = new StringContent("deposit R500 please"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _writer.SendAsync(request);

        await response.ShouldBeAsync(HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(ProblemCodes.UnsupportedMediaType, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_transaction_that_does_not_exist_is_404()
    {
        var response = await _writer.GetAsync($"/transactions/{long.MaxValue}");

        await response.ShouldBeAsync(HttpStatusCode.NotFound);
        Assert.Equal(ProblemCodes.TransactionNotFound, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Reversing_a_bet_puts_the_stake_back_in_the_wallet()
    {
        await _writer.PostTransactionAsync(_books.Deposit(500_00));
        var bet = await _writer.PostTransactionAsync(_books.PlaceBet(100_00));

        var response = await _writer.PostJsonAsync($"/transactions/{bet.Id}/reverse", body: null, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.Created);
        var reversal = await response.ReadJsonAsync<TransactionResponse>();
        Assert.Equal(bet.Id, reversal.ReversesTransactionId);
        Assert.Equal($"Reversal of {bet.Id}: Bet placed", reversal.Description);
        Assert.Equal(
            bet.Lines.Select(line => (line.AccountId, line.Side == PostingSide.Debit ? PostingSide.Credit : PostingSide.Debit, line.AmountMinor)),
            reversal.Lines.Select(line => (line.AccountId, line.Side, line.AmountMinor)));
        Assert.Equal(500_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
        Assert.Equal(0, await _writer.BalanceOnNormalSideAsync(_books.OpenBets));
    }

    [Fact]
    public async Task A_reversal_can_take_its_own_effective_date()
    {
        var deposit = await _writer.PostTransactionAsync(_books.Deposit(100_00));
        var nextDay = TestBooks.Day.AddDays(1);

        var response = await _writer.PostJsonAsync($"/transactions/{deposit.Id}/reverse",
            new ReverseTransactionRequest(nextDay), Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(nextDay, (await response.ReadJsonAsync<TransactionResponse>()).EffectiveDate);
    }

    [Fact]
    public async Task A_transaction_can_be_reversed_only_once()
    {
        var deposit = await _writer.PostTransactionAsync(_books.Deposit(500_00));
        await (await _writer.PostJsonAsync($"/transactions/{deposit.Id}/reverse", null, Guid.NewGuid().ToString()))
            .ShouldBeAsync(HttpStatusCode.Created);

        // A new key: this is a second reversal, not a retry of the first.
        var second = await _writer.PostJsonAsync($"/transactions/{deposit.Id}/reverse", null, Guid.NewGuid().ToString());

        await second.ShouldBeAsync(HttpStatusCode.Conflict);
        Assert.Equal(ProblemCodes.AlreadyReversed, await second.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_reversal_cannot_itself_be_reversed()
    {
        var deposit = await _writer.PostTransactionAsync(_books.Deposit(500_00));
        var reversal = await (await _writer.PostJsonAsync($"/transactions/{deposit.Id}/reverse", null, Guid.NewGuid().ToString()))
            .ReadJsonAsync<TransactionResponse>();

        var response = await _writer.PostJsonAsync($"/transactions/{reversal.Id}/reverse", null, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ProblemCodes.ReversalOfReversal, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Reversing_a_transaction_that_does_not_exist_is_404()
    {
        var response = await _writer.PostJsonAsync($"/transactions/{long.MaxValue}/reverse", null, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.NotFound);
        Assert.Equal(ProblemCodes.TransactionNotFound, await response.ProblemCodeAsync());
    }
}
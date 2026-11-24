using System.Net;
using System.Text;
using Ledger.Api.Errors;
using Ledger.Api.Transactions;
using Ledger.Tests.Integration.Support;

namespace Ledger.Tests.Integration.Api;

/// <summary>
/// The Idempotency-Key header: required on every write that moves money, and a retried request never posts twice,
/// even when the copies arrive at the same moment.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IdempotencyTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private LedgerApiFactory _api = null!;
    private LedgerDatabase _database = null!;
    private HttpClient _writer = null!;
    private ApiBooks _books = null!;

    public async Task InitializeAsync()
    {
        _api = await sqlServer.ApiForAsync(nameof(IdempotencyTests));
        _database = await sqlServer.DatabaseForAsync(nameof(IdempotencyTests));
        _writer = _api.ClientWith(TestApiKeys.Writer);
        _books = await ApiBooks.OpenAsync(_writer);
    }

    public Task DisposeAsync()
    {
        _writer.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_post_without_an_idempotency_key_is_rejected_with_400_and_nothing_is_posted()
    {
        var response = await _writer.PostJsonAsync("/transactions", _books.Deposit(500_00), idempotencyKey: null);

        await response.ShouldBeAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ProblemCodes.IdempotencyKeyMissing, await response.ProblemCodeAsync());
        Assert.Equal(0, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task A_reversal_without_an_idempotency_key_is_rejected_with_400()
    {
        var deposit = await _writer.PostTransactionAsync(_books.Deposit(500_00));

        var response = await _writer.PostJsonAsync($"/transactions/{deposit.Id}/reverse", body: null, idempotencyKey: null);

        await response.ShouldBeAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ProblemCodes.IdempotencyKeyMissing, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task An_idempotency_key_longer_than_the_database_stores_is_rejected_with_400()
    {
        var response = await _writer.PostJsonAsync("/transactions", _books.Deposit(500_00), new string('k', 101));

        await response.ShouldBeAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ProblemCodes.IdempotencyKeyInvalid, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_retried_request_gets_the_original_response_and_posts_only_once()
    {
        var key = Guid.NewGuid().ToString();

        var first = await _writer.PostJsonAsync("/transactions", _books.Deposit(500_00), key);
        var retry = await _writer.PostJsonAsync("/transactions", _books.Deposit(500_00), key);

        await first.ShouldBeAsync(HttpStatusCode.Created);
        await retry.ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.False(first.Headers.Contains(IdempotentPosting.ReplayedHeader));
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues(IdempotentPosting.ReplayedHeader)));
        Assert.Equal(500_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet)); // R500, not R1,000
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_rejected_with_422()
    {
        var key = Guid.NewGuid().ToString();
        await (await _writer.PostJsonAsync("/transactions", _books.Deposit(500_00), key)).ShouldBeAsync(HttpStatusCode.Created);

        var different = await _writer.PostJsonAsync("/transactions", _books.Deposit(900_00), key);

        await different.ShouldBeAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ProblemCodes.IdempotencyKeyReused, await different.ProblemCodeAsync());
        Assert.Equal(500_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task Spacing_and_property_order_do_not_make_a_different_request()
    {
        var key = Guid.NewGuid().ToString();
        var compact = $$"""{"description":"Customer deposit","currency":"ZAR","lines":[{"accountId":{{_books.Bank}},"side":"Debit","amountMinor":50000},{"accountId":{{_books.CustomerWallet}},"side":"Credit","amountMinor":50000}],"effectiveDate":"2026-11-19"}""";
        var reordered = $$"""
            {
              "lines": [
                { "amountMinor": 50000, "side": "Debit", "accountId": {{_books.Bank}} },
                { "amountMinor": 50000, "side": "Credit", "accountId": {{_books.CustomerWallet}} }
              ],
              "effectiveDate": "2026-11-19",
              "currency": "ZAR",
              "description": "Customer deposit"
            }
            """;

        var first = await PostRawAsync(compact, key);
        var retry = await PostRawAsync(reordered, key);

        await first.ShouldBeAsync(HttpStatusCode.Created);
        await retry.ShouldBeAsync(HttpStatusCode.Created);
        Assert.True(retry.Headers.Contains(IdempotentPosting.ReplayedHeader));
        Assert.Equal(500_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task Twenty_copies_of_a_request_at_the_same_moment_post_exactly_once()
    {
        var key = Guid.NewGuid().ToString();
        var transactionsBefore = await _database.TransactionCountAsync();

        // The first to reach the database posts. The rest wait on the unique constraint, fail with error 2627,
        // and are answered with the first one's transaction.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => _writer.PostJsonAsync("/transactions", _books.Deposit(500_00), key)));

        foreach (var response in responses)
        {
            await response.ShouldBeAsync(HttpStatusCode.Created);
        }

        var ids = await Task.WhenAll(responses.Select(async response => (await response.ReadJsonAsync<TransactionResponse>()).Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(transactionsBefore + 1, await _database.TransactionCountAsync());
        Assert.Equal(500_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task A_rejected_request_does_not_use_up_its_key()
    {
        var key = Guid.NewGuid().ToString();
        var oneCentShort = new PostTransactionRequest("Customer deposit", "ZAR",
            [ApiBooks.Debit(_books.Bank, 500_00), ApiBooks.Credit(_books.CustomerWallet, 499_99)], TestBooks.Day);
        await (await _writer.PostJsonAsync("/transactions", oneCentShort, key)).ShouldBeAsync(HttpStatusCode.UnprocessableEntity);

        // Only a transaction that was posted stores its key, so the corrected request can use the same one.
        var corrected = await _writer.PostJsonAsync("/transactions", _books.Deposit(500_00), key);

        await corrected.ShouldBeAsync(HttpStatusCode.Created);
        Assert.False(corrected.Headers.Contains(IdempotentPosting.ReplayedHeader));
    }

    [Fact]
    public async Task Each_caller_has_its_own_keys()
    {
        using var secondWriter = _api.ClientWith(TestApiKeys.SecondWriter);
        var key = Guid.NewGuid().ToString();

        var mine = await (await _writer.PostJsonAsync("/transactions", _books.Deposit(100_00), key)).ReadJsonAsync<TransactionResponse>();
        var theirs = await (await secondWriter.PostJsonAsync("/transactions", _books.Deposit(100_00), key)).ReadJsonAsync<TransactionResponse>();

        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Equal((TestApiKeys.WriterName, TestApiKeys.SecondWriterName), (mine.CreatedBy, theirs.CreatedBy));
        Assert.Equal(200_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task A_retried_reversal_gets_the_original_reversal_back()
    {
        var deposit = await _writer.PostTransactionAsync(_books.Deposit(500_00));
        var key = Guid.NewGuid().ToString();

        var first = await _writer.PostJsonAsync($"/transactions/{deposit.Id}/reverse", body: null, key);
        var retry = await _writer.PostJsonAsync($"/transactions/{deposit.Id}/reverse", new { }, key);

        await first.ShouldBeAsync(HttpStatusCode.Created);
        await retry.ShouldBeAsync(HttpStatusCode.Created);
        Assert.Equal(
            (await first.ReadJsonAsync<TransactionResponse>()).Id,
            (await retry.ReadJsonAsync<TransactionResponse>()).Id);
        Assert.True(retry.Headers.Contains(IdempotentPosting.ReplayedHeader));
        Assert.Equal(0, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task A_key_used_to_post_cannot_be_reused_to_reverse()
    {
        var key = Guid.NewGuid().ToString();
        var deposit = await (await _writer.PostJsonAsync("/transactions", _books.Deposit(500_00), key)).ReadJsonAsync<TransactionResponse>();

        var response = await _writer.PostJsonAsync($"/transactions/{deposit.Id}/reverse", body: null, key);

        await response.ShouldBeAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ProblemCodes.IdempotencyKeyReused, await response.ProblemCodeAsync());
        Assert.Equal(500_00, await _writer.BalanceOnNormalSideAsync(_books.CustomerWallet));
    }

    private Task<HttpResponseMessage> PostRawAsync(string json, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/transactions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        return _writer.SendAsync(request);
    }
}
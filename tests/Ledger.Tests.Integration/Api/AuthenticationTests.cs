using System.Net;
using System.Text.Json;
using Ledger.Api.Errors;
using Ledger.Tests.Integration.Support;

namespace Ledger.Tests.Integration.Api;

/// <summary>Every ledger endpoint needs an API key, the key's role decides what it may do, and its name becomes CreatedBy.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class AuthenticationTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private LedgerApiFactory _api = null!;

    public async Task InitializeAsync() => _api = await sqlServer.ApiForAsync(nameof(AuthenticationTests));

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_request_without_an_api_key_is_rejected_with_401_problem_details()
    {
        using var client = _api.ClientWith(apiKey: null);

        var response = await client.GetAsync("/accounts/1");

        await response.ShouldBeAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ProblemCodes.ApiKeyMissing, await response.ProblemCodeAsync());
        Assert.Contains(response.Headers.WwwAuthenticate, challenge => challenge.Scheme == "ApiKey");
    }

    [Fact]
    public async Task A_request_with_an_unknown_api_key_is_rejected_with_401()
    {
        using var client = _api.ClientWith("not-a-key-this-api-knows-0000000000000");

        var response = await client.GetAsync("/accounts/1");

        await response.ShouldBeAsync(HttpStatusCode.Unauthorized);
        Assert.Equal(ProblemCodes.ApiKeyInvalid, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_read_key_can_read()
    {
        using var reader = _api.ClientWith(TestApiKeys.Reader);

        var response = await reader.GetAsync("/accounts/1");

        await response.ShouldBeAsync(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/accounts")]
    [InlineData("/transactions")]
    [InlineData("/transactions/1/reverse")]
    public async Task A_read_key_cannot_write(string path)
    {
        using var reader = _api.ClientWith(TestApiKeys.Reader);

        // The role is checked before the body is even read, so an empty body is enough.
        var response = await reader.PostJsonAsync(path, new { }, Guid.NewGuid().ToString());

        await response.ShouldBeAsync(HttpStatusCode.Forbidden);
        Assert.Equal(ProblemCodes.InsufficientRole, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task The_caller_named_by_the_key_becomes_CreatedBy()
    {
        using var writer = _api.ClientWith(TestApiKeys.Writer);
        var books = await ApiBooks.OpenAsync(writer);

        var deposit = await writer.PostTransactionAsync(books.Deposit(500_00));

        Assert.Equal(TestApiKeys.WriterName, deposit.CreatedBy);
    }

    [Fact]
    public async Task The_docs_need_no_key_and_describe_both_headers()
    {
        using var client = _api.ClientWith(apiKey: null);

        var document = await client.GetAsync("/openapi/v1.json");
        var scalar = await client.GetAsync("/scalar/");

        await document.ShouldBeAsync(HttpStatusCode.OK);
        await scalar.ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("text/html", scalar.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await document.Content.ReadAsStringAsync());
        var apiKey = json.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", apiKey.GetProperty("type").GetString());
        Assert.Equal("header", apiKey.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", apiKey.GetProperty("name").GetString());

        var postParameters = json.RootElement.GetProperty("paths").GetProperty("/transactions").GetProperty("post").GetProperty("parameters");
        var idempotencyKey = Assert.Single(postParameters.EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.Equal("header", idempotencyKey.GetProperty("in").GetString());
        Assert.True(idempotencyKey.GetProperty("required").GetBoolean());
    }
}
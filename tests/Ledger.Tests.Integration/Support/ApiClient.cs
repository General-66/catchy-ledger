using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Api.Accounts;
using Ledger.Api.Transactions;

namespace Ledger.Tests.Integration.Support;

/// <summary>Small helpers for calling the API from tests, using the API's own request and response types.</summary>
internal static class ApiClient
{
    /// <summary>The same JSON conventions as the API: camelCase names, and enums as words.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object? body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    /// <summary>Fails the test with the response body if the status isn't the expected one, so the reason is right there.</summary>
    public static async Task ShouldBeAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            Assert.Fail($"Expected {(int)expected} {expected}, got {(int)response.StatusCode} {response.StatusCode}: "
                + await response.Content.ReadAsStringAsync());
        }
    }

    public static async Task<T> ReadJsonAsync<T>(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(Json)
        ?? throw new InvalidOperationException("The response body was empty.");

    /// <summary>The "code" of a Problem Details response, such as "Unbalanced".</summary>
    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Posts a transaction with a new Idempotency-Key, and checks it was created.</summary>
    public static async Task<TransactionResponse> PostTransactionAsync(this HttpClient client, PostTransactionRequest request)
    {
        var response = await client.PostJsonAsync("/transactions", request, Guid.NewGuid().ToString());
        await response.ShouldBeAsync(HttpStatusCode.Created);
        return await response.ReadJsonAsync<TransactionResponse>();
    }

    public static async Task<AccountResponse> GetAccountAsync(this HttpClient client, long id)
    {
        var response = await client.GetAsync($"/accounts/{id}");
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return await response.ReadJsonAsync<AccountResponse>();
    }

    /// <summary>An account's balance read the way a person would, in minor units: a wallet holding R500 is 50000.</summary>
    public static async Task<long> BalanceOnNormalSideAsync(this HttpClient client, long accountId) =>
        (await client.GetAccountAsync(accountId)).Balance.OnNormalSideMinor;
}
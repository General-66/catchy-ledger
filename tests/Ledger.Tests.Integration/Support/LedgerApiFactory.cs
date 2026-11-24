using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ledger.Tests.Integration.Support;

/// <summary>The API keys the in-memory API knows during tests. They exist only here; no real key is in source control.</summary>
public static class TestApiKeys
{
    public const string WriterName = "test-writer";
    public const string Writer = "test-writer-key-0000-0000-0000-000000000001";

    /// <summary>A second Write caller, to prove that each caller's Idempotency-Keys are its own.</summary>
    public const string SecondWriterName = "test-writer-2";
    public const string SecondWriter = "test-writer-key-0000-0000-0000-000000000002";

    public const string ReaderName = "test-reader";
    public const string Reader = "test-reader-key-0000-0000-0000-000000000003";
}

/// <summary>
/// The whole API, running in memory against one test class's database. It uses the "Testing" environment, so your
/// own User Secrets are never loaded into a test run: the connection string and keys below are the only ones it sees.
/// </summary>
public sealed class LedgerApiFactory(string appConnectionString) : WebApplicationFactory<Program>
{
    /// <summary>An HttpClient that sends this API key with every request, or no key at all if it's null.</summary>
    public HttpClient ClientWith(string? apiKey)
    {
        var client = CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = appConnectionString,
            [$"ApiKeys:{TestApiKeys.WriterName}:Key"] = TestApiKeys.Writer,
            [$"ApiKeys:{TestApiKeys.WriterName}:Role"] = "Write",
            [$"ApiKeys:{TestApiKeys.SecondWriterName}:Key"] = TestApiKeys.SecondWriter,
            [$"ApiKeys:{TestApiKeys.SecondWriterName}:Role"] = "Write",
            [$"ApiKeys:{TestApiKeys.ReaderName}:Key"] = TestApiKeys.Reader,
            [$"ApiKeys:{TestApiKeys.ReaderName}:Role"] = "Read",
        }));
    }
}
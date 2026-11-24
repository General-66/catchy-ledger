using System.Net;
using Ledger.Tests.Integration.Support;

namespace Ledger.Tests.Integration;

/// <summary>/health needs no API key, and is only Healthy when the database answers too.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class HealthEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        var api = await sqlServer.ApiForAsync(nameof(HealthEndpointTests));
        using var client = api.ClientWith(apiKey: null);

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task Health_endpoint_reports_unhealthy_when_the_database_cannot_be_reached()
    {
        // Nothing listens on port 1, so the connection is refused straight away.
        const string unreachable = "Server=127.0.0.1,1;Database=Ledger;User Id=nobody;Password=Nobody_2026!;TrustServerCertificate=True;Connect Timeout=3";
        await using var api = new LedgerApiFactory(unreachable);
        using var client = api.ClientWith(apiKey: null);

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", body);
    }
}
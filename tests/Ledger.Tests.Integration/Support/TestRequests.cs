using Ledger.Infrastructure;

namespace Ledger.Tests.Integration.Support;

/// <summary>Request details for tests. Each call gets a new Idempotency-Key, as a new HTTP request would.</summary>
internal static class TestRequests
{
    public const string Caller = "integration-tests";

    public static RequestInfo NewRequest(string? idempotencyKey = null, string body = "{}") =>
        new(Caller, idempotencyKey ?? Guid.NewGuid().ToString(), RequestInfo.HashOf(body));
}
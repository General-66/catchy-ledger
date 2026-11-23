namespace Ledger.Infrastructure;

/// <summary>
/// What the database remembers about an Idempotency-Key: the transaction it posted, and the fingerprint of the
/// request that posted it. A retry of that request has the same fingerprint; a different request reusing the key doesn't.
/// </summary>
public sealed record IdempotencyRecord(long TransactionId, IReadOnlyList<byte> RequestHash)
{
    /// <summary>True if this request is the same one that first used the key, so its original result can be returned.</summary>
    public bool IsSameRequestAs(RequestInfo request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestHash.SequenceEqual(request.RequestHash);
    }
}
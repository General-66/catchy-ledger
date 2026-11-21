using System.Security.Cryptography;
using System.Text;

namespace Ledger.Infrastructure;

/// <summary>
/// Who asked for a write, and the two values that make retrying it safe: the caller's Idempotency-Key and a
/// SHA-256 fingerprint of the request body. Phase 3 fills these in from the HTTP request.
/// </summary>
public sealed class RequestInfo
{
    /// <summary>The longest CreatedBy and IdempotencyKey the database stores (both nvarchar(100)).</summary>
    public const int MaxLength = 100;

    public RequestInfo(string createdBy, string idempotencyKey, byte[] requestHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(requestHash);
        // A stored procedure silently cuts a too-long value down to its parameter's size, so two different long
        // keys could end up as the same key. Refuse them here instead.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(createdBy.Length, MaxLength, nameof(createdBy));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(idempotencyKey.Length, MaxLength, nameof(idempotencyKey));
        if (requestHash.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException($"A SHA-256 hash is {SHA256.HashSizeInBytes} bytes.", nameof(requestHash));
        }

        CreatedBy = createdBy;
        IdempotencyKey = idempotencyKey;
        RequestHash = [.. requestHash]; // a copy, so the caller can't change it afterwards
    }

    public string CreatedBy { get; }

    public string IdempotencyKey { get; }

    public IReadOnlyList<byte> RequestHash { get; }

    /// <summary>The SHA-256 fingerprint of a request body, for the requestHash argument.</summary>
    public static byte[] HashOf(string requestBody)
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        return SHA256.HashData(Encoding.UTF8.GetBytes(requestBody));
    }
}
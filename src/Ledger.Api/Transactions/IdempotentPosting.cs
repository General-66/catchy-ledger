using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Api.Errors;
using Ledger.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Ledger.Api.Transactions;

/// <summary>
/// Makes POST /transactions and POST /transactions/{id}/reverse safe to retry. The caller sends an Idempotency-Key
/// header; the first request with a key posts, and every later one with the same key and the same body gets the
/// first one's response back, without posting again. The database's unique constraint on (CreatedBy, IdempotencyKey)
/// is the real guarantee; this class turns it into the right HTTP answers.
/// </summary>
public static class IdempotentPosting
{
    public const string KeyHeader = "Idempotency-Key";

    /// <summary>Added to a response that's a replay of an earlier one, so a client can tell (Stripe does the same).</summary>
    public const string ReplayedHeader = "Idempotent-Replayed";

    // A fixed way of writing a request as JSON. Two requests that differ only in spacing or property order are written
    // the same way, so they get the same fingerprint and count as the same request.
    private static readonly JsonSerializerOptions FingerprintJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads the Idempotency-Key header, or explains with a 400 why it can't.</summary>
    public static bool TryReadKey(string? header, [NotNullWhen(true)] out string? key, [NotNullWhen(false)] out IResult? problem)
    {
        key = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(header))
        {
            problem = Problems.Result(StatusCodes.Status400BadRequest, ProblemCodes.IdempotencyKeyMissing,
                "Idempotency-Key header required",
                "Send a new random value, such as a GUID, in the Idempotency-Key header for each operation, and the same value if you retry it.");
            return false;
        }

        if (header.Length > RequestInfo.MaxLength)
        {
            problem = Problems.Result(StatusCodes.Status400BadRequest, ProblemCodes.IdempotencyKeyInvalid,
                "Idempotency-Key header too long", $"An Idempotency-Key is at most {RequestInfo.MaxLength} characters.");
            return false;
        }

        key = header;
        return true;
    }

    /// <summary>
    /// Who's asking, their key, and a SHA-256 fingerprint of what they asked for. The fingerprint covers the endpoint
    /// as well as the body, so a key used to post can't be reused to reverse.
    /// </summary>
    public static RequestInfo Describe<TBody>(HttpContext http, string endpoint, string key, TBody body)
    {
        var caller = http.User.Identity?.Name
            ?? throw new InvalidOperationException("Only callers with an API key reach this endpoint.");

        return new RequestInfo(caller, key, RequestInfo.HashOf($"{endpoint}\n{JsonSerializer.Serialize(body, FingerprintJson)}"));
    }

    /// <summary>
    /// Posts once per Idempotency-Key. If the key was used before, nothing is posted: the same request gets the original
    /// response again (201, with Idempotent-Replayed: true), and a different request gets a 422.
    /// </summary>
    public static async Task<IResult> PostOnceAsync(
        HttpContext http, SqlLedger ledger, RequestInfo request, Func<Task<long>> post, CancellationToken cancellationToken)
    {
        var earlier = await ledger.FindByIdempotencyKeyAsync(request.CreatedBy, request.IdempotencyKey, cancellationToken);
        if (earlier is not null)
        {
            return await ReplayAsync(http, ledger, earlier, request, cancellationToken);
        }

        long transactionId;
        try
        {
            transactionId = await post();
        }
        catch (SqlException)
        {
            // Another request with the same key may have committed while this one was posting. The unique constraint
            // made this one fail (error 2627) instead of posting twice. If that's what happened, answer as a retry.
            var winner = await ledger.FindByIdempotencyKeyAsync(request.CreatedBy, request.IdempotencyKey, CancellationToken.None);
            if (winner is null)
            {
                throw; // a real error, such as a broken rule: LedgerExceptionHandler answers it
            }

            return await ReplayAsync(http, ledger, winner, request, cancellationToken);
        }

        return await CreatedAsync(http, ledger, transactionId, replayed: false, cancellationToken);
    }

    private static async Task<IResult> ReplayAsync(
        HttpContext http, SqlLedger ledger, IdempotencyRecord earlier, RequestInfo request, CancellationToken cancellationToken)
    {
        if (!earlier.IsSameRequestAs(request))
        {
            return Problems.Result(StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyKeyReused,
                "Idempotency-Key already used for a different request",
                $"This key already posted transaction {earlier.TransactionId} from a different request. Use a new key for a new operation.");
        }

        return await CreatedAsync(http, ledger, earlier.TransactionId, replayed: true, cancellationToken);
    }

    // Both the first response and every replay are read back from the database, so they're identical.
    private static async Task<IResult> CreatedAsync(
        HttpContext http, SqlLedger ledger, long transactionId, bool replayed, CancellationToken cancellationToken)
    {
        var transaction = await ledger.GetTransactionAsync(transactionId, cancellationToken)
            ?? throw new InvalidOperationException($"Transaction {transactionId} was posted but can't be read back.");

        if (replayed)
        {
            http.Response.Headers[ReplayedHeader] = "true";
        }

        return TypedResults.Created($"/transactions/{transactionId}", TransactionResponse.From(transaction));
    }
}
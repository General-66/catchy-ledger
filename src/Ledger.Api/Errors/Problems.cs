using Ledger.Domain;
using Ledger.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens.Experimental;
using System.Text.Json;

namespace Ledger.Api.Errors;

/// <summary>
/// The "code" in every error response. Clients should switch on these, never on the wording of title or detail,
/// which may change. The six ledger rules use their LedgerRule names, such as "Unbalanced".
/// </summary>
public static class ProblemCodes
{
    // 400 Bad Request: the request itself is wrong.
    public const string ValidationFailed = "ValidationFailed";
    public const string MalformedRequest = "MalformedRequest";
    public const string IdempotencyKeyMissing = "IdempotencyKeyMissing";
    public const string IdempotencyKeyInvalid = "IdempotencyKeyInvalid";

    // 401 Unauthorized and 403 Forbidden: who is asking.
    public const string ApiKeyMissing = "ApiKeyMissing";
    public const string ApiKeyInvalid = "ApiKeyInvalid";
    public const string InsufficientRole = "InsufficientRole";

    // 404 Not Found.
    public const string AccountNotFound = "AccountNotFound";
    public const string TransactionNotFound = "TransactionNotFound";

    // Answered by ASP.NET Core itself: an unknown route, the wrong HTTP method, or a body that isn't JSON.
    public const string NotFound = "NotFound";
    public const string MethodNotAllowed = "MethodNotAllowed";
    public const string UnsupportedMediaType = "UnsupportedMediaType";

    // 409 Conflict: the request clashes with what's already in the books.
    public const string DuplicateAccountCode = "DuplicateAccountCode";
    public const string AlreadyReversed = "AlreadyReversed";

    // 422 Unprocessable Content: well-formed, but the books can't accept it.
    public const string UnknownAccount = "UnknownAccount";
    public const string ReversalOfReversal = "ReversalOfReversal";
    public const string IdempotencyKeyReused = "IdempotencyKeyReused";

    // 500: our fault, not the caller's.
    public const string InternalError = "InternalError";

    // Anything else ASP.NET Core answers by itself.
    public const string HttpError = "HttpError";
}

/// <summary>Builds the API's error responses: Problem Details (RFC 9457) with a "code" from <see cref="ProblemCodes"/>.</summary>
public static class Problems
{
    public const string RuleBrokenTitle = "The transaction breaks a ledger rule";

    public static ProblemDetails Create(int status, string code, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
        Extensions = { ["code"] = code },
    };

    /// <summary>The code for an error ASP.NET Core produced by itself, which has none of ours.</summary>
    public static string CodeFor(int? status) => status switch
    {
        StatusCodes.Status400BadRequest => ProblemCodes.MalformedRequest,
        StatusCodes.Status404NotFound => ProblemCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => ProblemCodes.MethodNotAllowed,
        StatusCodes.Status415UnsupportedMediaType => ProblemCodes.UnsupportedMediaType,
        >= StatusCodes.Status500InternalServerError => ProblemCodes.InternalError,
        _ => ProblemCodes.HttpError,
    };

    /// <summary>The same, as a result an endpoint can return.</summary>
    public static IResult Result(int status, string code, string title, string detail) =>
        TypedResults.Problem(Create(status, code, title, detail));

    /// <summary>A 400 listing every invalid field, keyed by its JSON name, such as "lines[1].amountMinor".</summary>
    public static IResult Validation(ValidationErrors errors) => TypedResults.ValidationProblem(
        errors.ToDictionary(),
        title: "The request has invalid values",
        extensions: new Dictionary<string, object?> { ["code"] = ProblemCodes.ValidationFailed });

    /// <summary>A broken accounting rule from the C# domain.</summary>
    public static ProblemDetails FromRule(LedgerRuleException broken) =>
        Create(StatusCodes.Status422UnprocessableEntity, broken.Rule.ToString(), RuleBrokenTitle, broken.Message);

    /// <summary>
    /// An error one of the stored procedures THROWs, turned into its response. Returns null for any other SQL error:
    /// those are our fault, and become a 500 without SQL Server's message, which could reveal the schema.
    /// </summary>
    public static ProblemDetails? FromSqlException(SqlException error) => error.Number switch
    {
        LedgerErrors.TooFewPostings => Rule(LedgerRule.TooFewPostings, error),
        LedgerErrors.ZeroAmountPosting => Rule(LedgerRule.ZeroAmountPosting, error),
        LedgerErrors.MixedCurrencies => Rule(LedgerRule.MixedCurrencies, error),
        LedgerErrors.AccountCurrencyMismatch => Rule(LedgerRule.AccountCurrencyMismatch, error),
        LedgerErrors.Unbalanced => Rule(LedgerRule.Unbalanced, error),
        LedgerErrors.UnknownAccount => Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.UnknownAccount,
            "Unknown account", error.Message),
        LedgerErrors.TransactionNotFound => Create(StatusCodes.Status404NotFound, ProblemCodes.TransactionNotFound,
            "Transaction not found", error.Message),
        LedgerErrors.AlreadyReversed => Create(StatusCodes.Status409Conflict, ProblemCodes.AlreadyReversed,
            "Already reversed", error.Message),
        LedgerErrors.ReversalOfReversal => Create(StatusCodes.Status422UnprocessableEntity, ProblemCodes.ReversalOfReversal,
            "A reversal can't be reversed", error.Message),
        _ => null,
    };

    /// <summary>A body that isn't JSON, or JSON that doesn't fit the request: a missing, misspelled, null or mistyped field.</summary>
    public static ProblemDetails FromUnreadableRequest(BadHttpRequestException error)
    {
        // JsonException.Path says where reading stopped, such as $.lines[0].side, without naming any C# types.
        var detail = error.InnerException is JsonException { Path: { } path }
            ? $"The JSON body can't be read at {path}. Check it's valid JSON and that no field is missing, misspelled, null or the wrong type."
            : "The request body is missing or isn't valid JSON.";

        return Create(StatusCodes.Status400BadRequest, ProblemCodes.MalformedRequest, "The request can't be read", detail);
    }

    private static ProblemDetails Rule(LedgerRule rule, SqlException error) =>
        Create(StatusCodes.Status422UnprocessableEntity, rule.ToString(), RuleBrokenTitle, error.Message);
}
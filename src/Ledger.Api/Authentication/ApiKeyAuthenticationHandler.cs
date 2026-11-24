using System.Security.Claims;
using System.Text.Encodings.Web;
using Ledger.Api.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ledger.Api.Authentication;

/// <summary>
/// Reads the X-Api-Key header and turns a known key into a signed-in caller, with their name and their role as claims.
/// No key or an unknown key gets a 401; a Read key on a Write endpoint gets a 403. Both answer with Problem Details.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyStore keys,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || string.IsNullOrEmpty(values.ToString()))
        {
            // No key at all. If the endpoint needs one, HandleChallengeAsync answers with a 401.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // The key itself is never logged: a key in a log file is a leaked key.
        if (values.Count != 1 || keys.Find(values.ToString()) is not { } caller)
        {
            return Task.FromResult(AuthenticateResult.Fail("The API key isn't one this API knows."));
        }

        Claim[] claims = [new(ClaimTypes.Name, caller.Name), new(ClaimTypes.Role, caller.Role.ToString())];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync();
        var problem = result.Failure is null
            ? Problems.Create(StatusCodes.Status401Unauthorized, ProblemCodes.ApiKeyMissing,
                "API key required", $"Send your API key in the {HeaderName} header.")
            : Problems.Create(StatusCodes.Status401Unauthorized, ProblemCodes.ApiKeyInvalid,
                "API key not recognised", $"The key in the {HeaderName} header isn't one this API knows.");

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // HTTP requires a 401 to say how to authenticate. There's no standard scheme for API keys, so this names ours.
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
        await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = Context, ProblemDetails = problem });
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        var problem = Problems.Create(StatusCodes.Status403Forbidden, ProblemCodes.InsufficientRole,
            "This API key can't do that", "This key can only read. Opening accounts, posting and reversing need a key with the Write role.");

        Response.StatusCode = StatusCodes.Status403Forbidden;
        await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = Context, ProblemDetails = problem });
    }
}
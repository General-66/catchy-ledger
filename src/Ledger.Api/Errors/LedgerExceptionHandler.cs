using Ledger.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Ledger.Api.Errors;

/// <summary>
/// The one place exceptions become responses. A broken ledger rule, from C# or from a stored procedure, is a 422;
/// an unreadable body is a 400; anything else is our own bug: a 500 that's logged, with no internals in the response.
/// </summary>
public sealed class LedgerExceptionHandler(IProblemDetailsService problemDetails, ILogger<LedgerExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            LedgerRuleException broken => Problems.FromRule(broken),
            SqlException sql => Problems.FromSqlException(sql),
            BadHttpRequestException unreadable => Problems.FromUnreadableRequest(unreadable),
            _ => null,
        };

        if (problem is null)
        {
            logger.LogError(exception, "Unexpected error in {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
            problem = Problems.Create(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError,
                "Something went wrong on our side", "The error has been logged. Quote the traceId if you report it.");
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }
}
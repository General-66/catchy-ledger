using System.Text.Json.Serialization;
using Ledger.Api;
using Ledger.Api.Accounts;
using Ledger.Api.Authentication;
using Ledger.Api.Errors;
using Ledger.Api.OpenApi;
using Ledger.Api.Transactions;
using Ledger.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// JSON reads strictly: a missing, misspelled or null field is a 400, never a silent default, and a number must be
// a JSON number, not a string. Enums travel as words: "Debit", "Liability".
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});

// Every error is Problem Details JSON with a "code" (see Errors/Problems.cs), whatever produced it. Errors ASP.NET
// Core answers by itself, such as a 404 for an unknown route, get a code here.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
    context.ProblemDetails.Extensions.TryAdd("code", Problems.CodeFor(context.ProblemDetails.Status));
});
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

// The books. The API connects as its own login, a member of the ledger_app role, never as sa.
builder.Services.AddSingleton(services => new SqlLedger(
    services.GetRequiredService<IConfiguration>().GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("ConnectionStrings:Ledger is missing. Add it with Manage User Secrets on Ledger.Api.")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BusinessCalendar>();

// API keys: X-Api-Key names the caller, whose role decides what they may do.
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);
builder.Services.AddAuthorizationBuilder()
    // Secure by default: an endpoint that forgets to name a policy still needs a valid key.
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(LedgerPolicies.Read, policy => policy.RequireRole(nameof(ApiRole.Read), nameof(ApiRole.Write)))
    .AddPolicy(LedgerPolicies.Write, policy => policy.RequireRole(nameof(ApiRole.Write)));

// OpenAPI describes every endpoint as JSON at /openapi/v1.json; Scalar shows it as browsable docs at /scalar.
builder.Services.AddOpenApi(options => options
    .AddDocumentTransformer<LedgerOpenApiTransformer>()
    .AddOperationTransformer<LedgerOpenApiTransformer>());

// /health is Healthy only when the database answers too.
builder.Services.AddHealthChecks().AddCheck<LedgerDatabaseHealthCheck>("database");

var app = builder.Build();

// Stop at startup, with a clear message, if the API keys or the connection string are missing or wrong.
app.Services.GetRequiredService<ApiKeyStore>();
app.Services.GetRequiredService<SqlLedger>();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Public: the docs and the health check. Everything else needs an API key.
app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference(options => options
        .WithTitle("Catchy Ledger API")
        .AddPreferredSecuritySchemes(ApiKeyAuthenticationHandler.SchemeName))
    .AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();

app.MapAccountEndpoints();
app.MapTransactionEndpoints();

app.Run();
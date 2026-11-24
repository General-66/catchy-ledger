using Ledger.Api.Authentication;
using Ledger.Api.Transactions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ledger.Api.OpenApi;

/// <summary>
/// Tells the OpenAPI document, and so the Scalar docs, two things the endpoints can't say for themselves:
/// every call needs an X-Api-Key header, and the Idempotency-Key header is required wherever it appears.
/// </summary>
public sealed class LedgerOpenApiTransformer : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "Catchy Ledger API";
        document.Info.Description = "A double-entry ledger: every transaction's debits equal its credits. "
            + "Amounts are whole minor units (cents): 50000 is R500.00.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthenticationHandler.HeaderName,
            Description = "Your API key. A Write key can open accounts, post and reverse; a Read key can only use GET.",
        };

        // Applies the scheme to every operation in the document.
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(ApiKeyAuthenticationHandler.SchemeName, document)] = [],
        });

        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        // The endpoints take the header as an optional string so they can answer a missing one with a clear 400.
        // In the docs it's required, because it is.
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is OpenApiParameter { Name: IdempotentPosting.KeyHeader } idempotencyKey)
            {
                idempotencyKey.Required = true;
                idempotencyKey.Description = "A new random value, such as a GUID, for each operation. Send the same value when you retry it.";
            }
        }

        return Task.CompletedTask;
    }
}
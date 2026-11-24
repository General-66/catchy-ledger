using Ledger.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Api;

/// <summary>
/// /health is only "Healthy" if the API can reach the books with its own login. Azure takes an Unhealthy instance out
/// of service, so a lost database shows up as a failed health check instead of as failed payments.
/// </summary>
public sealed class LedgerDatabaseHealthCheck(SqlLedger ledger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await ledger.CheckConnectionAsync(cancellationToken);
            return HealthCheckResult.Healthy("The ledger database answered.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The ledger database can't be reached.", error);
        }
    }
}
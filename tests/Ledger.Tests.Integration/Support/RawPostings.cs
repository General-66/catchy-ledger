using System.Data;
using Dapper;
using Ledger.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Ledger.Tests.Integration.Support;

/// <summary>
/// Calls dbo.usp_PostTransaction directly with any lines at all, including ones Transaction.Create would refuse.
/// It plays a caller that skips the C# domain, to prove the database checks the rules on its own.
/// </summary>
internal static class RawPostings
{
    public static async Task<long> PostAsync(string connectionString, params (long AccountId, long AmountMinor, string Currency)[] lines)
    {
        using var table = new DataTable();
        table.Columns.Add("LineNumber", typeof(int));
        table.Columns.Add("AccountId", typeof(long));
        table.Columns.Add("AmountMinor", typeof(long));
        table.Columns.Add("Currency", typeof(string));
        for (var i = 0; i < lines.Length; i++)
        {
            table.Rows.Add(i + 1, lines[i].AccountId, lines[i].AmountMinor, lines[i].Currency);
        }

        var parameters = new DynamicParameters();
        parameters.Add("@CreatedBy", TestRequests.Caller);
        parameters.Add("@IdempotencyKey", Guid.NewGuid().ToString());
        parameters.Add("@RequestHash", RequestInfo.HashOf("{}"), DbType.Binary, size: 32);
        parameters.Add("@Description", "Posted directly, without the C# domain");
        parameters.Add("@EffectiveDate", TestBooks.Day.ToDateTime(TimeOnly.MinValue), DbType.Date);
        parameters.Add("@Lines", table.AsTableValuedParameter("dbo.PostingLineList"));
        parameters.Add("@TransactionId", dbType: DbType.Int64, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(connectionString);
        await connection.ExecuteAsync("dbo.usp_PostTransaction", parameters, commandType: CommandType.StoredProcedure);
        return parameters.Get<long>("@TransactionId");
    }
}
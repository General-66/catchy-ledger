using System.Data;
using Dapper;
using Ledger.Domain;
using Microsoft.Data.SqlClient;

namespace Ledger.Infrastructure;

/// <summary>
/// The ledger in SQL Server. Money only moves through the two stored procedures; this class turns
/// domain objects into procedure calls and reads the results back. It opens a short-lived connection
/// per call: connection pooling makes that cheap.
/// </summary>
public sealed class SqlLedger
{
    /// <summary>The longest description the Transactions table stores (nvarchar(200)).</summary>
    public const int MaxDescriptionLength = 200;

    private readonly string _connectionString;

    public SqlLedger(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>Opens an account and returns it with the Id SQL Server gave it.</summary>
    public async Task<Account> CreateAccountAsync(
        string code, string name, AccountType type, Currency currency, bool allowOverdraft, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(currency);

        const string sql = """
            INSERT INTO dbo.Accounts (Code, Name, Type, Currency, AllowOverdraft)
            OUTPUT INSERTED.Id
            VALUES (@Code, @Name, @Type, @Currency, @AllowOverdraft);
            """;

        await using var connection = new SqlConnection(_connectionString);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new { Code = code, Name = name, Type = type.ToString(), Currency = currency.Code, AllowOverdraft = allowOverdraft },
            cancellationToken: cancellationToken));

        return new Account(id, code, name, type, currency, allowOverdraft);
    }

    /// <summary>Finds an account by its Id, or returns null if there's none.</summary>
    public async Task<Account?> GetAccountAsync(long id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Code, Name, Type, Currency, AllowOverdraft
            FROM dbo.Accounts
            WHERE Id = @Id;
            """;

        await using var connection = new SqlConnection(_connectionString);
        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(
            sql, new { Id = id }, cancellationToken: cancellationToken));

        return row is null
            ? null
            : new Account(row.Id, row.Code, row.Name, Enum.Parse<AccountType>(row.Type), Currency.FromCode(row.Currency), row.AllowOverdraft);
    }

    /// <summary>
    /// Posts a balanced transaction through dbo.usp_PostTransaction and returns its new Id.
    /// The database checks every rule again; if one fails, the SqlException's Number is one of <see cref="LedgerErrors"/>.
    /// </summary>
    public async Task<long> PostAsync(Transaction transaction, RequestInfo request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            transaction.Description.Length, MaxDescriptionLength, nameof(transaction) + ".Description");

        using var lines = ToPostingLines(transaction);

        var parameters = WithRequest(request);
        parameters.Add("@Description", transaction.Description);
        parameters.Add("@EffectiveDate", transaction.EffectiveDate.ToDateTime(TimeOnly.MinValue), DbType.Date);
        parameters.Add("@Lines", lines.AsTableValuedParameter("dbo.PostingLineList"));
        parameters.Add("@TransactionId", dbType: DbType.Int64, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.usp_PostTransaction", parameters, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));

        return parameters.Get<long>("@TransactionId");
    }

    /// <summary>
    /// Posts the mirror image of a transaction through dbo.usp_ReverseTransaction and returns the reversal's Id.
    /// A transaction can be reversed only once, and a reversal can't be reversed.
    /// </summary>
    public async Task<long> ReverseAsync(
        long originalTransactionId, DateOnly effectiveDate, RequestInfo request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = WithRequest(request);
        parameters.Add("@OriginalTransactionId", originalTransactionId);
        parameters.Add("@EffectiveDate", effectiveDate.ToDateTime(TimeOnly.MinValue), DbType.Date);
        parameters.Add("@TransactionId", dbType: DbType.Int64, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.usp_ReverseTransaction", parameters, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken));

        return parameters.Get<long>("@TransactionId");
    }

    /// <summary>Reads a stored transaction and its lines, in the order they were posted, or returns null if there's none.</summary>
    public async Task<PostedTransaction?> GetTransactionAsync(long id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, CreatedBy, Description, EffectiveDate, CreatedAt, ReversesTransactionId
            FROM dbo.Transactions
            WHERE Id = @Id;

            SELECT Id, AccountId, AmountMinor, Currency
            FROM dbo.Postings
            WHERE TransactionId = @Id
            ORDER BY Id;
            """;

        await using var connection = new SqlConnection(_connectionString);
        using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            sql, new { Id = id }, cancellationToken: cancellationToken));

        var header = await results.ReadSingleOrDefaultAsync<TransactionRow>();
        var lines = await results.ReadAsync<PostingRow>();
        if (header is null)
        {
            return null;
        }

        return new PostedTransaction(
            header.Id,
            header.CreatedBy,
            header.Description,
            DateOnly.FromDateTime(header.EffectiveDate),
            header.CreatedAt,
            header.ReversesTransactionId,
            [.. lines.Select(line => new PostedLine(line.Id, line.AccountId, line.AmountMinor, line.Currency))]);
    }

    /// <summary>
    /// An account's balance as stored: the sum of every posting on it, signs included. Read it the way a person
    /// would with <see cref="Account.BalanceOnNormalSide"/>. Phase 4 replaces this sum with a stored balance.
    /// </summary>
    public async Task<Money> GetStoredBalanceAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        const string sql = """
            SELECT COALESCE(SUM(AmountMinor), 0)
            FROM dbo.Postings
            WHERE AccountId = @AccountId;
            """;

        await using var connection = new SqlConnection(_connectionString);
        var amountMinor = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql, new { AccountId = account.Id }, cancellationToken: cancellationToken));

        return new Money(amountMinor, account.Currency);
    }

    /// <summary>
    /// Finds several accounts in one round trip, keyed by Id. An Id with no account is simply missing from the result,
    /// which is how the API spots a posting line that names an account that doesn't exist.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, Account>> GetAccountsAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        long[] distinctIds = [.. ids.Distinct()];
        if (distinctIds.Length == 0)
        {
            return new Dictionary<long, Account>();
        }

        // Dapper expands "IN @Ids" into one parameter per Id: IN (@Ids1, @Ids2, ...).
        const string sql = """
            SELECT Id, Code, Name, Type, Currency, AllowOverdraft
            FROM dbo.Accounts
            WHERE Id IN @Ids;
            """;

        await using var connection = new SqlConnection(_connectionString);
        var rows = await connection.QueryAsync<AccountRow>(new CommandDefinition(
            sql, new { Ids = distinctIds }, cancellationToken: cancellationToken));

        return rows.ToDictionary(
            row => row.Id,
            row => new Account(row.Id, row.Code, row.Name, Enum.Parse<AccountType>(row.Type), Currency.FromCode(row.Currency), row.AllowOverdraft));
    }

    /// <summary>
    /// Finds the transaction a caller already posted with this Idempotency-Key, or returns null if the key is new.
    /// The request hash comes with it, so the API can tell a retry from a different request that reuses the key.
    /// </summary>
    public async Task<IdempotencyRecord?> FindByIdempotencyKeyAsync(
        string createdBy, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // UQ_Transactions_CreatedBy_IdempotencyKey makes this an index seek, and guarantees at most one row.
        const string sql = """
            SELECT Id, RequestHash
            FROM dbo.Transactions
            WHERE CreatedBy = @CreatedBy AND IdempotencyKey = @IdempotencyKey;
            """;

        await using var connection = new SqlConnection(_connectionString);
        var row = await connection.QuerySingleOrDefaultAsync<IdempotencyRow>(new CommandDefinition(
            sql, new { CreatedBy = createdBy, IdempotencyKey = idempotencyKey }, cancellationToken: cancellationToken));

        return row is null ? null : new IdempotencyRecord(row.Id, row.RequestHash);
    }

    /// <summary>Proves the app can reach the books with its own login. The /health endpoint calls this.</summary>
    public async Task CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP (1) 1 FROM dbo.Postings;", cancellationToken: cancellationToken));
    }
    // The three values every write carries, as procedure parameters.
    private static DynamicParameters WithRequest(RequestInfo request)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@CreatedBy", request.CreatedBy);
        parameters.Add("@IdempotencyKey", request.IdempotencyKey);
        parameters.Add("@RequestHash", request.RequestHash.ToArray(), DbType.Binary, size: 32);
        return parameters;
    }

    // Builds the table-valued parameter. Its columns must be in the same order as dbo.PostingLineList,
    // because SQL Server matches them by position, not by name.
    private static DataTable ToPostingLines(Transaction transaction)
    {
        var table = new DataTable();
        table.Columns.Add("LineNumber", typeof(int));
        table.Columns.Add("AccountId", typeof(long));
        table.Columns.Add("AmountMinor", typeof(long));
        table.Columns.Add("Currency", typeof(string));

        var lineNumber = 1;
        foreach (var posting in transaction.Postings)
        {
            table.Rows.Add(lineNumber++, posting.Account.Id, posting.Amount.AmountMinor, posting.Amount.Currency.Code);
        }

        return table;
    }

    // Rows exactly as SQL Server returns them. Dapper fills these by column name.
    private sealed class AccountRow
    {
        public long Id { get; init; }

        public string Code { get; init; } = "";

        public string Name { get; init; } = "";

        public string Type { get; init; } = "";

        public string Currency { get; init; } = "";

        public bool AllowOverdraft { get; init; }
    }

    private sealed class TransactionRow
    {
        public long Id { get; init; }

        public string CreatedBy { get; init; } = "";

        public string Description { get; init; } = "";

        public DateTime EffectiveDate { get; init; }

        public DateTime CreatedAt { get; init; }

        public long? ReversesTransactionId { get; init; }
    }

    private sealed class PostingRow
    {
        public long Id { get; init; }

        public long AccountId { get; init; }

        public long AmountMinor { get; init; }

        public string Currency { get; init; } = "";
    }

    private sealed class IdempotencyRow
    {
        public long Id { get; init; }

        public byte[] RequestHash { get; init; } = [];
    }
}
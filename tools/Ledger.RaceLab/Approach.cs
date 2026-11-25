using System.Data;
using Dapper;
using Ledger.Domain;
using Ledger.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Ledger.RaceLab;

/// <summary>One bet: a stake moving from a wallet to Open Bets.</summary>
internal sealed record LabBet(Account Wallet, Account OpenBets, long StakeMinor);

/// <summary>One way of placing a bet while other bets may be placing theirs at the same moment.</summary>
internal sealed record Approach(string Name, string HowItWorks, Func<LabBet, SqlRetryPolicy, Task> PlaceBetAsync)
{
    /// <summary>What lab.usp_PlaceBet_Optimistic throws when the wallet changed after it was read.</summary>
    public const int Conflict = 50030;

    /// <summary>The four approaches from the plan, then the real procedure the API runs.</summary>
    public static IReadOnlyList<Approach> All(string connectionString) =>
    [
        Lab("READ COMMITTED",
            "Reads the balance, checks it, then acts. Nothing stops two bets reading the same balance before either writes.",
            "lab.usp_PlaceBet_ReadCommitted", connectionString),
        Lab("SERIALIZABLE",
            "The same code at the strictest isolation level. Every read keeps a shared lock until COMMIT; deadlocks are retried.",
            "lab.usp_PlaceBet_Serializable", connectionString),
        Lab("UPDLOCK, HOLDLOCK",
            "Locks the wallet's balance while reading it. Other bets queue until the holder commits.",
            "lab.usp_PlaceBet_Pessimistic", connectionString),
        Lab("rowversion + retry",
            "Reads without locking, then updates only if the row's version is unchanged; otherwise reads again and retries.",
            "lab.usp_PlaceBet_Optimistic", connectionString),
        Shipped(connectionString),
    ];

    private static Approach Lab(string name, string howItWorks, string procedure, string connectionString) =>
        new(name, howItWorks, async (bet, retryPolicy) =>
        {
            // One Idempotency-Key for every attempt: a retry is the same bet, not a new one.
            var key = Guid.NewGuid().ToString();

            await retryPolicy.ExecuteAsync(async () =>
            {
                await using var connection = new SqlConnection(connectionString);
                return await connection.ExecuteAsync(
                    procedure,
                    new { WalletId = bet.Wallet.Id, OpenBetsId = bet.OpenBets.Id, bet.StakeMinor, IdempotencyKey = key },
                    commandType: CommandType.StoredProcedure);
            });
        });

    // The real thing: a domain Transaction through SqlLedger and dbo.usp_PostTransaction, exactly as the API posts.
    private static Approach Shipped(string connectionString) =>
        new("dbo.usp_PostTransaction",
            "The procedure the API runs, called through SqlLedger exactly as the API calls it, deadlock retries included.",
            async (bet, retryPolicy) =>
            {
                var ledger = new SqlLedger(connectionString, retryPolicy);
                var key = Guid.NewGuid().ToString();
                var stake = new Money(bet.StakeMinor, Currency.ZAR);
                var transaction = Transaction.Create(
                    "Lab bet", DateOnly.FromDateTime(DateTime.UtcNow), Posting.Debit(bet.Wallet, stake), Posting.Credit(bet.OpenBets, stake));

                await ledger.PostAsync(transaction, new RequestInfo("race-lab", key, RequestInfo.HashOf(key)));
            });
}
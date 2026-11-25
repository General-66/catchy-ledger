using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using Dapper;
using Ledger.Domain;
using Ledger.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Ledger.RaceLab;

/// <summary>A fresh set of accounts for one run: a Bank, Open Bets, and wallets that are already funded.</summary>
internal sealed record LabBooks(Account Bank, Account OpenBets, IReadOnlyList<Account> Wallets)
{
    public static async Task<LabBooks> OpenAsync(string connectionString, int wallets, long depositEachMinor)
    {
        var ledger = new SqlLedger(connectionString);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var bank = await ledger.CreateAccountAsync($"1000-{suffix}", "Lab Bank", AccountType.Asset, Currency.ZAR, allowOverdraft: true);
        var openBets = await ledger.CreateAccountAsync($"2100-{suffix}", "Lab Open Bets", AccountType.Liability, Currency.ZAR, allowOverdraft: true);

        var opened = new List<Account>();
        await using var connection = new SqlConnection(connectionString);
        for (var number = 1; number <= wallets; number++)
        {
            var wallet = await ledger.CreateAccountAsync(
                $"2000-{suffix}-{number:D2}", $"Lab Wallet {number}", AccountType.Liability, Currency.ZAR, allowOverdraft: false);

            // Funded by the lab's own deposit procedure, which moves the stored balances itself, before Step 5 and after.
            await connection.ExecuteAsync(
                "lab.usp_Deposit",
                new { BankId = bank.Id, WalletId = wallet.Id, AmountMinor = depositEachMinor, IdempotencyKey = Guid.NewGuid().ToString() },
                commandType: CommandType.StoredProcedure);

            opened.Add(wallet);
        }

        return new LabBooks(bank, openBets, opened);
    }

    public long[] AccountIds => [Bank.Id, OpenBets.Id, .. Wallets.Select(wallet => wallet.Id)];
}

/// <summary>How one bet ended.</summary>
internal enum BetOutcome
{
    /// <summary>Posted.</summary>
    Accepted,

    /// <summary>Refused with error 50006: the wallet couldn't cover it.</summary>
    Refused,

    /// <summary>Still deadlocked or in conflict after the last retry.</summary>
    GaveUp,

    /// <summary>Any other error. The report lists the first one.</summary>
    Failed,
}

/// <summary>What happened in one scenario, for one approach.</summary>
internal sealed record ScenarioResult(
    int Bets,
    int Accepted,
    int Refused,
    int GaveUp,
    int Failed,
    string? FirstFailure,
    int Deadlocks,
    int Conflicts,
    TimeSpan Elapsed,
    long LowestWalletMinor,
    int OverdrawnWallets,
    bool BalancesAgree);

/// <summary>Counts the retries a run needed, by reason.</summary>
internal sealed class RetryTally
{
    private int _deadlocks;
    private int _conflicts;

    public int Deadlocks => _deadlocks;

    public int Conflicts => _conflicts;

    /// <summary>
    /// The lab's retry policy: deadlocks and optimistic conflicts, up to 10 attempts per bet, each one counted.
    /// More attempts than the API's 5, so that every approach gets a fair chance to finish its bets.
    /// </summary>
    public SqlRetryPolicy Policy() => new()
    {
        MaxAttempts = 10,
        RetryOn = [LedgerErrors.Deadlock, Approach.Conflict],
        OnRetry = (error, _) =>
        {
            if (error.Number == LedgerErrors.Deadlock)
            {
                Interlocked.Increment(ref _deadlocks);
            }
            else
            {
                Interlocked.Increment(ref _conflicts);
            }
        },
    };
}

/// <summary>The two races the lab runs against every approach.</summary>
internal static class Scenarios
{
    public const int RaceBets = 100;
    public const long RaceDepositMinor = 100_00;
    public const long RaceStakeMinor = 80_00;

    public const int EveningWallets = 50;
    public const int EveningBets = 500;
    public const int EveningAtOnce = 50;
    public const long EveningDepositMinor = 1_000_00;
    public const long EveningStakeMinor = 10_00;

    /// <summary>
    /// The plan's race: a hundred R80 bets at the same moment against one wallet holding R100. Exactly one can be
    /// covered. Every bet is lined up first and released together, like runners at a starting gun.
    /// </summary>
    public static async Task<ScenarioResult> TheRaceAsync(string connectionString, Approach approach)
    {
        var books = await LabBooks.OpenAsync(connectionString, wallets: 1, RaceDepositMinor);
        var tally = new RetryTally();
        var retryPolicy = tally.Policy();
        var failures = new ConcurrentQueue<string>();
        var startingGun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var bets = Enumerable.Range(0, RaceBets).Select(async _ =>
        {
            await startingGun.Task;
            return await OutcomeOfAsync(
                () => approach.PlaceBetAsync(new LabBet(books.Wallets[0], books.OpenBets, RaceStakeMinor), retryPolicy), failures);
        }).ToArray();

        var stopwatch = Stopwatch.StartNew();
        startingGun.SetResult();
        var outcomes = await Task.WhenAll(bets);
        stopwatch.Stop();

        return await SummariseAsync(connectionString, books, outcomes, failures, tally, stopwatch.Elapsed);
    }

    /// <summary>
    /// Ordinary load: fifty wallets of R1,000 each, and 500 R10 bets spread across them, fifty at a time. No wallet can
    /// run out, so every bet should be accepted. What differs is how long it takes, and how much is retried.
    /// </summary>
    public static async Task<ScenarioResult> BusyEveningAsync(string connectionString, Approach approach)
    {
        var books = await LabBooks.OpenAsync(connectionString, EveningWallets, EveningDepositMinor);
        var tally = new RetryTally();
        var retryPolicy = tally.Policy();
        var failures = new ConcurrentQueue<string>();
        var outcomes = new BetOutcome[EveningBets];

        var stopwatch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, EveningBets),
            new ParallelOptions { MaxDegreeOfParallelism = EveningAtOnce },
            async (bet, _) =>
            {
                // Bet 0 on wallet 0, bet 1 on wallet 1, and so on round: bets placed at the same time are mostly on
                // different wallets, as on a real evening. Every bet still moves the one shared Open Bets account.
                var wallet = books.Wallets[bet % EveningWallets];
                outcomes[bet] = await OutcomeOfAsync(
                    () => approach.PlaceBetAsync(new LabBet(wallet, books.OpenBets, EveningStakeMinor), retryPolicy), failures);
            });
        stopwatch.Stop();

        return await SummariseAsync(connectionString, books, outcomes, failures, tally, stopwatch.Elapsed);
    }

    private static async Task<BetOutcome> OutcomeOfAsync(Func<Task> placeBet, ConcurrentQueue<string> failures)
    {
        try
        {
            await placeBet();
            return BetOutcome.Accepted;
        }
        catch (SqlException error) when (error.Number == LedgerErrors.InsufficientFunds)
        {
            return BetOutcome.Refused;
        }
        catch (SqlException error) when (error.Number is LedgerErrors.Deadlock or Approach.Conflict)
        {
            return BetOutcome.GaveUp;
        }
        catch (Exception error)
        {
            // Anything else is recorded, not thrown, so one broken bet can't stop the whole run.
            failures.Enqueue($"{error.GetType().Name}: {error.Message}");
            return BetOutcome.Failed;
        }
    }

    // Reads every account in the run twice: as stored in AccountBalances, and added up from the postings, which are
    // the truth. They must agree. A wallet is a liability, so its balance on its normal side is the sum times -1.
    private static async Task<ScenarioResult> SummariseAsync(
        string connectionString, LabBooks books, BetOutcome[] outcomes, ConcurrentQueue<string> failures, RetryTally tally, TimeSpan elapsed)
    {
        const string sql = """
            SELECT b.AccountId, b.BalanceMinor AS Stored, COALESCE(p.Total, 0) AS FromPostings
            FROM dbo.AccountBalances AS b
            LEFT JOIN (
                SELECT AccountId, SUM(AmountMinor) AS Total
                FROM dbo.Postings
                WHERE AccountId IN @Ids
                GROUP BY AccountId) AS p ON p.AccountId = b.AccountId
            WHERE b.AccountId IN @Ids;
            """;

        await using var connection = new SqlConnection(connectionString);
        var balances = (await connection.QueryAsync<BalanceRow>(sql, new { Ids = books.AccountIds })).ToList();

        var walletIds = books.Wallets.Select(wallet => wallet.Id).ToHashSet();
        long[] wallets = [.. balances.Where(row => walletIds.Contains(row.AccountId)).Select(row => -row.FromPostings)];

        return new ScenarioResult(
            Bets: outcomes.Length,
            Accepted: outcomes.Count(outcome => outcome == BetOutcome.Accepted),
            Refused: outcomes.Count(outcome => outcome == BetOutcome.Refused),
            GaveUp: outcomes.Count(outcome => outcome == BetOutcome.GaveUp),
            Failed: outcomes.Count(outcome => outcome == BetOutcome.Failed),
            FirstFailure: failures.TryPeek(out var first) ? first : null,
            Deadlocks: tally.Deadlocks,
            Conflicts: tally.Conflicts,
            Elapsed: elapsed,
            LowestWalletMinor: wallets.Min(),
            OverdrawnWallets: wallets.Count(balance => balance < 0),
            BalancesAgree: balances.Count == books.AccountIds.Length && balances.TrueForAll(row => row.Stored == row.FromPostings));
    }

    private sealed class BalanceRow
    {
        public long AccountId { get; init; }

        public long Stored { get; init; }

        public long FromPostings { get; init; }
    }
}
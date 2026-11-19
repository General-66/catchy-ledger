using FsCheck;
using FsCheck.Fluent;
using Ledger.Domain;
using static Ledger.Tests.Unit.Support.TestMoney;

namespace Ledger.Tests.Unit.Support;

/// <summary>Recipes FsCheck uses to make random postings for the property tests.</summary>
internal static class LedgerGenerators
{
    // Generated postings land on any of the betting accounts. The rules don't care which.
    private static readonly Account[] Accounts =
        [BettingChart.Bank, BettingChart.OpenBets, BettingChart.BettingRevenue, BettingChart.Payouts, BettingChart.CustomerWallet];

    /// <summary>A random account and an amount from 1 cent to R10,000.00. Never zero: that's a different rule.</summary>
    private static Gen<(Account Account, long Cents)> Line() =>
        from account in Gen.Elements(Accounts)
        from cents in Gen.Choose(1, 1_000_000)
        select (Account: account, Cents: (long)cents);

    /// <summary>One to four debits and one to four credits whose totals match exactly, in a random order.</summary>
    public static Gen<Posting[]> BalancedPostings() =>
        from debitCount in Gen.Choose(1, 4)
        from creditCount in Gen.Choose(1, 4)
        from debits in Line().ArrayOf(debitCount)
        from credits in Line().ArrayOf(creditCount)
        from shuffled in Gen.Shuffle(MakeBalanced(debits, credits))
        select shuffled;

    /// <summary>Balanced postings with one amount nudged by up to R10 either way, so the total is off.</summary>
    public static Gen<Posting[]> UnbalancedPostings() =>
        from postings in BalancedPostings()
        from index in Gen.Choose(0, postings.Length - 1)
        from nudge in Gen.Choose(-1_000, 1_000)
        where nudge != 0 && postings[index].Amount.AmountMinor + nudge != 0 // don't create a zero posting
        select Nudge(postings, index, nudge);

    // The two sides start with different totals. Adding the gap to the first line of the
    // smaller side makes them equal, without ever making a line zero.
    private static Posting[] MakeBalanced((Account Account, long Cents)[] debits, (Account Account, long Cents)[] credits)
    {
        var gap = debits.Sum(line => line.Cents) - credits.Sum(line => line.Cents);
        if (gap > 0)
        {
            credits[0].Cents += gap;
        }
        else
        {
            debits[0].Cents -= gap;
        }

        return
        [
            .. debits.Select(line => Posting.Debit(line.Account, Zar(line.Cents))),
            .. credits.Select(line => Posting.Credit(line.Account, Zar(line.Cents))),
        ];
    }

    private static Posting[] Nudge(Posting[] postings, int index, int nudge)
    {
        Posting[] nudged = [.. postings];
        var original = nudged[index];
        nudged[index] = new Posting(original.Account, original.Amount + Zar(nudge));
        return nudged;
    }
}
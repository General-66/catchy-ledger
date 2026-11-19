using Ledger.Domain;
using Ledger.Tests.Unit.Support;
using static Ledger.Tests.Unit.Support.BettingChart;
using static Ledger.Tests.Unit.Support.TestMoney;

namespace Ledger.Tests.Unit;

/// <summary>The worked example from the plan: a customer deposits R500, bets R100, and the bet loses or wins.</summary>
public class BettingWalletTests
{
    private static readonly DateOnly Day = new(2026, 11, 19);

    // Step 1: the customer deposits R500. The bank (asset) goes up; so does what the operator owes them.
    private static Transaction Deposit(long cents) => Transaction.Create(
        "Customer deposit",
        Day,
        Posting.Debit(Bank, Zar(cents)),
        Posting.Credit(CustomerWallet, Zar(cents)));

    // Step 2: the customer bets. The stake moves from the wallet to Open Bets until the result is known.
    private static Transaction PlaceBet(long stake) => Transaction.Create(
        "Bet placed",
        Day,
        Posting.Debit(CustomerWallet, Zar(stake)),
        Posting.Credit(OpenBets, Zar(stake)));

    // Step 3a: the bet loses. The operator no longer owes the stake to anyone; it's revenue.
    private static Transaction BetLoses(long stake) => Transaction.Create(
        "Bet lost",
        Day,
        Posting.Debit(OpenBets, Zar(stake)),
        Posting.Credit(BettingRevenue, Zar(stake)));

    // Step 3b: the bet wins. The stake comes back out of Open Bets, the operator pays the rest as an
    // expense, and the whole payout goes to the wallet. Three lines, still summing to zero.
    private static Transaction BetWins(long stake, long payout) => Transaction.Create(
        "Bet won",
        Day,
        Posting.Debit(OpenBets, Zar(stake)),
        Posting.Debit(Payouts, Zar(payout - stake)),
        Posting.Credit(CustomerWallet, Zar(payout)));

    [Fact]
    public void Step_1_a_deposit_debits_the_bank_and_credits_the_wallet()
    {
        var books = new Books();

        books.Post(Deposit(500_00));

        Assert.Equal(Zar(500_00), books.StoredBalance(Bank));
        Assert.Equal(Zar(-500_00), books.StoredBalance(CustomerWallet));
        Assert.Equal(Zar(0), books.TrialBalance(Currency.ZAR));
    }

    [Fact]
    public void After_step_1_the_bank_and_the_wallet_both_read_R500_on_their_normal_side()
    {
        var books = new Books();
        books.Post(Deposit(500_00));

        var bankStored = books.StoredBalance(Bank);
        var walletStored = books.StoredBalance(CustomerWallet);

        // Stored: +50000 and -50000. Read on their normal sides: R500 each.
        Assert.Equal(50_000L, bankStored.AmountMinor);
        Assert.Equal(-50_000L, walletStored.AmountMinor);
        Assert.Equal(Zar(500_00), Bank.BalanceOnNormalSide(bankStored));
        Assert.Equal(Zar(500_00), CustomerWallet.BalanceOnNormalSide(walletStored));
    }

    [Fact]
    public void Step_2_a_bet_moves_the_stake_from_the_wallet_to_open_bets()
    {
        var books = new Books();
        books.Post(Deposit(500_00));

        books.Post(PlaceBet(100_00));

        Assert.Equal(Zar(500_00), books.StoredBalance(Bank));
        Assert.Equal(Zar(400_00), CustomerWallet.BalanceOnNormalSide(books.StoredBalance(CustomerWallet)));
        Assert.Equal(Zar(100_00), OpenBets.BalanceOnNormalSide(books.StoredBalance(OpenBets)));
        Assert.Equal(Zar(0), books.TrialBalance(Currency.ZAR));
    }

    [Fact]
    public void Step_3a_a_losing_bet_turns_the_stake_into_revenue()
    {
        var books = new Books();
        books.Post(Deposit(500_00));
        books.Post(PlaceBet(100_00));

        books.Post(BetLoses(100_00));

        Assert.Equal(Zar(0), books.StoredBalance(OpenBets));
        Assert.Equal(Zar(100_00), BettingRevenue.BalanceOnNormalSide(books.StoredBalance(BettingRevenue)));
        Assert.Equal(Zar(400_00), CustomerWallet.BalanceOnNormalSide(books.StoredBalance(CustomerWallet)));
        Assert.Equal(Zar(0), books.TrialBalance(Currency.ZAR));
    }

    [Fact]
    public void Step_3b_a_winning_bet_is_three_postings_that_sum_to_zero()
    {
        var win = BetWins(stake: 100_00, payout: 250_00);

        long[] stored = [.. win.Postings.Select(posting => posting.Amount.AmountMinor)];

        Assert.Equal(new long[] { 10_000, 15_000, -25_000 }, stored);
        Assert.Equal(0L, stored.Sum());
    }

    [Fact]
    public void Step_3b_a_winning_bet_pays_R250_into_the_wallet()
    {
        var books = new Books();
        books.Post(Deposit(500_00));
        books.Post(PlaceBet(100_00));

        books.Post(BetWins(stake: 100_00, payout: 250_00));

        Assert.Equal(Zar(0), books.StoredBalance(OpenBets));
        Assert.Equal(Zar(150_00), Payouts.BalanceOnNormalSide(books.StoredBalance(Payouts)));
        Assert.Equal(Zar(650_00), CustomerWallet.BalanceOnNormalSide(books.StoredBalance(CustomerWallet)));
        Assert.Equal(Zar(0), books.TrialBalance(Currency.ZAR));
    }

    [Fact]
    public void After_a_win_the_debit_side_accounts_total_what_the_credit_side_accounts_total()
    {
        var books = new Books();
        books.Post(Deposit(500_00));
        books.Post(PlaceBet(100_00));
        books.Post(BetWins(stake: 100_00, payout: 250_00));

        Account[] all = [Bank, OpenBets, BettingRevenue, Payouts, CustomerWallet];

        // Bank R500 + Payouts R150 = R650 on the debit side; Wallet R650 + Open Bets R0 + Revenue R0 on the credit side.
        var debitSide = TotalOnNormalSide(books, all.Where(account => account.NormalSide == NormalSide.Debit));
        var creditSide = TotalOnNormalSide(books, all.Where(account => account.NormalSide == NormalSide.Credit));

        Assert.Equal(Zar(650_00), debitSide);
        Assert.Equal(debitSide, creditSide);
    }

    private static Money TotalOnNormalSide(Books books, IEnumerable<Account> accounts) =>
        accounts.Aggregate(
            Money.Zero(Currency.ZAR),
            (total, account) => total + account.BalanceOnNormalSide(books.StoredBalance(account)));
}
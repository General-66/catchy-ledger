-- 0007: the operator's own "house" accounts from the plan's betting example, shared by every customer.
-- Every environment needs these four, so they're seeded here. Customer wallets are opened through the API.
-- They get fixed Ids 1 to 4, the same as Phase 1's BettingChart, so requests and docs can name them.
-- SQL Server then carries on numbering new accounts from 5.

SET IDENTITY_INSERT dbo.Accounts ON;

INSERT INTO dbo.Accounts (Id, Code, Name, Type, Currency, AllowOverdraft)
VALUES
    (1, '1000', N'Bank', 'Asset', 'ZAR', 1),
    (2, '2100', N'Open Bets', 'Liability', 'ZAR', 1),
    (3, '4000', N'Betting Revenue', 'Income', 'ZAR', 1),
    (4, '5000', N'Payouts', 'Expense', 'ZAR', 1);

SET IDENTITY_INSERT dbo.Accounts OFF;
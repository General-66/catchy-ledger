-- 0008: stored balances, and one way to open an account that always gives it one.
--
-- AccountBalances holds each account's balance: the sum of its postings, signs included. From 0009 on it's updated
-- in the same database transaction as every posting, so it can't drift from them. The postings stay the source of
-- truth. The balance row is what a posting locks: while one transaction holds a wallet's row, no other can spend
-- from that wallet.
--
-- It's an ordinary table, not a ledger table. It changes on every posting, and a ledger table would keep a history
-- row for each of those changes, forever. The postings already are that history.

CREATE TABLE dbo.AccountBalances
(
    AccountId    bigint     NOT NULL,
    BalanceMinor bigint     NOT NULL, -- the sum of the account's postings: debits add, credits take away
    RowVersion   rowversion NOT NULL, -- SQL Server changes it on every update to the row; optimistic locking compares it

    CONSTRAINT PK_AccountBalances PRIMARY KEY CLUSTERED (AccountId),
    CONSTRAINT FK_AccountBalances_Accounts FOREIGN KEY (AccountId) REFERENCES dbo.Accounts (Id)
);
GO

-- Every account that already exists gets its balance, worked out once from its postings: the four seeded house
-- accounts, and any account you opened through the API in Phase 3.
INSERT INTO dbo.AccountBalances (AccountId, BalanceMinor)
SELECT a.Id, COALESCE(SUM(p.AmountMinor), 0)
FROM dbo.Accounts AS a
LEFT JOIN dbo.Postings AS p ON p.AccountId = a.Id
GROUP BY a.Id;
GO

-- Opens an account and its balance row together, so an account can never exist without a balance.
CREATE PROCEDURE dbo.usp_CreateAccount
    @Code           varchar(20),
    @Name           nvarchar(100),
    @Type           varchar(10),
    @Currency       char(3),
    @AllowOverdraft bit,
    @AccountId      bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        INSERT INTO dbo.Accounts (Code, Name, Type, Currency, AllowOverdraft)
        VALUES (@Code, @Name, @Type, @Currency, @AllowOverdraft);

        SET @AccountId = SCOPE_IDENTITY();

        -- Every account starts at zero: money only ever arrives through a posting.
        INSERT INTO dbo.AccountBalances (AccountId, BalanceMinor)
        VALUES (@AccountId, 0);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        THROW; -- a code already in use arrives as error 2627 on UQ_Accounts_Code, as before
    END CATCH;
END;
GO

-- The app reads balances, and changes them only through the procedures.
GRANT SELECT ON dbo.AccountBalances TO ledger_app;
DENY INSERT, UPDATE, DELETE ON dbo.AccountBalances TO ledger_app;

-- From now on accounts are opened only through usp_CreateAccount. 0006 let the app insert into Accounts directly,
-- which would make an account with no balance row. This DENY replaces that GRANT.
GRANT EXECUTE ON dbo.usp_CreateAccount TO ledger_app;
DENY INSERT ON dbo.Accounts TO ledger_app;
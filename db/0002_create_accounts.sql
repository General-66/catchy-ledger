-- 0002: the chart of accounts.
-- An ordinary table, not a ledger table: account details such as the name may change,
-- and Phase 5 turns it into a temporal table that keeps every old version.

CREATE TABLE dbo.Accounts
(
    Id             bigint        IDENTITY(1, 1) NOT NULL,
    Code           varchar(20)   NOT NULL,
    Name           nvarchar(100) NOT NULL,
    Type           varchar(10)   NOT NULL,
    -- Binary collation makes the comparison case-sensitive, so 'zar' can't pass for 'ZAR'.
    Currency       char(3)       COLLATE Latin1_General_BIN2 NOT NULL,
    AllowOverdraft bit           NOT NULL,
    CreatedAt      datetime2(7)  NOT NULL CONSTRAINT DF_Accounts_CreatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_Accounts PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UQ_Accounts_Code UNIQUE (Code),
    -- Lets Postings point at (account, currency) together, so a posting's currency must be its account's.
    CONSTRAINT UQ_Accounts_Id_Currency UNIQUE (Id, Currency),
    CONSTRAINT CK_Accounts_Type CHECK (Type IN ('Asset', 'Liability', 'Equity', 'Income', 'Expense')),
    CONSTRAINT CK_Accounts_Currency CHECK (Currency LIKE '[A-Z][A-Z][A-Z]')
);
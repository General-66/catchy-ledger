-- 0003: the books themselves. Both tables are append-only ledger tables:
-- SQL Server refuses every UPDATE and DELETE on them, even from an administrator, and keeps a
-- cryptographic hash of every row it inserts so tampering can be detected later.
-- A table can only become a ledger table in its first CREATE TABLE, and any column added later must be
-- nullable, so every column the plan needs is here from the start.

CREATE TABLE dbo.Transactions
(
    Id                    bigint        IDENTITY(1, 1) NOT NULL,
    CreatedBy             nvarchar(100) NOT NULL, -- the caller's name, from their API key (Phase 3)
    IdempotencyKey        nvarchar(100) NOT NULL, -- the caller's Idempotency-Key header (Phase 3)
    RequestHash           binary(32)    NOT NULL, -- SHA-256 of the request body: tells a retry from a new request
    Description           nvarchar(200) NOT NULL,
    EffectiveDate         date          NOT NULL, -- the day it counts for in the books
    CreatedAt             datetime2(7)  NOT NULL CONSTRAINT DF_Transactions_CreatedAt DEFAULT SYSUTCDATETIME(),
    ReversesTransactionId bigint        NULL,     -- set only on a reversal: the transaction it cancels

    CONSTRAINT PK_Transactions PRIMARY KEY CLUSTERED (Id),
    -- Each caller's keys are their own, and each key can post only once.
    CONSTRAINT UQ_Transactions_CreatedBy_IdempotencyKey UNIQUE (CreatedBy, IdempotencyKey),
    CONSTRAINT FK_Transactions_ReversesTransaction FOREIGN KEY (ReversesTransactionId) REFERENCES dbo.Transactions (Id)
)
WITH (LEDGER = ON (APPEND_ONLY = ON));

-- A transaction can be reversed only once. The index covers only rows that are reversals,
-- so the many NULLs in ordinary transactions don't count as duplicates.
CREATE UNIQUE NONCLUSTERED INDEX UX_Transactions_ReversesTransactionId
    ON dbo.Transactions (ReversesTransactionId)
    WHERE ReversesTransactionId IS NOT NULL;

CREATE TABLE dbo.Postings
(
    Id            bigint  IDENTITY(1, 1) NOT NULL,
    TransactionId bigint  NOT NULL,
    AccountId     bigint  NOT NULL,
    AmountMinor   bigint  NOT NULL, -- signed minor units: + is a debit, - is a credit
    Currency      char(3) COLLATE Latin1_General_BIN2 NOT NULL,

    CONSTRAINT PK_Postings PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Postings_Transactions FOREIGN KEY (TransactionId) REFERENCES dbo.Transactions (Id),
    -- Points at the account and its currency together: no dollars in a rand wallet, even by direct INSERT.
    CONSTRAINT FK_Postings_Accounts FOREIGN KEY (AccountId, Currency) REFERENCES dbo.Accounts (Id, Currency),
    CONSTRAINT CK_Postings_AmountMinor_NotZero CHECK (AmountMinor <> 0)
)
WITH (LEDGER = ON (APPEND_ONLY = ON));

-- Finds a transaction's lines, and an account's postings for balances and statements.
CREATE NONCLUSTERED INDEX IX_Postings_TransactionId ON dbo.Postings (TransactionId);
CREATE NONCLUSTERED INDEX IX_Postings_AccountId ON dbo.Postings (AccountId, TransactionId) INCLUDE (AmountMinor);
# 2. Store money as integer minor units

Date: 2026-11-19

## Status

Accepted

## Context

Every amount in the ledger must add up exactly. A transaction's postings must sum to zero, and the trial balance must be zero to the cent.

Floating-point types (`float`, `double`) store numbers in binary and can't hold 0.10 exactly, so `0.1 + 0.2` is `0.30000000000000004`. C#'s `decimal` is exact for amounts like 0.10, but it's a 128-bit type with its own rounding rules, and SQL Server's `decimal(p, s)` needs a precision and scale chosen for every column.

## Decision

Store every amount as a signed 64-bit whole number of the currency's minor unit (`long` in C#, `bigint` in SQL Server), next to its ISO 4217 currency code. R500.00 is stored as 50000 with currency ZAR.

A `Money` value object keeps the two together. Its arithmetic is `checked`, and combining two currencies throws. `decimal` is used only to display an amount.

## Consequences

- Sums are exact, and checking a balance is an integer comparison with zero.
- The same number travels through C#, SQL Server and JSON without rounding.
- The largest amount is about 92 quadrillion rand (9.2 x 10^18 cents). Overflow throws instead of wrapping round.
- Displaying an amount needs the currency's number of decimal places: 2 for ZAR, 0 for JPY.
- Fractions of a cent, such as interest or exchange rates, can't be stored directly. If they're ever needed, they get their own type and an explicit rounding rule, recorded in a new ADR.
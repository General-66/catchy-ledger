# 1. Record architecture decisions

Date: 2026-10-26

## Status

Accepted

## Context

This project makes design choices that aren't obvious from the code alone: storing money as cents, how concurrent postings are locked, whether balances are stored or computed. Six months from now, the code will show *what* was chosen but not *why*, or what was rejected.

## Decision

Record each significant decision as a short, numbered Markdown file in `docs/adr/`, using this template:

- **Status:** Proposed, Accepted, or Superseded by ADR-N
- **Context:** the problem and the forces at play
- **Decision:** what was chosen
- **Consequences:** what gets easier, what gets harder

An accepted ADR is never edited to change its decision. A new ADR supersedes it, so the history of thinking stays intact.

## Consequences

- Each decision costs a few minutes to write down.
- Reviewers and interviewers can follow the reasoning without reading every commit.
- Later phases add ADRs for: cents vs decimal; serializable vs row locks vs optimistic concurrency; stored vs computed balances; SQL Server ledger tables.
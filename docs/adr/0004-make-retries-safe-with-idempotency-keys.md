# 4. Make retries safe with an Idempotency-Key stored on the transaction

Date: 2026-11-23

## Status

Accepted

## Context

A client that times out can't tell whether its request was posted. If it retries, a naive API posts the money
twice. If it doesn't, the money may never move. Payment APIs solve this with an Idempotency-Key header: the client
sends a new random key per operation and the same key on every retry, and the server posts each key at most once.

The IETF's draft standard for the header says a missing key is a 400, and the same key with a different request
body is a 422. To tell those apart, the server keeps a fingerprint of the request that first used each key.

The options considered for where the server remembers keys:

1. **A separate IdempotencyKeys table** holding each key, its fingerprint and the full HTTP response. It's general,
   but the response has to be written in the same database transaction as the postings, or a crash between the two
   loses the record. It also stores a second copy of every transaction as JSON.
2. **An in-memory or Redis cache** of recent keys. It's fast, but it forgets keys on a restart or eviction, and a
   retry after that posts twice. For money, "usually" isn't good enough.
3. **Columns on the Transactions row itself:** CreatedBy, IdempotencyKey and RequestHash, with a unique constraint
   on (CreatedBy, IdempotencyKey). Phase 2 already created them.

## Decision

Store the key and a SHA-256 fingerprint of the request on the Transactions row, in the same INSERT as the postings
(option 3). The fingerprint covers the endpoint and a canonical JSON form of the body, so spacing and property order
don't matter, and a key used to post can't be reused to reverse.

For each write, the API looks the key up first. If it's there with the same fingerprint, it returns the original
transaction with 201 and Idempotent-Replayed: true; with a different fingerprint, it returns 422. If two copies
arrive at the same moment, both miss the lookup; the unique constraint lets one in, and the other fails with error
2627 and is answered as a retry. Keys belong to their caller, and only successful postings store a key.

## Consequences

- A retry can never post twice, even across restarts and even when the copies race: the database guarantees it.
- The original response is rebuilt from the stored transaction instead of being stored, so it can't drift from the
  books.
- A request that's rejected, for example as unbalanced, stores nothing, so the caller can fix it and resend it with
  the same key.
- Only writes that create a transaction can be idempotent this way. Opening an account relies on its unique code
  instead: a retry gets a 409, never a second account.
- Keys are kept forever, like the transactions they belong to. A key can't be reused months later for a new
  operation; clients should use a new random value, such as a GUID, every time.
- SQL Server compares keys without regard to case, so "Key-1" and "key-1" are the same key.
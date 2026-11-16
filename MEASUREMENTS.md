# MEASUREMENTS — experiment log

Every experiment: first provoke and measure the problem, then fix it and measure again.

---

## Phase 1: Race condition and overselling

**Load** (`load-tests/purchase.js`): N users each try to buy one ticket, all at the same moment
(k6 `per-vu-iterations`, 1 iteration per VU). Event has 100 tickets.
API: 0.5 CPU / 256 MB, Postgres: 1 CPU / 512 MB, Npgsql pool: default (max 100).

### Naive purchase

**Problem.** The purchase follows a *read → check in the application → write* pattern. Everything runs inside
a transaction, but under `READ COMMITTED` a transaction does not stop multiple requests from reading the same
counter at the same time.

Example with one ticket left:

|Time|Request A|Request B|
|-|-|-|
|t1|`SELECT` → `available = 1`||
|t2||`SELECT` → `available = 1`|
|t3|check `1 > 0` ✔|check `1 > 0` ✔|
|t4|`UPDATE SET available = 0`, `INSERT` order, `COMMIT`||
|t5||`UPDATE` waits for the row lock, then after A commits writes `available = 0`, `INSERT` order, `COMMIT`|

Result: **2 orders for 1 ticket**. The `UPDATE` writes a value computed in the application (`@newAvailable`),
so B overwrites A's write (**lost update**). The counter looks correct; the overselling only shows up when the
rows in `orders` are counted (`GET /events/{id}` → `soldTickets > totalTickets`).

**Code** (`Repository/EventRepository.cs`, `PurchaseNaiveAsync`):

```csharp
// NAIVE on purpose (Phase 1 starting point): read -> check in app -> write the computed value.
// The transaction does NOT protect us: under READ COMMITTED two requests can both read
// available = 1, both pass the check, and both write 0 (lost update) -> two orders for one ticket.
private async Task<PurchaseResult> PurchaseNaiveAsync(long eventId, string userId, CancellationToken cancellationToken)
{
    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

    // 1. Read
    var available = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
        "SELECT available_tickets FROM events WHERE id = @eventId",
        new { eventId }, transaction, cancellationToken: cancellationToken));

    // 2. Check in application code, on a value that may already be stale.
    //    Returning early disposes the transaction, which rolls it back.
    if (available is null)
        return PurchaseResult.EventNotFound;

    if (available <= 0)
        return PurchaseResult.SoldOut;

    // 3. Write the value computed in the app (not "available_tickets - 1" in SQL).
    await connection.ExecuteAsync(new CommandDefinition(
        "UPDATE events SET available_tickets = @newAvailable WHERE id = @eventId",
        new { eventId, newAvailable = available.Value - 1 }, transaction, cancellationToken: cancellationToken));

    var orderId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
        "INSERT INTO orders (event_id, user_id) VALUES (@eventId, @userId) RETURNING id",
        new { eventId, userId }, transaction, cancellationToken: cancellationToken));

    await transaction.CommitAsync(cancellationToken);
    return PurchaseResult.Success(orderId);
}
```

**Result:**

|Users|Sold (orders)|`available_tickets` after|Oversold|p95 latency|Throughput|Errors|
|-|-|-|-|-|-|-|
|500|**500**|95|**400**|2.80 s|~166 req/s|0%|
|50|50|**99**|0|—|—|0%|

* With 500 users, **every request "bought" a ticket**: 500 orders for 100 tickets, no 409 (sold out) at all.
* The counter dropped only 5 times instead of 500 (lost update), so `available_tickets` claims 95 tickets are still free.
* With 50 users nothing was oversold yet, but the counter is still wrong (99 instead of 50): the data is silently
  corrupted and the next buyers would be oversold.
* No errors, no 5xx: the system looks healthy while selling tickets it does not have.

**Bottleneck / why:**

* Plain `SELECT` does not wait for locks (MVCC), so almost all requests read the same value and pass the check.
* The `UPDATE` does lock the row, but only *after* the check, and it writes a stale value computed in the app.
* All writes go to **one hot row**; each transaction holds its row lock until `COMMIT`, so the writes run one
  after another. That, plus requests waiting for a free pool connection, explains the ~3 s latency.
  The naive version is both **wrong and slow**.

---

### Pessimistic locking: `SELECT ... FOR UPDATE`

**Change.** The same read → check → write, but the read locks the row until the transaction ends:

```sql
SELECT available_tickets FROM events WHERE id = @eventId FOR UPDATE
```

A second request now waits on its `SELECT` until the first one commits, and then reads the fresh value.
Read, check and write behave as one indivisible step.
(Code: `PurchaseForUpdateAsync` in `Repository/EventRepository.cs`; selected with `PURCHASE_STRATEGY=ForUpdate`.)

**Result** (500 users):

|Strategy|Sold|`available_tickets` after|200 / 409|p95 latency|Throughput|Errors|
|-|-|-|-|-|-|-|
|Naive|500 ❌|95|500 / 0|2.80 s|~166 req/s|0%|
|**ForUpdate**|**100** ✅|**0**|**100 / 400**|**0.83 s**|**~482 req/s**|0%|

* Correct: exactly 100 tickets sold, the counter matches the orders, the other 400 users got "sold out".
* And ~3× faster than the naive version. Not because locking is cheap, but because the naive version did
  **5× more expensive work**: it accepted all 500 purchases, so it ran 500 commits with writes (WAL flush),
  one after another on the locked row. With `FOR UPDATE` only 100 transactions write; the other 400 read `0`,
  roll back and release the lock almost immediately.

**Trade-off:**

* ✅ Correct and simple: one extra keyword, the logic stays in readable application code.
* ❌ Every request for this event waits in **one queue on one row**, including the ones that only find out it is
  sold out. Throughput is capped by how fast one row can be locked and released.
* ❌ A waiting request holds a pool connection while it waits for the lock, so under higher load the pool
  runs out before the database CPU does.
* ❌ Lock order matters as soon as a transaction locks more than one row (risk of deadlocks).

---

### Atomic conditional `UPDATE`

**Change.** No separate read: the check and the write are one statement, and the database computes the new value.

```sql
UPDATE events
SET available_tickets = available_tickets - 1
WHERE id = @eventId AND available_tickets > 0
```

1 updated row = ticket bought, 0 rows = sold out. If two requests race for the last ticket, the second one waits
for the row lock and Postgres re-checks the `WHERE` on the fresh value, so it updates 0 rows.
(Code: `PurchaseAtomicAsync`; `PURCHASE_STRATEGY=Atomic`.)

**Result** (500 users):

|Strategy|Sold|200 / 409|p95 latency|Throughput|
|-|-|-|-|-|
|ForUpdate|100 ✅|100 / 400|0.83 s|~482 req/s|
|**Atomic**|**100** ✅|**100 / 400**|**0.77 s**|**~535 req/s**|

Correct, and slightly faster than `FOR UPDATE`: one query less per purchase and the row is locked for a shorter
time. The difference is small, because both still serialize on the same hot row. Note: this 1-second burst test
varies a lot between runs (Atomic p95 ranged 0.77–1.33 s), so it proves correctness reliably, but not small
speed differences.

**Trade-off:**

* ✅ Simplest correct solution, no explicit lock, fewest round trips.
* ❌ The business rule lives in SQL (`WHERE`), which is not always possible: not every business rule fits into
  a single query.
* ❌ The `UPDATE` still locks the row until `COMMIT` (or `ROLLBACK`), just for a slightly shorter time, since there
  is no separate `SELECT` and C# check before it.

---

### Optimistic locking: `version` column

**Change.** No lock on read. Read the value together with its `version`, check in C#, and write only if nobody
changed the row in the meantime:

```sql
UPDATE events
SET available_tickets = @newAvailable, version = version + 1
WHERE id = @eventId AND version = @version
```

0 updated rows = someone else won the race → conflict (409 "Concurrent update, try again").
Two variants: without retry, and with up to 5 retries, each after a random 0–20 ms delay (jitter), so the losers
don't all collide again at the same moment.
(Code: `PurchaseOptimisticAsync`, `PurchaseOptimisticRetryAsync`; `PURCHASE_STRATEGY=Optimistic` / `OptimisticRetry`.)

**Result** (500 users):

|Strategy|Sold|`available_tickets` after|200 / sold out / conflict|p95 latency|Throughput|
|-|-|-|-|-|-|
|Atomic|100 ✅|0|100 / 400 / —|0.77 s|~535 req/s|
|Optimistic (no retry)|**23** ❌|**77**|23 / 0 / **477**|0.50 s|~752 req/s|
|Optimistic + retry|100 ✅|0|100 / 398 / 2|1.29 s|~348 req/s|

* **Without retry: underselling.** Never oversells, but sold only 23 of 100 tickets: 477 buyers were turned away
  while 77 tickets were still free. Every wave of concurrent reads sees the same `version`, and only one request per
  wave wins. It looks like the fastest variant only because 95% of the requests did no useful work.
* **With retry: correct business result** (all 100 sold, only 2 buyers gave up after 5 attempts), but the slowest
  of the correct variants: the losers repeat the whole read + write several times.

**Trade-off:**

* ✅ No lock while reading; great when conflicts are **rare** (e.g. two users rarely edit the same record).
* ❌ On a hot row conflicts are the normal case: without retry it undersells, with retry it pays with extra queries
  and higher latency.
* ❌ More code: `version` column, retry loop, limit on attempts, jitter.
* ➖ In Postgres the `UPDATE` still waits for the row lock of a concurrent `UPDATE`; "optimistic" only means the
  read does not lock.

---

### Throughput comparison: steady load

The burst test above proves correctness, but it lasts ~1 s and its latency varies a lot between runs.
To compare speed, all variants ran under the same **steady load**: k6 `constant-arrival-rate`, **200 purchases/s
for 30 s** (`load-tests/throughput.js`), on an event with 100,000 tickets, so every request is a real purchase.
New requests keep arriving at 200/s no matter how fast the server answers (open model).

|Strategy|Successful purchases/s|p95 latency|Rejected (conflict)|Not sent by k6 (`dropped_iterations`)|Lost updates|
|-|-|-|-|-|-|
|Naive|~142|7.33 s|—|730|**4,912** ❌|
|ForUpdate|~145|6.50 s|—|811|0|
|**Atomic**|**~159**|4.60 s|—|565|0|
|Optimistic|~147|**69 ms**|26%|0|0|
|OptimisticRetry|~74|4.01 s|59%|399|0|

* **`dropped_iterations` = the system is overloaded.** The server could not keep up with the arrival rate, so
  requests piled up. Each waiting request keeps one k6 VU busy, and we allowed at most 1,000 VUs. At ~5 s latency,
  200 req/s needs 200 × 5 = **1,000 requests in flight**, which is exactly that limit: from then on k6 had no free VU
  and skipped scheduled purchases. In real life these are buyers who never got to try, because the site hung.
* **The limit is the hot row, ~150 writes/s.** Every purchase updates the same `events` row, and Postgres lets only
  one transaction at a time change a row: the next one waits for the previous `COMMIT`. One purchase
  (`UPDATE` + `INSERT` + `COMMIT` with a disk flush) takes ~6–7 ms, so one row handles about **150 purchases/s**.
  Every variant hit roughly that number, no matter how it locks. It is not the API (0.5 CPU): Optimistic pushed all
  200 req/s through the same API with 69 ms p95.
* **The difference is what happens to the excess** (200 − ~150 req/s). Locking variants **queue** it: the queue grows
  for the whole test and p95 climbs to 5–7 s. Optimistic **rejects** it immediately: p95 69 ms, but 26% of buyers
  were turned away while tickets were still available.
* **Naive lost 93% of its writes**: 5,271 orders, but the counter dropped only 359 times. No errors anywhere.
* **Retry under overload makes things worse**: each rejected request came back up to 5 times, multiplying the load
  on the hot row. OptimisticRetry sold **half** as much as Optimistic without retry, with 60× higher latency
  (a small retry storm).
* **Atomic is the best fit** here: correct, simplest and the highest throughput, because it holds the row lock for
  the shortest time.

**Going beyond ~150 purchases/s needs a different data model, not a different lock.** All variants only change *how*
requests wait for the single row; none removes the fact that everyone writes to it. Options that remove (or relieve)
that single contention point:

* **One row per ticket:** a `tickets` table with 100,000 rows; a buyer takes the first free one with
  `FOR UPDATE SKIP LOCKED`. Buyers lock *different* rows and run in parallel; locked rows are skipped, not waited for.
* **Sharded counter:** split the stock into e.g. 10 counter rows of 10,000; each buyer picks one at random.
  10 hot rows instead of 1 → roughly 10× the capacity.
* **Counter in Redis** (Phase 3): an in-memory atomic `DECR`, the database is updated afterwards.
  Tens of thousands of operations per second, no disk flush per purchase.
* **Queue** (Phase 4): accept the request immediately, a worker processes purchases in batches,
  e.g. one `UPDATE` and one `COMMIT` for 100 purchases instead of 100 commits.

---

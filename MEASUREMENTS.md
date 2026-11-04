# MEASUREMENTS — experiment log

Every experiment: first provoke and measure the problem, then fix it and measure again.

---

### [Phase 1] Naive purchase — race condition and overselling

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

**Code** (`Repository/EventRepository.cs`, `PurchaseAsync`):

```csharp
// NAIVE on purpose (Phase 1 starting point): read -> check in app -> write the computed value.
// The transaction does NOT protect us: under READ COMMITTED two requests can both read
// available = 1, both pass the check, and both write 0 (lost update) -> two orders for one ticket.
public async Task<PurchaseResult> PurchaseAsync(long eventId, string userId, CancellationToken cancellationToken)
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

**Load** (`load-tests/purchase.js`): N users each try to buy one ticket, all at the same moment
(k6 `per-vu-iterations`, 1 iteration per VU). Event has 100 tickets.
API: 0.5 CPU / 256 MB, Postgres: 1 CPU / 512 MB, Npgsql pool: default (max 100).

**Result:**

|Users|Sold (orders)|`available_tickets` after|Oversold|p95 latency|Throughput|Errors|
|-|-|-|-|-|-|-|
|500 (cold start)|**500**|93|**400**|3.41 s|~125 req/s|0%|
|500 (warm)|**500**|95|**400**|2.80 s|~166 req/s|0%|
|50|50|**99**|0|—|—|0%|

"Cold start" = first run after the API container starts (JIT compilation, empty connection pool).
Comparisons below use warm runs.

* With 500 users, **every request "bought" a ticket**: 500 orders for 100 tickets, no 409 (sold out) at all.
* The counter dropped only 7 times instead of 500 (lost update), so `available_tickets` claims 93 tickets are still free.
* With 50 users nothing was oversold yet, but the counter is still wrong (99 instead of 50): the data is silently
corrupted and the next buyers would be oversold.
* No errors, no 5xx: the system looks healthy while selling tickets it does not have.

**Bottleneck / why:**

* Plain `SELECT` does not wait for locks (MVCC), so almost all requests read the same value and pass the check.
* The `UPDATE` does lock the row, but only *after* the check, and it writes a stale value computed in the app.
* All writes go to **one hot row**; each transaction holds its row lock until `COMMIT`, so the writes run one
after another. That, plus requests waiting for a free pool connection, explains the ~3 s latency.
The naive version is both **wrong and slow**.

**Next:** `SELECT ... FOR UPDATE` (lock the row when reading, not after the check).

---

### [Phase 1] Pessimistic locking — `SELECT ... FOR UPDATE`

**Change.** The same read → check → write, but the read locks the row until the transaction ends:

```sql
SELECT available_tickets FROM events WHERE id = @eventId FOR UPDATE
```

A second request now waits on its `SELECT` until the first one commits, and then reads the fresh value.
Read, check and write behave as one indivisible step.
(Code: `PurchaseForUpdateAsync` in `Repository/EventRepository.cs`; selected with `PURCHASE_STRATEGY=ForUpdate`.)

**Load:** same as above, 500 users, warm run.

**After the change:**

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

**Next:** atomic `UPDATE ... WHERE available_tickets > 0` (check and write in one statement, no separate read).


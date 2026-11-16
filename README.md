# Ticketing System — learning how systems scale

A deliberately simple ticket-selling backend used as a lab for system design: a **flash sale**, where thousands of
users try to buy 100 tickets in the same second. Every problem is first **provoked and measured** under load in
resource-limited containers, and only then fixed and measured again.

The goal is not a finished app, but understanding what breaks under load and why.
All experiments and numbers are in [MEASUREMENTS.md](MEASUREMENTS.md).

**Stack:** .NET 8 Web API · PostgreSQL 16 (Npgsql + Dapper, plain SQL, no ORM) · Docker Compose · k6 · Prometheus

## Running

Requires Docker. The .NET 8 SDK is only needed to run the API outside Docker.

```powershell
docker compose up -d --build    # db (port 5432), api (http://localhost:8080), prometheus (http://localhost:9090)
docker compose down -v          # stop and wipe the database (init.sql recreates it on the next "up")
```

**Database port.** The host port comes from `DB_HOST_PORT` (default `5432`). If 5432 is already taken by a local
Postgres, create a `.env` file next to `docker-compose.yml`:

```
DB_HOST_PORT=5433
```

`src/Ticketing.Api/appsettings.Development.json` (used when running the API from the IDE) points to port `5433`;
change it if you use a different port. Inside Docker the API always connects to `db:5432`.

**Endpoints:** `GET /events/{id}`, `POST /events/{id}/purchase` (body `{"userId":"u1"}`),
`POST /events/{id}/reset`, `GET /health`, `GET /metrics`.
Event `1` has 100 tickets (flash sale), event `2` has 100,000 (throughput test).

## Measuring

The purchase strategy is chosen with `PURCHASE_STRATEGY` (no rebuild needed):
`Naive`, `ForUpdate`, `Atomic`, `Optimistic`, `OptimisticRetry`.

```powershell
$env:PURCHASE_STRATEGY="Atomic"; docker compose up -d

# Burst: N users each buy one ticket at the same moment (correctness: is anything oversold?)
docker compose run --rm k6 run -e USERS=500 /scripts/purchase.js

# Steady load: constant purchases/s (speed: throughput and latency)
docker compose run --rm k6 run -e RATE=200 -e DURATION=30s /scripts/throughput.js
```

Use PowerShell or cmd. Git Bash rewrites `/scripts/...` into a Windows path.
Each test resets the event first and prints a `RESULT ...` line at the end.

## Results so far

API 0.5 CPU / 256 MB, Postgres 1 CPU / 512 MB.

**Phase 1: race condition.** 500 users at once, 100 tickets:

|Strategy|Sold|Note|
|-|-|-|
|`Naive` (read → check → write)|500 ❌|lost update, overselling|
|`ForUpdate` (`SELECT ... FOR UPDATE`)|100 ✔||
|`Atomic` (`UPDATE ... WHERE available_tickets > 0`)|100 ✔|simplest, fastest|
|`Optimistic` (`version` column)|23 ❌|477 conflicts, underselling|
|`OptimisticRetry` (up to 5 attempts)|100 ✔||

Under steady load (200 purchases/s) every strategy tops out at **~150 purchases/s**. The limit is the single hot
`events` row, not the API or the locking method. Details in [MEASUREMENTS.md](MEASUREMENTS.md).

## Roadmap

- [x] Phase 0: baseline (schema, API, Docker, k6, Prometheus)
- [x] Phase 1: race condition and overselling
- [ ] Phase 2: database connections, pool size, PgBouncer (+ Grafana)
- [ ] Phase 3: Redis (inventory counter, caching, cache stampede)
- [ ] Phase 4: queue (RabbitMQ), duplicate processing, idempotency
- [ ] Phase 5: multiple instances behind nginx, rate limiting, timeouts, retries, circuit breaker

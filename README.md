# Ticketing System — scaling systems

A deliberately simple ticket-selling backend used as a lab for system design: a **flash sale**, where thousands of
users try to buy 100 tickets in the same second. Every problem is first **provoked and measured** under load in
resource-limited containers, and only then fixed and measured again.

All experiments and numbers are in [MEASUREMENTS.md](MEASUREMENTS.md).

**Stack:** .NET 8 Web API · PostgreSQL 16 (Npgsql + Dapper, plain SQL, no ORM) · Docker Compose · k6 · Prometheus · Grafana · PgBouncer

## Running & measuring

Requires Docker. Run the commands in PowerShell (Git Bash rewrites `/scripts/...` into a Windows path).

```powershell
docker compose up -d --build    # start everything (first time, or after a code change)
docker compose down -v          # stop and wipe the database; init.sql recreates it on the next "up"
```

Grafana dashboard: http://localhost:3000/d/ticketing (login `admin` / `admin`).
If port 5432 is already taken by a local Postgres, put `DB_HOST_PORT=5433` in a `.env` file next to
`docker-compose.yml`.

**Settings.** Three environment variables configure the API. Set them, then run `docker compose up -d` to restart the
API with the new value (no rebuild). They stay set until you close that PowerShell window.

|Variable|Default|Values|Matters for|
|-|-|-|-|
|`PURCHASE_STRATEGY`|`Naive`|`Naive`, `ForUpdate`, `Atomic`, `Optimistic`, `OptimisticRetry`|purchase tests (Phase 1)|
|`DB_POOL_SIZE`|`100`|any number|read test (Phase 2)|
|`DB_HOST`|`db`|`db` (directly to Postgres), `pgbouncer` (through PgBouncer)|read test (Phase 2)|

**Two API instances:** add `--scale api=2` to `docker compose up` (ports 8080 and 8081). Every `up` without it
goes back to one instance, and so does `docker compose run k6` unless it has `--no-deps`.

**Load tests** (k6, runs in Docker):

|Script|Load|Measures|Options (default)|
|-|-|-|-|
|`purchase.js`|**burst**: N users each buy one ticket at the same moment|correctness: is anything oversold?|`USERS` (500)|
|`throughput.js`|**steady**: constant purchases per second|speed: throughput and latency of purchases|`RATE` (200), `DURATION` (30s)|
|`read.js`|**steady**: constant `GET /events/1` per second|connections and pool size|`RATE` (200), `DURATION` (30s)|

Examples:

```powershell
# Phase 1: purchase strategy
$env:PURCHASE_STRATEGY="Atomic"; docker compose up -d
docker compose run --rm k6 run -e USERS=500 /scripts/purchase.js
docker compose run --rm k6 run -e RATE=200 /scripts/throughput.js

# Phase 2: pool size
$env:DB_POOL_SIZE="50"; docker compose up -d
docker compose run --rm k6 run -e RATE=1000 /scripts/read.js

# Phase 2: two API instances through PgBouncer
$env:DB_POOL_SIZE="90"; $env:DB_HOST="pgbouncer"; docker compose up -d --scale api=2
docker compose run --rm --no-deps k6 run -e RATE=1000 /scripts/read.js
```

The first run after the API restarts is slow (warm-up), so run each test twice and keep the second result.
Purchase tests reset the event first and print a `RESULT ...` line at the end.

## Results

Details in [MEASUREMENTS.md](MEASUREMENTS.md).

## Roadmap

- [x] Phase 0: baseline (schema, API, Docker, k6, Prometheus)
- [x] Phase 1: race condition and overselling
- [x] Phase 2: database connections, pool size, PgBouncer
- [ ] Phase 3: Redis (inventory counter, caching, cache stampede)
- [ ] Phase 4: queue (RabbitMQ), duplicate processing, idempotency
- [ ] Phase 5: multiple instances behind nginx, rate limiting, timeouts, retries, circuit breaker

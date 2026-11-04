-- Runs once, when the Postgres container starts with an empty data volume.

CREATE TABLE events (
    id                BIGSERIAL PRIMARY KEY,
    name              TEXT NOT NULL,
    total_tickets     INT  NOT NULL CHECK (total_tickets >= 0),
    available_tickets INT  NOT NULL
);

CREATE TABLE orders (
    id         BIGSERIAL   PRIMARY KEY,
    event_id   BIGINT      NOT NULL REFERENCES events (id),
    user_id    TEXT        NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX ix_orders_event_id ON orders (event_id);

-- Test event: 100 tickets for the flash sale.
INSERT INTO events (name, total_tickets, available_tickets)
VALUES ('Flash Sale Concert', 100, 100);

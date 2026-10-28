-- Synthetic fixture: no real users. Fixed UTC dates make tests reproducible.
CREATE TABLE users (
  id integer PRIMARY KEY,
  device text NOT NULL CHECK (device IN ('desktop', 'mobile')),
  source text NOT NULL CHECK (source IN ('organic', 'paid'))
);
CREATE TABLE events (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id integer NOT NULL REFERENCES users(id),
  event_name text NOT NULL CHECK (event_name IN ('visit', 'checkout')),
  occurred_at timestamptz NOT NULL,
  UNIQUE (user_id, event_name)
);
CREATE TABLE orders (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id integer NOT NULL REFERENCES users(id),
  status text NOT NULL CHECK (status IN ('paid', 'failed')),
  created_at timestamptz NOT NULL
);
CREATE INDEX events_period ON events(occurred_at, event_name, user_id);
CREATE INDEX orders_user ON orders(user_id, created_at);

-- 14 days, 4 segments, 100 distinct visitors per segment per day.
-- Before: 60% checkout, 40% paid in all segments.
-- After: only mobile/paid drops to 30% checkout, 10% paid.
INSERT INTO users
SELECT d * 400 + s * 100 + n,
       CASE WHEN s >= 2 THEN 'mobile' ELSE 'desktop' END,
       CASE WHEN s % 2 = 1 THEN 'paid' ELSE 'organic' END
FROM generate_series(0,13) d CROSS JOIN generate_series(0,3) s
CROSS JOIN generate_series(1,100) n;

INSERT INTO events(user_id,event_name,occurred_at)
SELECT id, 'visit', timestamptz '2026-10-05 12:00:00+00' + ((id-1)/400) * interval '1 day'
FROM users;
INSERT INTO events(user_id,event_name,occurred_at)
SELECT id, 'checkout', timestamptz '2026-10-05 12:05:00+00' + ((id-1)/400) * interval '1 day'
FROM users
WHERE ((id-1)%100)+1 <= CASE WHEN ((id-1)/400)>=7 AND device='mobile' AND source='paid' THEN 30 ELSE 60 END;
INSERT INTO orders(user_id,status,created_at)
SELECT id, 'paid', timestamptz '2026-10-05 12:10:00+00' + ((id-1)/400) * interval '1 day'
FROM users
WHERE ((id-1)%100)+1 <= CASE WHEN ((id-1)/400)>=7 AND device='mobile' AND source='paid' THEN 10 ELSE 40 END;

-- Funnel uses ordered events in [start,end), deduplicated by user.
CREATE VIEW funnel_users AS
SELECT u.id, u.device, u.source, v.visit_at, c.checkout_at, o.paid_at
FROM users u
JOIN (SELECT user_id, min(occurred_at) AS visit_at FROM events WHERE event_name='visit' GROUP BY user_id) v ON v.user_id=u.id
LEFT JOIN LATERAL (
  SELECT min(occurred_at) AS checkout_at FROM events
  WHERE user_id=u.id AND event_name='checkout' AND occurred_at>=v.visit_at
) c ON true
LEFT JOIN LATERAL (
  SELECT min(created_at) AS paid_at FROM orders
  WHERE user_id=u.id AND status='paid' AND created_at>=c.checkout_at
) o ON true;

CREATE TABLE IF NOT EXISTS daily_usage (
  day TEXT NOT NULL,
  scope TEXT NOT NULL,
  key TEXT NOT NULL,
  count INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, scope, key)
);

CREATE TABLE IF NOT EXISTS requests (
  request_id TEXT PRIMARY KEY,
  device_hash TEXT NOT NULL,
  ip_hash TEXT NOT NULL,
  status TEXT NOT NULL,
  response_json TEXT,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_requests_device_created
  ON requests(device_hash, created_at);

CREATE INDEX IF NOT EXISTS idx_requests_created
  ON requests(created_at);

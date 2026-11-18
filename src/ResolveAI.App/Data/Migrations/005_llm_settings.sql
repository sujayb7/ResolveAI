-- The live model chosen on the Settings page. One row; saved only after a connection test passed.
-- The key is encrypted with ASP.NET Data Protection; key_hint is the masked form the UI shows.
CREATE TABLE llm_settings (
  id                INTEGER PRIMARY KEY CHECK (id = 1),
  provider          TEXT NOT NULL CHECK (provider IN ('github', 'anthropic')),
  model             TEXT NOT NULL,
  base_url          TEXT,
  api_key_protected TEXT NOT NULL,
  key_hint          TEXT NOT NULL,
  key_fingerprint   TEXT NOT NULL,
  tested_at         TEXT NOT NULL
);

-- Tokens used by every live model call, grouped by the key that paid for it. Only a fingerprint of the key is kept.
-- estimated = 1 when the provider reports no usage (GitHub Copilot SDK) and tokens are approximated from text length.
CREATE TABLE llm_usage (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  key_fingerprint TEXT NOT NULL,
  key_hint        TEXT NOT NULL,
  provider        TEXT NOT NULL,
  model           TEXT,
  purpose         TEXT NOT NULL,
  input_tokens    INTEGER NOT NULL,
  output_tokens   INTEGER NOT NULL,
  estimated       INTEGER NOT NULL,
  created_at      TEXT NOT NULL
);
CREATE INDEX llm_usage_by_key ON llm_usage (key_fingerprint, created_at);

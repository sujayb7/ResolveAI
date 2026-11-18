-- Display preferences set on the Settings page, one row per name. They apply to every viewer of this server.
-- show_model_name: '1' shows which model wrote an AI result ("AI-generated (claude-haiku-4.5)"); absent or '0' hides it.
CREATE TABLE app_prefs (
  name  TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

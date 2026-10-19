# Release notes

## v0.1.0 — 2026-10-19

First release. ACopy backs up the databases on a Docker host on a schedule and keeps the last N copies.

### Backups
- Engines: **PostgreSQL, MySQL, MariaDB, MongoDB, Redis / Valkey, SQLite, SQL Server**, and a
  **custom command** for anything else.
- Two execution modes: **docker exec** (the database's own dump tool inside its container, credentials
  from the container's env) and **network** (bundled pg_dump 18, mariadb-dump, mongodump, redis-cli, sqlite3).
- Per-database cron schedule, retention (keep N), compression (gzip / brotli / none), folder and timeout.
- **Timestamped or rolling file names** (`name.0` → `name.1` → … → `name.0`) for Syncthing-style mirrors.
- Writes to a Syncthing-ignored temp file and renames atomically; failed runs never touch existing copies.
- SHA-256 per file, run history with tool output, cancel a running backup, connection test.

### Docker
- **Label discovery**: `acopy.enable=true` plus optional `acopy.*` labels turn a container into a backup
  target; re-synced every minute, orphaned targets keep their history.
- Container picker with engine detection when adding a database by hand.

### UI & ops
- Dashboard, databases, per-database history, activity log, Docker overview with label reference,
  settings; light/dark/system themes in the Copper & Patina palette.
- Single admin password (first-run setup or `ACOPY_Auth__Password`), encrypted target passwords.
- Webhook notifications on failure (optionally on success) — Discord/Slack/Mattermost compatible.
- Single multi-arch Docker image (amd64 + arm64); optional `PUID`/`PGID`; health check at `/healthz`.

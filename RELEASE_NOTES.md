# Release notes

## v0.2.0 — 2026-10-20

### Restore
- **One-click restore** of any successful backup, from the local file or — when it has rotated out locally —
  from a copy on an rclone destination. Network mode uses the bundled clients; exec mode copies the dump
  into the container and runs its own tools (psql, mariadb/mysql, mongorestore, sqlite3, sqlcmd).
- A **safety backup** of the current state is taken first by default; confirm by typing the database name.
- Custom targets get an optional *restore command* (reads the backup on stdin). Redis restores stay manual.
- PostgreSQL dumps now include `--clean --if-exists` so they restore in place.

### Encryption
- **Encryption keys**, chosen per database or with `acopy.encryption=<key>`:
  - **Passphrase** — AES-256 in OpenSSL's format (`.enc`), decryptable with `openssl enc -d`.
  - **age** — public-key encryption (`.age`); generate a pair in ACopy (exportable private key) or store only
    the public key and paste the private key when restoring.

### Off-site copies
- **Destinations**: any rclone backend (forms for S3/MinIO/R2/Wasabi, B2, SFTP, WebDAV/Nextcloud, SMB, FTP,
  local path; raw options for the rest) and **Telegram** (files split into parts above the bot limit;
  self-hosted Bot API servers supported).
- Per-destination retention (mirror the local one, or keep N / N days), upload status per run, retry.

### Retention, labels, Docker
- **Maximum age** in days, alone or combined with keep-N; the newest copy is always kept.
- **Several targets per container**: `acopy.database=a,b` and named jobs `acopy.jobs.<job>.<key>`.
- New labels: `max_age_days`, `encryption`, `destinations`, `restore_command`.
- **Docker over TCP** (`tcp://…`, e.g. a socket proxy) with optional TLS; `DOCKER_HOST` is honoured.

### Notifications
- **Telegram** notifications next to the webhook; upload failures are reported too.

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

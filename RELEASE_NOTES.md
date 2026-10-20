# Release notes

## Unreleased

### Telegram
- **Chats per database:** a database (or the `acopy.telegram_chats` label, also per job) can name its own
  comma-separated Telegram chats, replacing the destination's chat — send each backup to the people
  responsible for it. Destination and notification chat ids also accept several chats.
- Each part is uploaded once and sent to further chats by file id. When some chats refuse a file, the upload
  is marked failed with the reason, the other chats keep their copy, and **Retry** only sends to the missing
  ones. Retention deletes old messages in every chat.
- **Self-hosted Bot API server:** an optional `telegram-bot-api` service in `docker-compose.yml`
  (`--profile telegram`), `ACOPY_Telegram__ApiUrl` as the default server for every Telegram URL left blank,
  and a **Log out from public API** button to move a bot over. Files up to 2 GB per part.

## v0.1.0 — 2026-10-19

First release. ACopy backs up the databases on a Docker host on a schedule, keeps the last N copies (or the
last N days), encrypts them, copies them off-site, and restores them with one click.

### Backups
- Engines: **PostgreSQL, MySQL, MariaDB, MongoDB, Redis / Valkey, SQLite, SQL Server**, and a
  **custom command** for anything else.
- Two execution modes: **docker exec** (the database's own dump tool inside its container, credentials
  from the container's env) and **network** (bundled pg_dump 18, mariadb-dump, mongodump, redis-cli, sqlite3).
- Per-database cron schedule, compression (gzip / brotli / none), folder and timeout.
- **Retention** by count, by maximum age in days, or both; the newest copy is always kept.
- **Timestamped or rolling file names** (`name.0` → `name.1` → … → `name.0`) for Syncthing-style mirrors.
- Writes to a Syncthing-ignored temp file and renames atomically; failed runs never touch existing copies.
- SHA-256 per file, run history with tool output, cancel a running backup, connection test.

### Restore
- **One-click restore** of any successful backup, from the local file or — when it has rotated out locally —
  from a copy on an rclone destination. Network mode uses the bundled clients; exec mode copies the dump
  into the container and runs its own tools (psql, mariadb/mysql, mongorestore, sqlite3, sqlcmd).
- A **safety backup** of the current state is taken first by default; confirm by typing the database name.
- Custom targets get an optional *restore command* (reads the backup on stdin). Redis restores stay manual.

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

### Docker
- **Label discovery**: `acopy.enable=true` plus optional `acopy.*` labels turn a container into a backup
  target; re-synced every minute, orphaned targets keep their history.
- **Several targets per container**: `acopy.database=a,b` and named jobs `acopy.jobs.<job>.<key>`.
- Docker via the unix socket or **over TCP** (`tcp://…`, e.g. a socket proxy) with optional TLS;
  `DOCKER_HOST` is honoured.
- Container picker with engine detection when adding a database by hand.

### Notifications
- Webhook (Discord / Slack / Mattermost compatible) and **Telegram**; failures of backups, restores and
  uploads are always sent, successes optionally.

### UI & ops
- Dashboard, databases, per-database history and restore, activity log, destinations, encryption keys,
  Docker overview with label reference, settings; light/dark/system themes in the Copper & Patina palette.
- Single admin password (first-run setup or `ACOPY_Auth__Password`); stored secrets encrypted at rest.
- Single multi-arch Docker image (amd64 + arm64); optional `PUID`/`PGID`; health check at `/healthz`.

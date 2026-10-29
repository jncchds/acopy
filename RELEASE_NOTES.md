# Release notes

## v0.3.0 — 2026-10-29

### Network mode, filled in from a container
- **Pick a container for a network-mode target** and ACopy fills in the host, port, user and database. It
  compares its own container's Docker networks with the database's and says how it would get there — a
  shared network (by name), the default bridge (by IP, which can change), a published host port, or not at
  all — then checks with a live TCP connect. When it can't get there, it shows the compose snippet (and the
  `docker network connect` command) that puts ACopy on the database's network.
- **Network-mode targets read credentials from the linked container**, like exec mode: a blank user or
  password, or *Password from container variable*, comes from the container's env at run time, so the
  password is never typed in or sent to the browser. Labelled `acopy.mode=network` targets get this too.
- The Docker page's **＋ Back up** opens the form with that container picked.

### Fixes
- **MongoDB backups no longer exhaust memory on small hosts.** `mongodump` now reads one collection at a time
  (`--numParallelCollections=1`) instead of four, which kept `mongod`'s cache growing until the host froze.
  Pass `--numParallelCollections` (or `-j`) in the extra arguments to choose your own.
- **Docker-exec dumps run at low priority too.** Dumps that run inside the database container now start under
  `nice`/`ionice` with the `NICE` setting (when the container has those tools), like ACopy's own work already did.

## v0.2.2 — 2026-10-20

### Fixes
- **MariaDB/MySQL with the event scheduler disabled** (`event_scheduler=DISABLED`) no longer fail with
  *"Couldn't execute 'show events' … (1577)"*: the dump runs again without `--events`, and the run log says so.

## v0.2.1 — 2026-10-20

### Backups no longer stall the host
- Backup files are forced to disk every 32 MB (`ACOPY_Backup__SyncEveryMb`) instead of piling up gigabytes of
  unwritten data in memory, whose flush could freeze every service on the host for a while.
- ACopy and the dumps, compression and uploads it runs now use low CPU and disk priority (`NICE`, default
  `10`; `0` restores normal priority).

## v0.2.0 — 2026-10-20

Telegram grows up: per-database recipients, a self-hosted Bot API server for files up to 2 GB, and backups that
stay in the chat for good.

### Telegram
- **Chats per database.** A database can name its own Telegram chats (*Telegram chats* in the editor, or the
  `acopy.telegram_chats` label — also per job), replacing the destination's chat: each backup goes to the people
  responsible for it. Chat ids are comma-separated lists everywhere — destinations and notifications too.
- **One upload, many chats.** Each part is uploaded once and sent to the other chats by Telegram's file id. If
  some chats refuse a file (e.g. a user blocked the bot), the upload is marked failed with the reason, the other
  chats keep their copy, and **Retry** only sends to the chats still missing it.
- **Dated file names.** Files sent to Telegram always carry the date of the backup — rolling databases too
  (`app.0.sql.gz` arrives as `app_20261020-030000.sql.gz`).
- **Messages are kept.** Retention no longer deletes Telegram messages; Telegram destinations have no
  retention setting any more.
- **Self-hosted Bot API server** ([telegram-bot-api](https://github.com/tdlib/telegram-bot-api)): up to 2 GB per
  part instead of 50 MB.
  - Optional `telegram-bot-api` service in `docker-compose.yml` (`docker compose --profile telegram up -d`).
  - `ACOPY_Telegram__ApiUrl` — the default server for every Telegram URL left blank in the UI.
  - **Log out from public API** in the destination form — the one-time step to move a bot over.
  - **Shared upload folder** (`ACOPY_Telegram__UploadDir` / `ACOPY_Telegram__ServerUploadDir`): files are
    written to a subfolder of the server's data folder and sent by path instead of over HTTP. One copy per
    database for all chats and destinations, cleaned up by the database's retention; `/backups` is never
    shared with the server.

### Destinations
- rclone destinations: **File names → Always timestamped** stores rolling databases under dated names, kept by
  count / age on the remote. The default stays *Same as local*.

### Upgrading from 0.1.0
- The database is migrated on start (one new column).
- Old Telegram messages are no longer pruned; delete them in the chat if you need to. Retention values set on a
  Telegram destination are ignored.
- Rolling databases now reach Telegram under dated names, so new messages no longer reuse the slot names.

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

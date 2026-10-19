# ACopy — database backups for your Docker host

**ACopy** backs up the databases on your Docker host on a schedule, keeps the last *N* copies (or the
last *N* days), encrypts them if you like, copies them off-site, and restores them with one click.
Point it at a database from the web UI — or just put a label on the database container.

- **Many engines:** PostgreSQL, MySQL, MariaDB, MongoDB, Redis / Valkey, SQLite, SQL Server, plus a
  *custom command* for anything else (CouchDB, InfluxDB, ClickHouse, a directory tarball, …).
- **Two ways to dump:**
  - **Docker exec** — runs the database's *own* dump tool inside its container via the Docker API.
    No ports to expose, tool versions always match the server, credentials are read from the container's
    environment (`POSTGRES_PASSWORD`, `MYSQL_ROOT_PASSWORD`, `MONGO_INITDB_ROOT_PASSWORD`, …).
  - **Network** — connects over TCP with the client tools bundled in the image
    (pg_dump 18, mariadb-dump, mongodump, redis-cli, sqlite3).
- **Configure by Docker labels** — `acopy.enable=true` on a container and it shows up automatically;
  several databases or jobs per container.
- **One-click restore** — from the local file or a remote copy, with an automatic safety backup first.
- **Encryption** — a passphrase (OpenSSL-compatible) or **age** public keys; decryptable without ACopy.
- **Off-site copies** — any **rclone** backend (S3 / MinIO / R2 / B2 / SFTP / WebDAV / SMB / FTP / Drive / …)
  or a **Telegram** chat, each with its own retention.
- **Retention** by count, by age, or both. **Sync-friendly naming:** timestamped files
  (`app_20261019-030000.sql.gz`) *or* rolling slots (`app.0.sql.gz` → `app.1…` → back to `0`).
- **Safe writes:** dumps go to a temp file named `.syncthing.acopy-….tmp` (Syncthing never syncs those) and
  are renamed atomically when complete. A failed run never touches existing copies.
- Notifications via webhook (Discord / Slack / Mattermost / JSON) and **Telegram**. SHA-256 of every file,
  run history with tool output, downloads, connection tests. One container, one SQLite file for state.

## Quick start

```yaml
services:
  acopy:
    image: jncchds/acopy:latest
    container_name: acopy
    ports:
      - "5005:8080"
    environment:
      TZ: Europe/Berlin                 # schedules and file timestamps use this zone
      # ACOPY_Auth__Password: change-me # optional — otherwise you choose it on first visit
      # PUID: "1000"                    # optional — own the backup files as this user (see below)
      # PGID: "1000"
    volumes:
      - ./acopy-data:/data              # ACopy's own SQLite database (acopy.db)
      - ./backups:/backups              # all backup files
      - /var/run/docker.sock:/var/run/docker.sock:ro   # docker exec mode + label discovery
    restart: unless-stopped
```

Open `http://<host>:5005`, set the admin password, and add a database — or label one:

```yaml
services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_PASSWORD: secret
    labels:
      acopy.enable: "true"
      acopy.schedule: "0 3 * * *"   # daily at 03:00
      acopy.keep: "7"
      acopy.naming: "rolling"       # postgres.0.sql.gz … postgres.6.sql.gz
      acopy.encryption: "offsite"   # a key created under Encryption keys
      acopy.destinations: "s3"      # a destination created under Destinations
```

### The two mounts

| Mount | What lives there |
|---|---|
| `/data` | `acopy.db` — targets, run history, settings, keys and the encryption key ring. Back this up too (it is small). |
| `/backups` | `/backups/<folder>/<name>…` — one folder per database. Point Syncthing / restic here, or use a destination. |

> **Mount a directory for `/data`, not the single `acopy.db` file.** A single-file bind works (ACopy keeps
> SQLite in rollback-journal mode so the file is always complete), but Docker creates a *directory* if the
> host file does not exist yet, and SQLite needs to create its short-lived journal next to the file.
> If you do bind a file, create it first (`touch acopy.db`) and set `ACOPY_Paths__Database` accordingly.

### Docker access

Docker access is optional. Without it, only *network* mode works and labels are not read.

- **Socket:** mount `/var/run/docker.sock`. Access to the socket is equivalent to root on the host
  (ACopy execs into containers), and `:ro` on the mount does not limit the API.
- **TCP / socket proxy:** set `ACOPY_Docker__Host=tcp://docker-proxy:2375` (or the standard `DOCKER_HOST`).
  With [docker-socket-proxy](https://github.com/Tecnativa/docker-socket-proxy) enable
  `CONTAINERS=1 EXEC=1 POST=1` (exec and the archive upload used by restores need POST/PUT).
  For TLS (`tcp://host:2376`) set `ACOPY_Docker__Tls=true` and mount `ca.pem`, `cert.pem`, `key.pem` into
  the folder named by `ACOPY_Docker__CertPath` (or `DOCKER_CERT_PATH`).

## Docker labels

| Label | Default | Meaning |
|---|---|---|
| `acopy.enable` | — | `true` to manage this container (required) |
| `acopy.name` | container name | Display name and file base name |
| `acopy.engine` | from the image | `postgresql`, `mysql`, `mariadb`, `mongodb`, `redis`, `sqlite`, `sqlserver`, `custom` |
| `acopy.mode` | `exec` | `exec` (dump inside the container) or `network` |
| `acopy.schedule` | `0 3 * * *` | Cron, 5 fields (or 6 with seconds) |
| `acopy.keep` | `7` | Copies to keep — the number of slots in rolling mode; `0` = unlimited (with `max_age_days`) |
| `acopy.max_age_days` | — | Also delete copies older than this (timestamp naming; the newest is always kept) |
| `acopy.naming` | `timestamp` | `timestamp` or `rolling` |
| `acopy.compression` | `gzip` | `gzip`, `brotli`, `none` |
| `acopy.encryption` | — | Name of an encryption key |
| `acopy.destinations` | — | Comma-separated destination names |
| `acopy.database` | all | Database name (empty = all where supported); SQLite: file path. **Comma-separated = one target per database** |
| `acopy.user` | from env | Database user |
| `acopy.password_env` | — | Read the password from this env var of the container |
| `acopy.password` | from env | Literal password (prefer `password_env`) |
| `acopy.host` / `acopy.port` | container name / default | Network mode only |
| `acopy.args` | — | Extra dump-tool arguments; for `custom` the shell command |
| `acopy.restore_command` | — | `custom`: shell command that receives the backup on stdin |
| `acopy.extension` | — | `custom`: output file extension |
| `acopy.folder` | name | Sub-folder under `/backups` |
| `acopy.timeout` | `120` | Minutes before a run is killed |
| `acopy.jobs.<job>.<key>` | — | Several jobs on one container (see below) |

**Several targets per container.** `acopy.database: "app,analytics"` creates `<name>-app` and
`<name>-analytics`. For different settings per job, use named jobs — each inherits the base labels and
overrides what it sets:

```yaml
labels:
  acopy.enable: "true"
  acopy.destinations: "s3"
  acopy.jobs.app.database: "app"            # → postgres-app, daily, 7 copies, to s3
  acopy.jobs.hourly.schedule: "0 * * * *"   # → postgres-hourly: whole cluster, 24 rolling slots
  acopy.jobs.hourly.naming: "rolling"
  acopy.jobs.hourly.keep: "24"
```

Labelled targets are re-synced every minute. Remove the label or the container and the target is marked
*orphaned*: it stops running but its history and files stay until you delete it. Pausing a labelled
target in the UI survives re-syncs.

## Engines

| Engine | Network | Exec | Output | Restore |
|---|---|---|---|---|
| PostgreSQL | `pg_dump` / `pg_dumpall` (v18 — dumps any older server) | server's own | `.sql` (`--clean --if-exists`) | `psql` ✓ |
| MySQL / MariaDB | `mariadb-dump --single-transaction` | `mariadb-dump` or `mysqldump` | `.sql` | `mariadb` / `mysql` ✓ |
| MongoDB | `mongodump --archive` | same | `.archive` | `mongorestore --drop` ✓ |
| Redis / Valkey | `redis-cli --rdb` | same | `.rdb` | manual (copy as `dump.rdb`) |
| SQLite | online backup API on a file mounted into ACopy | `sqlite3 .backup` (or a plain copy) | `.sqlite` | backup API / `sqlite3 .restore` ✓ |
| SQL Server | — | `BACKUP DATABASE … WITH COPY_ONLY` via `sqlcmd` | `.bak` | `RESTORE … WITH REPLACE` ✓ |
| Custom | `sh -c "<command>"` in ACopy | `sh -c` in the container | your choice | your restore command ✓ |

Custom commands get `$ACOPY_HOST`, `$ACOPY_PORT`, `$ACOPY_USER`, `$ACOPY_PASSWORD`, `$ACOPY_DATABASE`;
the backup command writes to stdout, the restore command reads stdin. Passwords are passed through
environment variables or a 0600 config file, never on the command line of the bundled tools.

## Restore

Every successful backup has a **↺ Restore** button. ACopy decrypts and decompresses the file (or first
downloads it from an rclone destination when the local copy is gone), then loads it into the database —
through the bundled clients in network mode, or by copying it into the container and running the
container's own tools in exec mode. By default a **safety backup** of the current state is taken first, so
a restore can itself be undone. You confirm by typing the database name.

Restores replace data in place: single-database PostgreSQL dumps use `--clean --if-exists`, MySQL dumps
drop and recreate tables, MongoDB uses `--drop`. Applications may see errors while a restore runs; for
whole-cluster PostgreSQL dumps, stop them first.

## Encryption

Create keys under **Encryption keys** and pick one per database (or `acopy.encryption=<name>`).
Files are encrypted after compression and get an extra suffix.

| Kind | Suffix | Decrypt without ACopy |
|---|---|---|
| **Passphrase** — AES-256-CBC, PBKDF2-SHA256 (600 000 rounds), OpenSSL format | `.enc` | `openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -in FILE -out OUT` |
| **age** — public-key ([age-encryption.org](https://age-encryption.org)), X25519 or SSH keys | `.age` | `age -d -i key.txt FILE > OUT` |

For age keys, ACopy can generate the pair (the private key is stored encrypted and can be exported — keep
a copy offline), or you give it only the public key: then a compromised ACopy cannot read old backups,
and you paste the private key when restoring.

## Destinations

Under **Destinations**, add places to copy each backup to after it is written locally:

- **Storage via rclone** — forms for S3-compatible (AWS, MinIO, Cloudflare R2, Wasabi, …), Backblaze B2,
  SFTP, WebDAV / Nextcloud, SMB, FTP and a local/mounted path; *Other* takes any rclone backend with raw
  options. Files land in `<remote path>/<database folder>/` under the same names as locally.
- **Telegram** — a bot posts each file to a chat (or forum topic) with its size and SHA-256. The public
  Bot API accepts up to 50 MB per file, so bigger backups are split into numbered parts
  (`cat file.* > file` to join); with a [self-hosted Bot API server](https://github.com/tdlib/telegram-bot-api)
  the limit is 2 GB. Old uploads are deleted from the chat by retention where Telegram allows it.

Each destination either mirrors the database's own retention or has its own (e.g. 7 copies locally,
30 days off-site). Failed uploads are shown on the run and can be retried; they also trigger a
notification.

## Syncthing and friends

Use **rolling** naming when another tool mirrors `/backups`: the set of file names never changes, so the
remote side overwrites in place instead of accumulating files. With **timestamp** naming, configure the
mirror to propagate deletions (Syncthing does by default) so retention carries over. In-progress files are
named `.syncthing.acopy-*.tmp`, which Syncthing ignores by design, so a half-written dump is never shipped.

Set `PUID`/`PGID` so the files are owned by the user your sync tool runs as.

## Notifications

In **Settings**: a webhook URL (JSON with `text` and `content`, so Slack / Discord / Mattermost webhooks
work as-is) and/or a **Telegram** bot + chat. Failures — of a backup, a restore or an upload — are always
sent; successes when you enable them.

## Configuration

Everything in `appsettings.json` can be overridden with `ACOPY_`-prefixed environment variables
(`ACOPY_Section__Key`):

| Variable | Default | Description |
|---|---|---|
| `ACOPY_Paths__Database` | `/data/acopy.db` | ACopy's SQLite database |
| `ACOPY_Paths__Backups` | `/backups` | Root folder for backup files |
| `ACOPY_Auth__Password` | — | Admin password, re-applied on every start (setup / recovery) |
| `ACOPY_Auth__Disabled` | `false` | No login at all — only behind an authenticating reverse proxy |
| `ACOPY_Docker__Enabled` | `true` | Use Docker |
| `ACOPY_Docker__Host` | `DOCKER_HOST`, else the socket | `unix:///path` or `tcp://host:port` |
| `ACOPY_Docker__Socket` | `/var/run/docker.sock` | Socket path when no host is set |
| `ACOPY_Docker__Tls` / `ACOPY_Docker__CertPath` | `false` / `DOCKER_CERT_PATH` | TLS for tcp hosts |
| `ACOPY_Docker__LabelPrefix` | `acopy` | Label namespace |
| `ACOPY_Docker__ScanSeconds` | `60` | Label re-scan interval |
| `ACOPY_Backup__MaxParallel` | `2` | Jobs running at the same time |
| `ACOPY_Backup__HistoryPerTarget` | `200` | Run-history rows kept per database (rows of files still on disk are kept) |
| `TZ` | `UTC` | Default time zone (also editable in Settings) |
| `PUID` / `PGID` | — (root) | Run as this user/group; backup files are owned by it |
| `UMASK` | `022` | File-creation mask |

Webhook, Telegram, "notify on success" and the time zone are set in the UI (Settings).

## Development

The app always runs in Docker — `docker compose up -d --build` builds the image from source and serves
it on <http://localhost:5005>.

```bash
dotnet build ACopy.slnx && dotnet test ACopy.slnx      # backend + unit tests
cd src/acopy-ui && npm ci && npx tsc -b --noEmit       # UI typecheck

# End-to-end: PostgreSQL (jobs), MariaDB, MySQL, MongoDB, Valkey, SQLite, an S3 server and a mock
# Telegram API — backups, encryption, uploads, retention, restores
docker compose -f scripts/e2e/docker-compose.e2e.yml up -d --build
node scripts/e2e/e2e.mjs
```

Stack: ASP.NET Core / .NET 10, EF Core + SQLite, React 19 + TypeScript + Vite, rclone, age. See
[AGENTS.md](https://github.com/jncchds/acopy/blob/main/AGENTS.md) for the code layout and conventions and
[RELEASE_NOTES.md](https://github.com/jncchds/acopy/blob/main/RELEASE_NOTES.md) for changes.

## License

[MIT](https://github.com/jncchds/acopy/blob/main/LICENSE)

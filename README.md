# ACopy — database backups for your Docker host

**ACopy** backs up the databases on your Docker host on a schedule and keeps the last *N* copies.
Point it at a database from the web UI — or just put a label on the database container — and it dumps it
with the right tool, compresses it, and rotates old copies out.

- **Many engines:** PostgreSQL, MySQL, MariaDB, MongoDB, Redis / Valkey, SQLite, SQL Server, plus a
  *custom command* for anything else (CouchDB, InfluxDB, ClickHouse, a directory tarball, …).
- **Two ways to dump:**
  - **Docker exec** — runs the database's *own* dump tool inside its container via the Docker socket.
    No ports to expose, tool versions always match the server, credentials are read from the container's
    environment (`POSTGRES_PASSWORD`, `MYSQL_ROOT_PASSWORD`, `MONGO_INITDB_ROOT_PASSWORD`, …).
  - **Network** — connects over TCP with the client tools bundled in the image
    (pg_dump 18, mariadb-dump, mongodump, redis-cli, sqlite3).
- **Configure by Docker labels** — `acopy.enable=true` on a container and it shows up automatically.
- **Sync-friendly file naming:** timestamped files (`app_20261019-030000.sql.gz`) *or* rolling slots
  (`app.0.sql.gz` → `app.1…` → back to `0`) so Syncthing & co. see a fixed set of files that get overwritten.
- **Safe writes:** dumps go to a temp file named `.syncthing.acopy-….tmp` (Syncthing never syncs those) and
  are renamed atomically when complete. A failed run never touches existing copies.
- Per-database cron schedule, retention, compression (gzip / brotli / none), timeout and folder;
  SHA-256 of every file; run history with tool output; download from the UI; connection test;
  webhook notifications (Discord / Slack / Mattermost / any JSON endpoint).
- One container, one SQLite file for state. Passwords are encrypted at rest.

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
```

### The two mounts

| Mount | What lives there |
|---|---|
| `/data` | `acopy.db` — targets, run history, settings, the encryption key ring. Back this up too (it is small). |
| `/backups` | `/backups/<folder>/<name>…` — one folder per database. Point Syncthing / rclone / restic here. |

> **Mount a directory for `/data`, not the single `acopy.db` file.** A single-file bind works (ACopy keeps
> SQLite in rollback-journal mode so the file is always complete), but Docker creates a *directory* if the
> host file does not exist yet, and SQLite needs to create its short-lived journal next to the file.
> If you do bind a file, create it first (`touch acopy.db`) and set `ACOPY_Paths__Database` accordingly.

### The Docker socket

The socket is optional. Without it, only *network* mode works and labels are not read. Be aware that
access to the Docker socket is equivalent to root on the host (ACopy execs into containers), and `:ro`
on the mount does not limit the API. If that is too much trust, leave the socket out and use network
mode only. (ACopy talks to a unix socket; TCP Docker endpoints such as socket proxies are not supported yet.)

## Docker labels

| Label | Default | Meaning |
|---|---|---|
| `acopy.enable` | — | `true` to manage this container (required) |
| `acopy.name` | container name | Display name and file base name |
| `acopy.engine` | from the image | `postgresql`, `mysql`, `mariadb`, `mongodb`, `redis`, `sqlite`, `sqlserver`, `custom` |
| `acopy.mode` | `exec` | `exec` (dump inside the container) or `network` |
| `acopy.schedule` | `0 3 * * *` | Cron, 5 fields (or 6 with seconds) |
| `acopy.keep` | `7` | Copies to keep — the number of slots in rolling mode |
| `acopy.naming` | `timestamp` | `timestamp` or `rolling` |
| `acopy.compression` | `gzip` | `gzip`, `brotli`, `none` |
| `acopy.database` | all | Database name (empty = all where supported); SQLite: file path |
| `acopy.user` | from env | Database user |
| `acopy.password_env` | — | Read the password from this env var of the container |
| `acopy.password` | from env | Literal password (prefer `password_env`) |
| `acopy.host` / `acopy.port` | container name / default | Network mode only |
| `acopy.args` | — | Extra dump-tool arguments; for `custom` the shell command |
| `acopy.extension` | — | `custom`: output file extension |
| `acopy.folder` | name | Sub-folder under `/backups` |
| `acopy.timeout` | `120` | Minutes before a run is killed |

Labelled targets are re-synced every minute. Remove the label or the container and the target is marked
*orphaned*: it stops running but its history and files stay until you delete it. Pausing a labelled
target in the UI survives re-syncs.

## Engines

| Engine | Network | Exec | Output | Restore with |
|---|---|---|---|---|
| PostgreSQL | `pg_dump` / `pg_dumpall` (v18 — dumps any older server) | server's own | `.sql` | `psql` |
| MySQL / MariaDB | `mariadb-dump --single-transaction` | `mariadb-dump` or `mysqldump` | `.sql` | `mysql` / `mariadb` |
| MongoDB | `mongodump --archive` | same | `.archive` | `mongorestore --archive` |
| Redis / Valkey | `redis-cli --rdb` | same | `.rdb` | copy as `dump.rdb` |
| SQLite | online backup API on a file mounted into ACopy | `sqlite3 .backup` (or a plain copy) | `.sqlite` | copy the file |
| SQL Server | — | `BACKUP DATABASE … WITH COPY_ONLY` via `sqlcmd` | `.bak` | `RESTORE DATABASE` |
| Custom | `sh -c "<command>"` in ACopy | `sh -c` in the container | your choice | — |

Custom commands get `$ACOPY_HOST`, `$ACOPY_PORT`, `$ACOPY_USER`, `$ACOPY_PASSWORD`, `$ACOPY_DATABASE`
and must write the backup to stdout. Passwords are passed through environment variables or a 0600
config file, never on the command line of the bundled tools.

## Syncthing and friends

Use **rolling** naming when another tool mirrors `/backups`: the set of file names never changes, so the
remote side overwrites in place instead of accumulating files. With **timestamp** naming, configure the
mirror to propagate deletions (Syncthing does by default) so retention carries over. In-progress files are
named `.syncthing.acopy-*.tmp`, which Syncthing ignores by design, so a half-written dump is never shipped.

Set `PUID`/`PGID` so the files are owned by the user your sync tool runs as.

## Configuration

Everything in `appsettings.json` can be overridden with `ACOPY_`-prefixed environment variables
(`ACOPY_Section__Key`):

| Variable | Default | Description |
|---|---|---|
| `ACOPY_Paths__Database` | `/data/acopy.db` | ACopy's SQLite database |
| `ACOPY_Paths__Backups` | `/backups` | Root folder for backup files |
| `ACOPY_Auth__Password` | — | Admin password, re-applied on every start (setup / recovery) |
| `ACOPY_Auth__Disabled` | `false` | No login at all — only behind an authenticating reverse proxy |
| `ACOPY_Docker__Enabled` | `true` | Use the Docker socket |
| `ACOPY_Docker__Socket` | `/var/run/docker.sock` | Socket path (point at a socket proxy if you use one) |
| `ACOPY_Docker__LabelPrefix` | `acopy` | Label namespace |
| `ACOPY_Docker__ScanSeconds` | `60` | Label re-scan interval |
| `ACOPY_Backup__MaxParallel` | `2` | Backups running at the same time |
| `ACOPY_Backup__HistoryPerTarget` | `200` | Run-history rows kept per database |
| `TZ` | `UTC` | Default time zone (also editable in Settings) |
| `PUID` / `PGID` | — (root) | Run as this user/group; backup files are owned by it |
| `UMASK` | `022` | File-creation mask |

Webhook URL, "notify on success" and the time zone are set in the UI (Settings).

## Development

The app always runs in Docker — `docker compose up -d --build` builds the image from source and serves
it on <http://localhost:5005>.

```bash
dotnet build ACopy.slnx && dotnet test ACopy.slnx      # backend + unit tests
cd src/acopy-ui && npm ci && npx tsc -b --noEmit       # UI typecheck

# End-to-end: ACopy + PostgreSQL, MariaDB, MySQL, MongoDB, Valkey and a SQLite file
docker compose -f scripts/e2e/docker-compose.e2e.yml up -d --build
node scripts/e2e/e2e.mjs
```

Stack: ASP.NET Core / .NET 10, EF Core + SQLite, React 19 + TypeScript + Vite. See
[AGENTS.md](https://github.com/jncchds/acopy/blob/main/AGENTS.md) for the code layout and conventions and
[RELEASE_NOTES.md](https://github.com/jncchds/acopy/blob/main/RELEASE_NOTES.md) for changes.

## License

[MIT](https://github.com/jncchds/acopy/blob/main/LICENSE)

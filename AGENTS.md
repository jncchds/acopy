# ACopy — agent guide

ACopy is a single-container service that backs up databases on a Docker host on a schedule, keeping the
last N copies per database. Configuration comes from the web UI or from Docker labels on the database
containers. The user-facing description is [README.md](README.md) (it is also the Docker Hub description).

## Hard rules

- **The app always runs in Docker.** `docker compose up -d --build` is the way to run it — no `dotnet run`
  or `vite dev`, not even in development. Compile-only checks are fine.
- **Migrations are authored with `dotnet ef`, never by hand** (`dotnet tool restore` gets the pinned tool):
  `cd src/ACopy.Api && dotnet ef migrations add <Name> -o Data/Migrations`. The app migrates on start.
- **Manual database access is read-only.** Schema changes go through migrations, data changes through the app.
- **Never delete a file ACopy did not write.** Retention only matches names this target produces
  (`BackupFiles.FilesToPrune` / `OwnedFiles`); keep it that way.
- **Secrets never reach a command line or the browser.** Passwords go through env vars (`PGPASSWORD`,
  `MYSQL_PWD`, `REDISCLI_AUTH`, `SQLCMDPASSWORD`) or a 0600 temp config file; DTOs expose `hasPassword` only.
- After every change keep green: `dotnet build ACopy.slnx`, `dotnet test ACopy.slnx`,
  `npx tsc -b --noEmit` (in `src/acopy-ui`). For engine/runner changes also run the e2e stack (below).
- **Versioning:** `VERSION` holds the app version (shown in the UI pill, tagged on the image). Every
  user-visible change gets an entry at the top of `RELEASE_NOTES.md`; bump `VERSION` when releasing.

## Layout

```
src/ACopy.Api/
  Data/        EF Core entities (BackupTarget, BackupRun, AppSetting), context, migrations
  Engines/     EngineCatalog (per-engine facts, image detection, env credentials) and
               DumpCommandBuilder (argv/env for network and docker-exec modes)
  Docker/      DockerClient (raw Engine API over the unix socket: list, inspect, exec + demux),
               LabelConfig (label parsing), LabelDiscovery (periodic sync into targets)
  Services/    BackupRunner (execute, compress, atomic rename, retention, history),
               Scheduler (cron via Cronos), BackupFiles (naming/retention, pure),
               ConnectionTester, Notifier (webhook), SettingsStore (+ password hashing)
  Endpoints/   Minimal-API endpoint groups and DTOs
src/ACopy.Tests/ xUnit tests (naming, retention, labels, command building, docker stream demux)
src/acopy-ui/    React 19 + TypeScript + Vite SPA — plain CSS, one global stylesheet
docker/          entrypoint.sh (PUID/PGID drop, docker-socket group)
scripts/e2e/     docker-compose.e2e.yml + e2e.mjs — real databases, end to end
```

## Conventions

- **UI** follows the shared portable design system (one global `index.css`, token-only colours,
  single context-aware left rail, emoji icons, `.btn-primary` opt-in buttons). ACopy's palette is
  **Copper & Patina**: copper accent (`--accent`, washes via `rgb(var(--accent-rgb) / α)`) on
  verdigris-tinted neutrals; `--patina` marks "inside the container" (exec mode); `--warning` is yellow so
  it never reads as the accent. Change colours only in the token block at the top of `index.css`.
- Config: `ACOPY_`-prefixed env vars override `appsettings.json` (`ACOPY_Section__Key`).
- SQLite runs in rollback-journal mode (single-file friendly) with `busy_timeout` set on every connection
  (`BusyTimeoutInterceptor`) — do not switch to WAL without revisiting the single-file bind story.
- Temp files are `.syncthing.acopy-<final>.tmp` in the target folder; the final step is an atomic rename.
  Leftovers are removed on start-up.

## Verification

```bash
docker compose -f scripts/e2e/docker-compose.e2e.yml up -d --build
node scripts/e2e/e2e.mjs        # UI: http://localhost:5006, password e2e-password
docker compose -f scripts/e2e/docker-compose.e2e.yml down -v
```

The e2e script discovers the labelled containers, adds a network-mode SQLite target, tests each
connection, backs everything up and prints status/size/errors (exit code 1 on any failure).
SQL Server is not in the e2e stack (its image is amd64-only).

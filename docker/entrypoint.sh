#!/bin/sh
# ACopy entrypoint. By default the app runs as root (simplest with the Docker socket and bind mounts).
# Set PUID/PGID to run as that user instead: backup files are then owned by it — handy when Syncthing
# or another non-root process picks them up. The user is added to the Docker socket's group.
set -e

# Backups are background work: run ACopy and every dump/compress/upload it starts at low CPU and disk
# priority so a large backup never starves the databases and services it shares the host with.
# NICE=0 turns this off. (Dumps in exec mode run inside the database container and are not affected.)
NICE="${NICE:-10}"
if [ "$NICE" != "0" ]; then
  renice -n "$NICE" -p $$ >/dev/null 2>&1 || true
  ionice -c 2 -n 7 -p $$ >/dev/null 2>&1 || true
fi

if [ -n "$PUID" ] && [ "$PUID" != "0" ]; then
  PGID="${PGID:-$PUID}"
  getent group acopy >/dev/null 2>&1 || addgroup -g "$PGID" acopy 2>/dev/null || addgroup acopy
  getent passwd acopy >/dev/null 2>&1 || adduser -D -H -u "$PUID" -G acopy -s /sbin/nologin acopy

  SOCK="${ACOPY_Docker__Socket:-/var/run/docker.sock}"
  if [ -S "$SOCK" ]; then
    SGID=$(stat -c %g "$SOCK")
    SGROUP=$(getent group "$SGID" | cut -d: -f1)
    if [ -z "$SGROUP" ]; then SGROUP=dockersock; addgroup -g "$SGID" "$SGROUP"; fi
    addgroup acopy "$SGROUP" 2>/dev/null || true
  fi

  DB="${ACOPY_Paths__Database:-/data/acopy.db}"
  BK="${ACOPY_Paths__Backups:-/backups}"
  mkdir -p "$(dirname "$DB")" "$BK"
  chown acopy:acopy "$(dirname "$DB")" "$BK" 2>/dev/null || true
  [ -f "$DB" ] && chown acopy:acopy "$DB" 2>/dev/null || true
  umask "${UMASK:-022}"
  exec su-exec acopy "$@"
fi

umask "${UMASK:-022}"
exec "$@"

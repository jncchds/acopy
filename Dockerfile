# ── Stage 1: Build React UI ───────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM node:22-alpine AS ui-build
WORKDIR /ui
COPY src/acopy-ui/package*.json ./
RUN npm ci
COPY src/acopy-ui/ ./
COPY VERSION /VERSION
# Build to ./dist (the dev outDir points at the API wwwroot; override here)
RUN npx vite build --outDir ./dist --emptyOutDir

# ── Stage 2: Build .NET API ────────────────────────────────────────────────────
# Portable (RID-less) publish: architecture-neutral output, so this stage runs natively even for
# multi-arch builds; SQLite picks its linux-musl native library at runtime.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS api-build
WORKDIR /src
COPY ACopy.slnx ./
COPY src/ACopy.Api/ACopy.Api.csproj src/ACopy.Api/
RUN dotnet restore src/ACopy.Api/ACopy.Api.csproj
COPY src/ACopy.Api/ src/ACopy.Api/
RUN dotnet publish src/ACopy.Api/ACopy.Api.csproj -c Release --no-restore -o /app/publish

# ── Stage 3: Runtime with database client tools ────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Client tools for network-mode backups. pg_dump 18 dumps any older server too.
# mariadb-connector-c brings the caching_sha2_password plugin MySQL 8+ needs.
# valkey-cli is protocol-compatible with redis-cli (--rdb included); exposed under both names.
RUN apk add --no-cache \
        postgresql18-client \
        mariadb-client \
        mariadb-connector-c \
        mongodb-tools \
        valkey-cli \
        sqlite \
        tzdata \
        su-exec \
        curl \
    && { command -v redis-cli >/dev/null || ln -s "$(command -v valkey-cli)" /usr/local/bin/redis-cli; }

COPY --from=api-build /app/publish ./
COPY --from=ui-build /ui/dist ./wwwroot
COPY VERSION ./VERSION
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh && mkdir -p /data /backups

ENV ASPNETCORE_URLS=http://+:8080 \
    ACOPY_Paths__Database=/data/acopy.db \
    ACOPY_Paths__Backups=/backups \
    TZ=UTC
EXPOSE 8080
VOLUME ["/data", "/backups"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["/entrypoint.sh"]
CMD ["dotnet", "ACopy.Api.dll"]

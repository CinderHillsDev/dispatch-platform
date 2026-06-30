# syntax=docker/dockerfile:1
#
# Dispatch SMTP Relay - container image (multi-arch: linux/amd64 + linux/arm64).
#
# Build:  docker build -t dispatch .
# Run:    see docker-compose.yml (brings up Dispatch + SQL together).
#
# Config (spec §12.1): the image takes ONLY the two bootstrap settings from the environment -
#   ConnectionStrings__DispatchLog   the SQL connection string (required)
#   AdminPassword                    first-run dashboard admin password seed (optional)
# Everything else is seeded into the SQL config table on first run and managed in the dashboard.

# --- Stage 1: build the React dashboard -------------------------------------------------------
FROM node:22-alpine AS ui
WORKDIR /ui
COPY src/Dispatch.UI/package*.json ./
RUN npm ci
COPY src/Dispatch.UI/ ./
RUN npm run build

# --- Stage 2: publish the .NET service with the UI embedded -----------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# The dashboard is embedded into Dispatch.Web as wwwroot (spec §19.4); drop the built assets in
# before publish so they get embedded.
RUN rm -rf src/Dispatch.Web/wwwroot && mkdir -p src/Dispatch.Web/wwwroot
COPY --from=ui /ui/dist/ src/Dispatch.Web/wwwroot/
RUN dotnet publish src/Dispatch.Service -c Release -o /app

# --- Stage 3: runtime ------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# curl is used only by the container HEALTHCHECK below (the slim runtime image ships without it).
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app ./
# Spool (./.dispatch-spool, resolved against the content root /app) and logs live on volumes.
# Store the at-rest encryption key in the (persistent) spool volume so it survives container recreation -
# the content root /app is ephemeral. Without this the key would change each recreate and stored provider
# secrets couldn't be decrypted.
ENV DISPATCH_LOG_DIR=/var/log/dispatch
ENV DISPATCH_KEY_DIR=/app/.dispatch-spool
RUN mkdir -p /var/log/dispatch /app/.dispatch-spool
# Dashboard (8420), ingestion API (8025), SMTP (25 & 587) - all configurable in the dashboard afterwards.
# The container runs as root so it can bind the privileged SMTP ports; the listener falls back to 2525 only
# if 25 is unavailable.
EXPOSE 8420 8025 25 587
# Report container health from the unauthenticated /health endpoint. The dashboard is HTTPS-only with a
# self-signed cert by default, so use https + -k. start-period covers first-run schema init + SQL connect.
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=4 \
    CMD curl -fsSk https://localhost:8420/health || exit 1
# UseSystemd()/UseWindowsService() in Program.cs are no-ops here; the service runs as PID 1.
ENTRYPOINT ["./Dispatch.Service"]

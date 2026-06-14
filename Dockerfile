# syntax=docker/dockerfile:1
#
# Dispatch SMTP Relay — container image (multi-arch: linux/amd64 + linux/arm64).
#
# Build:  docker build -t dispatch .
# Run:    see docker-compose.yml (brings up Dispatch + SQL together).
#
# Config (spec §12.1): the image takes ONLY the two bootstrap settings from the environment —
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
ENV DISPATCH_LOG_DIR=/var/log/dispatch
RUN mkdir -p /var/log/dispatch /app/.dispatch-spool
# Dashboard (8420), ingestion API (8421), SMTP (2525) — all configurable in the dashboard afterwards.
EXPOSE 8420 8421 2525
# Report container health from the unauthenticated /health endpoint (allow-list permits private/NAT
# sources by default). start-period covers first-run schema init + SQL connect.
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=4 \
    CMD curl -fsS http://localhost:8420/health || exit 1
# UseSystemd()/UseWindowsService() in Program.cs are no-ops here; the service runs as PID 1.
ENTRYPOINT ["./Dispatch.Service"]

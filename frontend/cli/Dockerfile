# Cerberus CLI (terminal client) in a container, for machines without Bun.
# Build from the repository root (the CLI is part of a Bun workspace):
#
#   docker build -f frontend/cli/Dockerfile -t cerberus-cli .
#
# It is interactive: run it with a terminal attached, e.g.
#   docker compose run --rm cli
#
# The base image is an exact version.

FROM oven/bun:1.4.4 AS runtime
WORKDIR /app

# The lockfile covers all four workspaces, so their manifests must be present.
# The CLI uses explicit HTTP contracts and needs no generated database client;
# install scripts stay disabled.
COPY package.json bun.lock tsconfig.base.json ./
COPY frontend/cli/package.json      frontend/cli/
COPY frontend/server/package.json   frontend/server/
COPY frontend/shared/package.json   frontend/shared/
COPY frontend/database/package.json frontend/database/
RUN bun install --frozen-lockfile --ignore-scripts

COPY frontend/cli    frontend/cli
COPY frontend/shared frontend/shared
COPY LICENSE NOTICE ./

# This client connects to an existing API; it never starts another backend.
# Its session token is kept in the mounted home directory between runs.
ENV API_URL=http://api:3000 \
    CERBERUS_AUTOSTART=off \
    HOME=/home/bun \
    TERM=xterm-256color

# The CLI's file tools act on the directory it is started in: mount the project there.
WORKDIR /workspace
USER bun
ENTRYPOINT ["bun", "run", "/app/frontend/cli/src/main.ts"]

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

# Install the workspace's dependencies from the lockfile. Install scripts are
# skipped: the only one generates the TypeScript server's database client,
# which the CLI does not use.
COPY package.json bun.lock tsconfig.base.json ./
COPY frontend/cli/package.json      frontend/cli/
COPY frontend/server/package.json   frontend/server/
COPY frontend/shared/package.json   frontend/shared/
COPY frontend/database/package.json frontend/database/
RUN bun install --frozen-lockfile --ignore-scripts

COPY frontend/cli    frontend/cli
COPY frontend/shared frontend/shared
# Only its types are imported by the CLI (the typed client for auth, sessions and chat).
COPY frontend/server frontend/server

# The API it talks to, and where its session token is kept between runs.
ENV API_URL=http://api:3000 \
    HOME=/home/bun \
    TERM=xterm-256color

# The CLI's file tools act on the directory it is started in: mount the project there.
WORKDIR /workspace
USER bun
ENTRYPOINT ["bun", "run", "/app/frontend/cli/src/main.ts"]

# Cerberus CLI (terminal client) in a container, for machines without Bun.
# Build from the repository root (the CLI is part of a Bun workspace):
#
#   docker build -f packages/cli/Dockerfile -t cerberus-cli .
#
# It is interactive: run it with a terminal attached, e.g.
#   docker compose run --rm cli
#
# NOTE: written without being built or run (see PLAN.md §15).

FROM oven/bun:1 AS runtime
WORKDIR /app

# Install the workspace's dependencies from the lockfile. Install scripts are
# skipped: the only one generates the TypeScript server's database client,
# which the CLI does not use.
COPY package.json bun.lock tsconfig.base.json ./
COPY packages/cli/package.json      packages/cli/
COPY packages/server/package.json   packages/server/
COPY packages/shared/package.json   packages/shared/
COPY packages/database/package.json packages/database/
RUN bun install --frozen-lockfile --ignore-scripts

COPY packages/cli    packages/cli
COPY packages/shared packages/shared
# Only its types are imported by the CLI (the typed client for auth, sessions and chat).
COPY packages/server packages/server

# The API it talks to, and where its session token is kept between runs.
ENV API_URL=http://api:3000 \
    HOME=/home/bun \
    TERM=xterm-256color

# The CLI's file tools act on the directory it is started in: mount the project there.
WORKDIR /workspace
USER bun
ENTRYPOINT ["bun", "run", "/app/packages/cli/src/index.tsx"]

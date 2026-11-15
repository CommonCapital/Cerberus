# Cerberus CLI (terminal client) in a container, for machines without Bun.
# Build from the repository root (the CLI is part of a Bun workspace):
#
#   docker build -f packages/cli/Dockerfile -t cerberus-cli .
#
# It is interactive: run it with a terminal attached, e.g.
#   docker compose run --rm cli
#
# First built and run on 2026-11-14 (PLAN.md §15). The base image is an exact version.

FROM oven/bun:1.4.4 AS runtime
WORKDIR /app

# Install only the CLI and shared contracts. No server, database client or
# generated code is needed; install scripts stay disabled.
COPY package.json bun.lock tsconfig.base.json ./
COPY packages/cli/package.json      packages/cli/
COPY packages/shared/package.json   packages/shared/
RUN bun install --frozen-lockfile --ignore-scripts

COPY packages/cli    packages/cli
COPY packages/shared packages/shared

# The API it talks to, and where its session token is kept between runs.
ENV API_URL=http://api:3000 \
    HOME=/home/bun \
    TERM=xterm-256color

# The CLI's file tools act on the directory it is started in: mount the project there.
WORKDIR /workspace
USER bun
ENTRYPOINT ["bun", "run", "/app/packages/cli/src/main.ts"]

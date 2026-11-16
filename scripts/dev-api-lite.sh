#!/bin/sh
# A foreground API for small development machines. These are concurrency
# limits and runtime hints, not operating-system CPU or memory quotas.
# No watcher, local model, container or infrastructure service is started.
set -eu

if [ "$#" -ne 0 ]; then
    printf '%s\n' 'Usage: sh scripts/dev-api-lite.sh (configure PORT and CERBERUS_* through the environment)' >&2
    exit 64
fi

if ! command -v dotnet >/dev/null 2>&1; then
    printf '%s\n' 'Cerberus needs the .NET 10 SDK. Install it, then run dev:api:lite again; nothing was started.' >&2
    exit 127
fi

cerberus_sdks=$(dotnet --list-sdks)
if ! printf '%s\n' "$cerberus_sdks" | grep -q '^10\.'; then
    printf '%s\n' 'Cerberus needs the .NET 10 SDK. No .NET 10 SDK is installed; nothing was started.' >&2
    exit 1
fi

cerberus_root=$(CDPATH= cd -P "$(dirname "$0")/.." && pwd)
cd "$cerberus_root"

# An explicitly selected lite launcher always applies its small worker pool.
# Other settings, including PORT, CERBERUS_DB and CERBERUS_KEYRING, survive.
export CERBERUS_JOB_CONCURRENCY=1 CERBERUS_JOB_PER_USER=1
export CERBERUS_CHAT_CONCURRENCY=1 CERBERUS_CHAT_PER_USER=1
export CERBERUS_AGENT_PARALLELISM=1 DOTNET_PROCESSOR_COUNT=2
export DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true

if command -v nice >/dev/null 2>&1; then
    set -- nice -n 10 dotnet
else
    set -- dotnet
fi

printf '%s\n' 'Building the API with one build worker; the server will run in this terminal. Ctrl+C stops it.'
"$@" build backend/src/Cerberus.Api/Cerberus.Api.csproj \
    --disable-build-servers --nologo -m:1 \
    -p:UseSharedCompilation=false -p:BuildInParallel=false -p:RestoreDisableParallel=true

# exec keeps the API attached to this terminal and preserves its exit status.
# The build above already restored and compiled it; do neither again here.
exec "$@" run --project backend/src/Cerberus.Api/Cerberus.Api.csproj \
    --no-build --no-restore --no-launch-profile

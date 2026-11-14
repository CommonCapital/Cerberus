# Cerberus API (.NET). Build from the repository root, because the image also
# carries the compliance catalogs and the skills library:
#
#   docker build -f backend/Dockerfile -t cerberus-api .
#
# First built and run on 2026-11-14 (PLAN.md §15). Base images are exact versions, not "10.0":
# the same Dockerfile gives the same image next month. Raise them together when updating .NET.

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src

# Restore first, from the project files only, so this layer is cached until a
# dependency changes.
COPY backend/Cerberus.slnx backend/
COPY backend/src/Cerberus.Api/Cerberus.Api.csproj                     backend/src/Cerberus.Api/
COPY backend/src/Cerberus.Authorization/Cerberus.Authorization.csproj backend/src/Cerberus.Authorization/
COPY backend/src/Cerberus.Compliance/Cerberus.Compliance.csproj       backend/src/Cerberus.Compliance/
COPY backend/src/Cerberus.Core/Cerberus.Core.csproj                   backend/src/Cerberus.Core/
COPY backend/src/Cerberus.Data/Cerberus.Data.csproj                   backend/src/Cerberus.Data/
COPY backend/src/Cerberus.Jobs/Cerberus.Jobs.csproj                   backend/src/Cerberus.Jobs/
COPY backend/src/Cerberus.Models/Cerberus.Models.csproj               backend/src/Cerberus.Models/
COPY backend/src/Cerberus.Scanning/Cerberus.Scanning.csproj           backend/src/Cerberus.Scanning/
COPY backend/src/Cerberus.Skills/Cerberus.Skills.csproj               backend/src/Cerberus.Skills/
COPY backend/src/Cerberus.Triage/Cerberus.Triage.csproj               backend/src/Cerberus.Triage/
RUN dotnet restore backend/src/Cerberus.Api/Cerberus.Api.csproj

COPY backend/src backend/src
RUN dotnet publish backend/src/Cerberus.Api/Cerberus.Api.csproj \
      --configuration Release --no-restore --output /app/publish

# ---- runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./
# Data the API reads at startup: control catalogs and the agents' skills library.
COPY catalogs ./catalogs
COPY security-skills/skills ./security-skills/skills
COPY security-skills/LICENSE ./security-skills/LICENSE

# /data holds everything that must survive the container: the SQLite database
# and the key ring that encrypts stored API keys. /workspace is the code to scan.
# Optional: a browser for the researcher (CERBERUS_BROWSER=on). It adds several hundred megabytes, so it is
# left out unless asked for: docker compose build --build-arg WITH_BROWSER=true api
# The browser variant has not been built yet (PLAN.md §15). If Chromium refuses to start as this non-root user
# for want of user namespaces, the choice is the host's seccomp profile or CERBERUS_BROWSER_NO_SANDBOX=true.
ARG WITH_BROWSER=false
RUN if [ "$WITH_BROWSER" = "true" ]; then \
      apt-get update && apt-get install -y --no-install-recommends chromium && rm -rf /var/lib/apt/lists/*; \
    fi

RUN mkdir -p /data /workspace && chown -R "$APP_UID" /data
VOLUME ["/data"]

ENV PORT=3000 \
    CERBERUS_BIND=0.0.0.0 \
    CERBERUS_DB=/data/cerberus.db \
    CERBERUS_KEYRING=/data/dp-keys \
    CERBERUS_SCAN_ROOT=/workspace \
    CERBERUS_CATALOG_ROOT=/app/catalogs \
    CERBERUS_SKILLS_ROOT=/app/security-skills/skills \
    DOTNET_RUNNING_IN_CONTAINER=true

# Run as the unprivileged user the base image provides.
USER $APP_UID
EXPOSE 3000
ENTRYPOINT ["dotnet", "Cerberus.Api.dll"]

# GymOS Web API — multi-stage build.
#
# Build context is the REPOSITORY ROOT, not this directory, because the build needs
# Directory.Build.props, Directory.Packages.props, and global.json. Compose sets that for you:
#   docker compose build api
# By hand:
#   docker build -f src/Host/GymOS.WebApi/Dockerfile -t gymos-api .
#
# ── Why the stages are split the way they are ────────────────────────────────────────────────
# The SDK (~800 MB) exists only to compile. Nothing it carries — compilers, NuGet caches, the
# whole restore graph — belongs in a running container, where every extra binary is attack
# surface someone has to justify during a review. The final image contains the ASP.NET runtime
# and the published output, and nothing else.
#
# ── Why the copies are ordered the way they are ──────────────────────────────────────────────
# Docker caches a layer until one of its inputs changes. Project files change rarely, source
# changes constantly, so the manifests are copied and restored *before* the source. A one-line
# edit to a .cs file therefore re-runs compile only, not a full package restore. Copying
# everything up front would make every edit pay for NuGet again.

# ═══════════════════════════════════════════════════════════════════════════════════════════
# Stage 1 — restore
# ═══════════════════════════════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

# Build-wide settings first: these govern every project, and they must ALL be present or the image
# compiles under different rules than CI does.
#
# .editorconfig is not optional here, and its absence is not obvious. Directory.Build.props sets
# TreatWarningsAsErrors, while .editorconfig is where analyzer severities are tuned — including
# `dotnet_diagnostic.CA1716.severity = none`, which is what allows the SharedKernel's `Error` type
# to exist (CA1716 objects that it collides with a Visual Basic keyword). Omit it and the build
# fails on a rule the repository has deliberately turned off. Discovered exactly that way.
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY BannedSymbols.txt ./

# Project manifests only. Central Package Management (Directory.Packages.props) means versions
# are pinned here, so this layer fully determines the restore graph.
#
# The *host project* is restored rather than GymOS.Backend.slnx: the solution also contains the
# test projects, which .dockerignore deliberately keeps out of the build context — a solution
# restore would fail looking for them. Restoring the host pulls exactly its transitive graph,
# which is what ships.
COPY src/BuildingBlocks/GymOS.SharedKernel/GymOS.SharedKernel.csproj \
     src/BuildingBlocks/GymOS.SharedKernel/
COPY src/BuildingBlocks/GymOS.BuildingBlocks.Application/GymOS.BuildingBlocks.Application.csproj \
     src/BuildingBlocks/GymOS.BuildingBlocks.Application/
COPY src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/GymOS.BuildingBlocks.Infrastructure.csproj \
     src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/
COPY src/Platform/GymOS.Platform.Domain/GymOS.Platform.Domain.csproj \
     src/Platform/GymOS.Platform.Domain/
COPY src/Platform/GymOS.Platform.Application/GymOS.Platform.Application.csproj \
     src/Platform/GymOS.Platform.Application/
COPY src/Platform/GymOS.Platform.Infrastructure/GymOS.Platform.Infrastructure.csproj \
     src/Platform/GymOS.Platform.Infrastructure/
COPY src/Host/GymOS.WebApi/GymOS.WebApi.csproj src/Host/GymOS.WebApi/

# The restored packages land in this layer, deliberately, and NOT in a --mount=type=cache.
#
# A cache mount was the obvious choice here and it was wrong, in a way that only shows up in CI.
# A cache mount is not part of any image layer: it lives in the builder. The publish stage below
# runs `--no-restore`, so it needs `obj/project.assets.json` — which IS in this layer — *and* the
# packages that file points at, which with a mount are NOT. Both only exist together while a
# single build runs the restore step and the publish step back to back.
#
# On a GitHub runner the mount starts empty every time. When BuildKit serves this layer from the
# registry/GHA cache instead of executing it — which is exactly what happens on any commit that
# does not touch these .csproj/props files — nothing is ever downloaded, and publish fails with
# `NETSDK1064: Package <first one alphabetically> was not found`. The docker job passed on the
# commit that changed Directory.Packages.props and failed on every docs-only commit after it,
# which is the signature of this and not of any package being genuinely missing.
#
# Baking them into the layer costs image size in a *build* stage that never ships — the final
# stage copies only /app/publish — and keeps the layer-caching property this file is arranged
# for: a one-line .cs edit still does not invalidate this layer, so restore still does not re-run.
# What is lost is the cross-build download cache, which the layer cache already covers.
#
# Do not reintroduce a cache mount on this path without also removing --no-restore below. Mounting
# over /root/.nuget/packages in the publish stage would additionally *hide* the packages this
# layer now contains.
RUN dotnet restore src/Host/GymOS.WebApi/GymOS.WebApi.csproj

# ═══════════════════════════════════════════════════════════════════════════════════════════
# Stage 2 — publish
# ═══════════════════════════════════════════════════════════════════════════════════════════
FROM restore AS publish
WORKDIR /src

COPY src/ src/

# ── Build identity, supplied rather than discovered ────────────────────────────────────────────
# .dockerignore excludes .git deliberately, so the SDK's SourceLink targets find no checkout here
# and stamp no commit — an image built this way would answer /version with a version and no
# identity. The caller passes both instead:
#
#   docker build --build-arg GIT_SHA=$(git rev-parse HEAD) \
#                --build-arg BUILD_TIMESTAMP=$(date -u +%Y-%m-%dT%H:%M:%SZ) …
#
# Both default to empty, and empty is honest: BuildInfo reports null rather than inventing a value.
# CI passes the same SHA it tags the image with, which is what ties a running container to a commit.
ARG GIT_SHA=""
ARG BUILD_TIMESTAMP=""

# No trimming and no ReadyToRun. Both would be defensible for start-up time, and both are
# hostile to this application: EF Core builds its model by reflection and Npgsql resolves type
# handlers dynamically, so a trimmer removes things nothing statically references and the
# failure arrives at runtime, in production, as a missing type. Start-up cost here is a health
# check waiting a second longer; the alternative is an outage nobody can reproduce locally.
#
# UseAppHost=false drops the native launcher: the entrypoint invokes `dotnet GymOS.WebApi.dll`,
# so the extra executable is weight with no purpose.
#
# No cache mount here either, and that is load-bearing rather than tidy: mounting one over
# /root/.nuget/packages would shadow the packages the restore layer now carries, and --no-restore
# would fail for the same reason it was failing before. See the note on the restore step.
RUN dotnet publish src/Host/GymOS.WebApi/GymOS.WebApi.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        -p:UseAppHost=false \
        -p:SourceRevisionId="$GIT_SHA" \
        -p:BuildTimestampUtc="$BUILD_TIMESTAMP"

# ═══════════════════════════════════════════════════════════════════════════════════════════
# Stage 3 — runtime
# ═══════════════════════════════════════════════════════════════════════════════════════════
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# Re-declared: an ARG does not cross a stage boundary. These become image metadata, which is how a
# registry, `docker inspect` and any scanner can answer "what commit is this?" without starting the
# container. The same values are inside the assembly, and /version reads them from there — this is
# the copy for tooling that cannot make an HTTP request. Standard OCI keys, so that tooling already
# knows them.
ARG GIT_SHA=""
ARG BUILD_TIMESTAMP=""
LABEL org.opencontainers.image.title="GymOS API" \
      org.opencontainers.image.description="The GymOS backend — modular monolith, .NET 10 / SQL Server" \
      org.opencontainers.image.vendor="Marios Christodoulou" \
      org.opencontainers.image.revision="$GIT_SHA" \
      org.opencontainers.image.created="$BUILD_TIMESTAMP"

# curl is the only thing added to the runtime image, and it is added for one reason: the ASP.NET
# image ships no HTTP client, so without it a container cannot answer "are you healthy?" about
# itself. The alternatives were worse — an Alpine base needs icu-libs (~30 MB) for globalization
# plus tzdata for the IANA time-zone ids AD-41 requires, which costs more than it saves, and a
# health check that only tests whether the process exists is not a health check.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=publish /app/publish ./

# The two writable paths, created here with the right ownership rather than left to Docker.
# A named volume inherits the ownership of the image directory it covers, and a directory that does
# not exist in the image is created root-owned — which an unprivileged process cannot write. The
# symptom is quiet: ASP.NET logs "An error occurred while reading the key ring" and falls back to
# in-memory DataProtection keys, so every restart invalidates whatever they protected, and the
# stack still reports healthy. Discovered exactly that way, during a production smoke test.
#
# 0700 because both hold data only this process has any business reading; group 0 matches the
# base image's convention for the app user.
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys /var/lib/gymos/uploads \
    && chown -R "${APP_UID}:0" /home/app /var/lib/gymos \
    && chmod -R 0700 /home/app/.aspnet /var/lib/gymos

# Kestrel binds a fixed, unprivileged port. 8080 because a non-root user cannot bind below 1024,
# and fixed because the health check, the compose port mapping, and any future Kubernetes probe
# all have to agree on one number.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

# The .NET images predefine a non-root `app` user (uid 1654 on the 10.0 images; the variable
# exists so the number is the image's business, not ours). Set after
# COPY so the copied files stay root-owned and therefore unwritable by the running process — the
# application has no business rewriting its own binaries.
USER $APP_UID

# Readiness, not liveness: dependents gated on `service_healthy` need to know the database is
# reachable, which is what /health/ready answers (/health/live has no dependencies by design).
# start-period covers migration and seeding on first boot.
HEALTHCHECK --interval=15s --timeout=5s --start-period=45s --retries=5 \
    CMD curl --fail --silent --show-error http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "GymOS.WebApi.dll"]

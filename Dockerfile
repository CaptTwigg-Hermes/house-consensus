# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_VERSION=dev
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props HouseConsensus.slnx ./
COPY src/Shared/HouseConsensus.Shared.csproj src/Shared/
COPY src/Client/HouseConsensus.Client.csproj src/Client/
COPY src/Server/HouseConsensus.Server.csproj src/Server/
RUN dotnet restore src/Server/HouseConsensus.Server.csproj
COPY src/Shared src/Shared
COPY src/Client src/Client
COPY src/Server src/Server
RUN dotnet publish src/Server/HouseConsensus.Server.csproj -c Release --no-restore -o /app /p:UseAppHost=false /p:InformationalVersion="${BUILD_VERSION}"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS app
RUN apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["sh","-c","wget -q -O /dev/null http://127.0.0.1:8080/health || exit 1"]
ENTRYPOINT ["dotnet","HouseConsensus.Server.dll"]

FROM ghcr.io/astral-sh/uv:python3.13-bookworm-slim AS worker-base
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates util-linux \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 house-consensus \
    && useradd --system --uid 10001 --gid house-consensus --home-dir /app house-consensus
WORKDIR /app
COPY exporter exporter
COPY ingestion ingestion
COPY manual_scoring manual_scoring
COPY scripts scripts
RUN uv sync --project ingestion --frozen --no-dev \
    && uv sync --project manual_scoring --frozen --no-dev --extra postgres \
    && chown -R house-consensus:house-consensus /app
ENV HOUSE_CONSENSUS_ROOT=/app \
    HOUSE_CONSENSUS_LOCK_DIR=/tmp/house-consensus \
    UV_FROZEN=1 \
    UV_NO_SYNC=1 \
    UV_OFFLINE=1
USER house-consensus

FROM worker-base AS ingestion-worker
ENTRYPOINT ["/app/scripts/run-native-ingestion.sh"]

FROM worker-base AS manual-scoring-worker
ENTRYPOINT ["/app/scripts/run-native-manual-scoring.sh"]

FROM app AS final

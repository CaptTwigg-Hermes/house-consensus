# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props HouseConsensus.slnx ./
COPY src/Shared/HouseConsensus.Shared.csproj src/Shared/
COPY src/Client/HouseConsensus.Client.csproj src/Client/
COPY src/Server/HouseConsensus.Server.csproj src/Server/
RUN dotnet restore src/Server/HouseConsensus.Server.csproj
COPY src/Shared src/Shared
COPY src/Client src/Client
COPY src/Server src/Server
RUN dotnet publish src/Server/HouseConsensus.Server.csproj -c Release --no-restore -o /app /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/*
RUN adduser --disabled-password --gecos "" --uid 10001 appuser
WORKDIR /app
COPY --from=build /app .
USER appuser
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["sh","-c","wget -q -O /dev/null http://127.0.0.1:8080/health || exit 1"]
ENTRYPOINT ["dotnet","HouseConsensus.Server.dll"]

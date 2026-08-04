# Build from the repository root:
#   docker build -f src/Host/GymOS.WebApi/Dockerfile -t gymos-backend .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore layers cache on the dependency files only
COPY global.json Directory.Build.props Directory.Packages.props BannedSymbols.txt ./
COPY src/ ./src/
RUN dotnet restore src/Host/GymOS.WebApi/GymOS.WebApi.csproj

RUN dotnet publish src/Host/GymOS.WebApi/GymOS.WebApi.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget -qO- http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "GymOS.WebApi.dll"]

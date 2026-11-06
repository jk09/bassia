# bassia-server: the monorepo's API, landing service and dashboard.
# Build from the repository root: docker build -f deploy/docker/server.Dockerfile -t bassia-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Bassia.Server/Bassia.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Every switch can come from BASSIA_SERVER_* (storage, state, token: see docs/reference.md).
ENV BASSIA_SERVER_URL=http://0.0.0.0:8080 \
    BASSIA_SERVER_DATA=/data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "/app/bassia-server.dll"]

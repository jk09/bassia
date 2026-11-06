# bassia-studio: the web client (versions and agentic runs).
# Build from the repository root: docker build -f deploy/docker/studio.Dockerfile -t bassia-studio .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Bassia.Studio/Bassia.Studio.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Every switch can come from BASSIA_STUDIO_* (server, token, run host, sign-in: see docs/reference.md).
ENV BASSIA_STUDIO_URL=http://0.0.0.0:8080 \
    BASSIA_STUDIO_CACHE=/tmp/bassia-studio-cache
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "/app/bassia-studio.dll"]

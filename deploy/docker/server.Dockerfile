# The Bassia server (bassia-server), configured by BASSIA_* environment variables (see Bassia.Server/ServerConfig.cs).
# Build from the repository root: docker build -f deploy/docker/server.Dockerfile -t bassia-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Bassia.Server/Bassia.Server.csproj --configuration Release --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
	&& apt-get install --yes --no-install-recommends git ca-certificates \
	&& rm -rf /var/lib/apt/lists/* \
	&& useradd --create-home --uid 10001 bassia \
	&& mkdir -p /scratch && chown bassia /scratch
COPY --from=build /out /app
USER bassia
# The git cache and scratch repositories live in the task's ephemeral storage.
ENV BASSIA_URL=http://0.0.0.0:8080 BASSIA_STATE=/scratch DOTNET_gcServer=1
EXPOSE 8080
ENTRYPOINT ["/app/bassia-server"]

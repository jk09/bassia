# An agent worker: the bassia CLI with git and an agent, running one agentic run against a Bassia server
# (see worker-entrypoint.sh). Build from the repository root: docker build -f deploy/docker/worker.Dockerfile -t bassia-worker .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Bassia/Bassia.csproj --configuration Release --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# The agent's own CLI (npm packages, e.g. @anthropic-ai/claude-code@<version>); empty for none.
ARG AGENT_PACKAGES=""
RUN apt-get update \
	&& apt-get install --yes --no-install-recommends git ca-certificates curl \
	&& if [ -n "$AGENT_PACKAGES" ]; then apt-get install --yes --no-install-recommends nodejs npm && npm install --global $AGENT_PACKAGES; fi \
	&& rm -rf /var/lib/apt/lists/* \
	&& useradd --create-home --uid 10001 bassia \
	&& mkdir -p /work && chown bassia /work
COPY --from=build /out /app
COPY deploy/docker/worker-entrypoint.sh /usr/local/bin/bassia-worker
RUN ln -s /app/Bassia /usr/local/bin/bassia && chmod +x /usr/local/bin/bassia-worker
USER bassia
WORKDIR /work
ENV BASSIA_CREDENTIAL_STORE=file BASSIA_EXE=/app/Bassia
ENTRYPOINT ["/usr/local/bin/bassia-worker"]

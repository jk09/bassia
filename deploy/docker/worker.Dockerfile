# The agentic run worker: one container per run (an Azure Container Apps job execution) runs `bassia agent job`,
# which clones the server and runs the task with the coding agent (Claude Code by default; agent.command).
# Build from the repository root: docker build -f deploy/docker/worker.Dockerfile -t bassia-worker .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Bassia/Bassia.csproj -c Release -o /opt/bassia

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG AGENT_PACKAGE=@anthropic-ai/claude-code
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates nodejs npm \
    && npm install -g "$AGENT_PACKAGE" \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /opt/bassia /opt/bassia
RUN printf '#!/bin/sh\nexec dotnet /opt/bassia/Bassia.dll "$@"\n' > /usr/local/bin/bassia && chmod +x /usr/local/bin/bassia
# Jobs clone into a fresh folder; the agent API key and the server token come from the job's secrets.
ENV BASSIA_JOB_DIR=/work \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir /work && chown app /work
USER app
WORKDIR /work
ENTRYPOINT ["bassia"]
CMD ["agent", "job"]

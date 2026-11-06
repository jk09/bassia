#!/usr/bin/env bash
# Smoke test of the three images (built with tag $TAG, default "test"): a bassia-server on local disk with its state in
# a PostgreSQL container, Bassia Studio in front of it, and one run in the worker image - `bassia agent job` clones the
# server, runs a command and lands it.
set -euo pipefail
tag="${TAG:-test}"
server_port=8181
studio_port=8282
postgres_port=5433
cleanup() { docker rm -f bassia-smoke-server bassia-smoke-studio bassia-smoke-postgres >/dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup

docker run -d --name bassia-smoke-postgres -e POSTGRES_PASSWORD=smoke -p 127.0.0.1:$postgres_port:5432 postgres:16 >/dev/null
for _ in $(seq 1 60); do
  docker exec bassia-smoke-postgres pg_isready -U postgres -h 127.0.0.1 >/dev/null 2>&1 && break
  sleep 1
done
docker exec bassia-smoke-postgres pg_isready -U postgres -h 127.0.0.1

docker run -d --name bassia-smoke-server --network host -e BASSIA_SERVER_URL=http://127.0.0.1:$server_port \
  -e BASSIA_SERVER_TOKEN=smoke -e BASSIA_SERVER_DATA=/tmp/data \
  -e "BASSIA_SERVER_POSTGRES=Host=127.0.0.1;Port=$postgres_port;Username=postgres;Password=smoke;Database=postgres" "bassia-server:$tag" >/dev/null
docker run -d --name bassia-smoke-studio --network host -e BASSIA_STUDIO_SERVER=http://127.0.0.1:$server_port \
  -e BASSIA_STUDIO_TOKEN=smoke -e BASSIA_STUDIO_URL=http://127.0.0.1:$studio_port "bassia-studio:$tag" >/dev/null

for _ in $(seq 1 60); do
  curl -sf "http://127.0.0.1:$server_port/healthz" >/dev/null && curl -sf "http://127.0.0.1:$studio_port/healthz" >/dev/null && break
  sleep 1
done

docker run --rm --network host -e BASSIA_JOB_SERVER=http://127.0.0.1:$server_port -e BASSIA_JOB_TOKEN=smoke -e BASSIA_USER=smoke \
  -e 'BASSIA_JOB_ARGS=["agent","start","-run-id","run-smoke","-name","smoke","-land","-run","echo from the worker image > smoke.txt"]' \
  "bassia-worker:$tag"

log=$(curl -sf "http://127.0.0.1:$studio_port/log")
grep -q "run-smoke" <<<"$log" || { echo "Studio's mainline log does not show the run's landing" >&2; docker logs bassia-smoke-server >&2; docker logs bassia-smoke-studio >&2; exit 1; }
curl -sf "http://127.0.0.1:$studio_port/raw?path=smoke.txt" | grep -q "from the worker image"
echo "smoke test passed: the worker's run landed and Studio shows it"

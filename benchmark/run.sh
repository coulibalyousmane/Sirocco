#!/usr/bin/env bash
set -euo pipefail

# Orchestration bout en bout du benchmark comparatif (voir benchmark/README.md) : demarre la
# cible saturee, lance les 4 outils sequentiellement — jamais en parallele, pour isoler l'effet de
# chaque outil sur la meme cible plutot que de les faire concourir pour les memes 8 slots en meme
# temps — puis normalise les 4 sorties en benchmark/results/RESULTS.md.
#
# MSYS_NO_PATHCONV=1 devant les `docker run` qui passent un chemin cote conteneur en argument nu
# (le script k6, la simulation Gatling) : sous Git Bash sur Windows, la couche MSYS reecrit sinon
# silencieusement ces chemins en chemins hote (deja rencontre deux fois pendant ce chantier).
# A l'inverse, ne PAS l'utiliser pour `docker compose -f <chemin>` : ce chemin est cote hote, il a
# besoin de la traduction MSYS normale (sans quoi docker cherche "C:\c\Users\..." et echoue) —
# constat reel fait en executant ce script. Inoffensif sous Linux/macOS de toute facon.

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCHMARK_DIR="$REPO_ROOT/benchmark"
RESULTS_DIR="$BENCHMARK_DIR/results"
COMPOSE_FILE="$BENCHMARK_DIR/docker-compose.yml"
TARGET_URL="http://localhost:5281"

cleanup() {
  echo "--- Arret de la cible ---"
  docker compose -f "$COMPOSE_FILE" down
}
trap cleanup EXIT

echo "--- Demarrage de Tempest.SampleTarget (reglee pour saturer, voir docker-compose.yml) ---"
docker compose -f "$COMPOSE_FILE" up --build -d

echo "--- Attente de disponibilite ---"
ready=0
for _ in $(seq 1 30); do
  if curl -sf "$TARGET_URL/api/catalog/products" > /dev/null; then
    ready=1
    break
  fi
  sleep 1
done
if [ "$ready" -ne 1 ]; then
  echo "Tempest.SampleTarget n'a jamais repondu :"
  docker compose -f "$COMPOSE_FILE" logs
  exit 1
fi

mkdir -p "$RESULTS_DIR/gatling" "$RESULTS_DIR/nbomber"

echo "--- 1/4 Tempest ---"
dotnet run --project "$REPO_ROOT/src/Tempest.Cli" -c Release -- run \
  "$BENCHMARK_DIR/scenarios/tempest-checkout.yaml" \
  --target-url "$TARGET_URL" \
  --from-rps 20 --to-rps 150 --duration 90s \
  --report-json "$RESULTS_DIR/tempest.json"

echo "--- 2/4 k6 ---"
MSYS_NO_PATHCONV=1 docker run --rm \
  --add-host=host.docker.internal:host-gateway \
  -e TARGET_URL=http://host.docker.internal:5281 \
  -v "$BENCHMARK_DIR:/scripts" \
  -v "$RESULTS_DIR:/results" \
  grafana/k6 run /scripts/k6/checkout.js --summary-export=/results/k6.json

echo "--- 3/4 Gatling ---"
docker build -t benchmark-gatling "$BENCHMARK_DIR/gatling"
MSYS_NO_PATHCONV=1 docker run --rm \
  --add-host=host.docker.internal:host-gateway \
  -e TARGET_URL=http://host.docker.internal:5281 \
  -v "$BENCHMARK_DIR/gatling/CheckoutSimulation.java:/opt/gatling/src/test/java/CheckoutSimulation.java" \
  -v "$RESULTS_DIR/gatling:/opt/gatling/target/gatling" \
  benchmark-gatling -Dgatling.simulationClass=CheckoutSimulation \
  > "$RESULTS_DIR/gatling/console.log" 2>&1

echo "--- 4/4 NBomber ---"
TARGET_URL="$TARGET_URL" \
RESULTS_PATH="$RESULTS_DIR/nbomber.json" \
REPORT_FOLDER="$RESULTS_DIR/nbomber" \
  dotnet run --project "$BENCHMARK_DIR/nbomber" -c Release

echo "--- Normalisation ---"
dotnet run --project "$BENCHMARK_DIR/normalize" -c Release -- "$RESULTS_DIR"

echo "Termine : $RESULTS_DIR/RESULTS.md"

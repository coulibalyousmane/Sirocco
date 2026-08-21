#!/usr/bin/env bash
set -euo pipefail

# Orchestration bout en bout du benchmark comparatif (voir benchmark/README.md) : demarre la
# cible saturee, lance les 4 outils sequentiellement — jamais en parallele, pour isoler l'effet de
# chaque outil sur la meme cible plutot que de les faire concourir pour les memes 8 slots en meme
# temps — puis normalise les 4 sorties en benchmark/results/RESULTS.md.
#
# Sans aucune variable d'environnement, ce script reproduit exactement le tir publie dans
# results/RESULTS.md : tous les defauts ci-dessous SONT les valeurs qui etaient codees en dur avant
# le chantier de l'article sur la dette d'ordonnancement. Ne pas les changer ici — c'est
# benchmark/saturation.sh qui les surcharge pour son propre protocole, dans son propre dossier de
# resultats.
#
# MSYS_NO_PATHCONV=1 devant les `docker run` qui passent un chemin cote conteneur en argument nu
# (le script k6, la simulation Gatling) : sous Git Bash sur Windows, la couche MSYS reecrit sinon
# silencieusement ces chemins en chemins hote (deja rencontre deux fois pendant ce chantier).
# A l'inverse, ne PAS l'utiliser pour `docker compose -f <chemin>` : ce chemin est cote hote, il a
# besoin de la traduction MSYS normale (sans quoi docker cherche "C:\c\Users\..." et echoue) —
# constat reel fait en executant ce script. Inoffensif sous Linux/macOS de toute facon.

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCHMARK_DIR="$REPO_ROOT/benchmark"
COMPOSE_FILE="$BENCHMARK_DIR/docker-compose.yml"
TARGET_URL="http://localhost:5281"

# --- Profil de charge, identique pour les 4 outils (defauts = tir publie) ---
export START_RATE="${START_RATE:-20}"
export TARGET_RATE="${TARGET_RATE:-150}"
export DURATION_SECONDS="${DURATION_SECONDS:-90}"

# Plafond d'utilisateurs virtuels. Non defini par defaut : chaque outil garde le sien (512 pour
# Tempest, 60/200 pour k6), ce qui est bien le comportement du tir publie. Gatling (injectOpen) et
# NBomber (Simulation.Inject) n'ont pas de plafond en modele ouvert — asymetrie reelle, documentee
# dans leurs fichiers respectifs et dans l'article, pas contournee ici.
MAX_VUS="${MAX_VUS:-}"
PRE_ALLOCATED_VUS="${PRE_ALLOCATED_VUS:-}"

# Attente maximale de la cible avant de refuser une commande. 50 ms = elle DELESTE (503), le
# comportement du tir publie ; une grande valeur = elle MET EN FILE. Voir docker-compose.yml.
export QUEUE_WAIT_MS="${QUEUE_WAIT_MS:-50}"

# Dossier de sortie et mode de rendu, pour qu'un protocole different n'ecrase jamais l'artefact
# publie. NORMALIZE_MODE=skip : ne pas normaliser du tout — utile quand l'appelant enchaine
# plusieurs passes et ne veut qu'un seul rapport a la fin (voir benchmark/saturation.sh).
RESULTS_DIR="$BENCHMARK_DIR/${RESULTS_SUBDIR:-results}"
NORMALIZE_MODE="${NORMALIZE_MODE:-}"

# Outils a jouer. Les quatre par defaut : c'est le benchmark comparatif. Un sous-ensemble sert aux
# passes temoins (ex. TOOLS=tempest pour rejouer le meme profil contre une cible reglee autrement).
TOOLS="${TOOLS:-tempest k6 gatling nbomber}"

tool_enabled() {
  case " $TOOLS " in
    *" $1 "*) return 0 ;;
    *) return 1 ;;
  esac
}

DURATION_ARG="${DURATION_SECONDS}s"

cleanup() {
  echo "--- Arret de la cible ---"
  docker compose -f "$COMPOSE_FILE" down
}
trap cleanup EXIT

echo "--- Profil : ${START_RATE} -> ${TARGET_RATE} req/s sur ${DURATION_ARG}, attente cible ${QUEUE_WAIT_MS} ms ---"
echo "--- Resultats : $RESULTS_DIR ---"

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

if tool_enabled tempest; then
  echo "--- Tempest ---"
  tempest_args=(
    run "$BENCHMARK_DIR/scenarios/tempest-checkout.yaml"
    --target-url "$TARGET_URL"
    --from-rps "$START_RATE" --to-rps "$TARGET_RATE" --duration "$DURATION_ARG"
    --report-json "$RESULTS_DIR/tempest.json"
  )
  if [ -n "$MAX_VUS" ]; then
    tempest_args+=(--max-vus "$MAX_VUS")
  fi
  dotnet run --project "$REPO_ROOT/src/Tempest.Cli" -c Release -- "${tempest_args[@]}"
fi

if tool_enabled k6; then
  echo "--- k6 ---"
  MSYS_NO_PATHCONV=1 docker run --rm \
    --add-host=host.docker.internal:host-gateway \
    -e TARGET_URL=http://host.docker.internal:5281 \
    -e START_RATE -e TARGET_RATE -e DURATION="$DURATION_ARG" \
    -e PRE_ALLOCATED_VUS -e MAX_VUS \
    -v "$BENCHMARK_DIR:/scripts" \
    -v "$RESULTS_DIR:/results" \
    grafana/k6 run /scripts/k6/checkout.js --summary-export=/results/k6.json
fi

if tool_enabled gatling; then
  echo "--- Gatling ---"
  docker build -t benchmark-gatling "$BENCHMARK_DIR/gatling"
  MSYS_NO_PATHCONV=1 docker run --rm \
    --add-host=host.docker.internal:host-gateway \
    -e TARGET_URL=http://host.docker.internal:5281 \
    -e START_RATE -e TARGET_RATE -e DURATION_SECONDS \
    -v "$BENCHMARK_DIR/gatling/CheckoutSimulation.java:/opt/gatling/src/test/java/CheckoutSimulation.java" \
    -v "$RESULTS_DIR/gatling:/opt/gatling/target/gatling" \
    benchmark-gatling -Dgatling.simulationClass=CheckoutSimulation \
    > "$RESULTS_DIR/gatling/console.log" 2>&1
fi

if tool_enabled nbomber; then
  echo "--- NBomber ---"
  TARGET_URL="$TARGET_URL" \
  RESULTS_PATH="$RESULTS_DIR/nbomber.json" \
  REPORT_FOLDER="$RESULTS_DIR/nbomber" \
    dotnet run --project "$BENCHMARK_DIR/nbomber" -c Release
fi

if [ "$NORMALIZE_MODE" = "skip" ]; then
  echo "--- Normalisation deleguee a l'appelant ---"
elif [ -n "$NORMALIZE_MODE" ]; then
  echo "--- Normalisation ---"
  dotnet run --project "$BENCHMARK_DIR/normalize" -c Release -- "$RESULTS_DIR" "$NORMALIZE_MODE"
else
  echo "--- Normalisation ---"
  dotnet run --project "$BENCHMARK_DIR/normalize" -c Release -- "$RESULTS_DIR"
fi

echo "Termine : $RESULTS_DIR"

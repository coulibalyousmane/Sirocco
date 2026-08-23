#!/usr/bin/env bash
set -euo pipefail

# Experience de l'article sur la dette d'ordonnancement (docs/articles/dette-ordonnancement.md).
#
# Meme cible, meme scenario, memes 4 outils et meme orchestration que le benchmark comparatif
# publie : ce script ne fait que surcharger les parametres de benchmark/run.sh, il ne duplique
# aucune logique. Deux passes, qui ne different que par UNE variable :
#
#   1. QUEUE_WAIT_MS=120000 — la cible MET EN FILE. Les 4 outils.
#   2. QUEUE_WAIT_MS=50     — la cible DELESTE. Temoin, Sirocco seul.
#
# Avec 50 ms, passe 8 commandes simultanees, la cible rend un 503 en 50 ms : les utilisateurs
# virtuels sont liberes immediatement, aucun injecteur ne prend de retard, et la dette
# d'ordonnancement reste de l'ordre de 20 ms. C'est pour ca que le benchmark publie ne montre
# presque rien — il mesure un systeme qui va bien.
#
# Avec 120 s d'attente maximale (plus long que tout ce que ce tir peut produire), la cible ne refuse
# jamais : elle fait attendre. C'est le comportement de la majorite des systemes reels, et c'est la
# que la dette apparait.
#
# La passe temoin ne joue que Sirocco : la dette d'ordonnancement est une grandeur que lui seul
# publie, donc rejouer k6/Gatling/NBomber contre la cible delesteuse n'ajouterait aucune
# information a la comparaison — c'est deja ce que fait le benchmark publie.
#
# Les autres reglages servent la lisibilite de la demonstration, pas le resultat :
#   - Debit CONSTANT (START_RATE == TARGET_RATE) au-dessus de la capacite de la cible (~70 req/s,
#     soit 8 places / ~115 ms) plutot qu'une rampe : la file croit alors lineairement, une seule
#     pente a expliquer.
#   - Plafond d'utilisateurs virtuels identique pour les deux outils qui en ont un (Sirocco
#     --max-vus, k6 maxVUs). Gatling (injectOpen) et NBomber (Simulation.Inject) n'en ont aucun en
#     modele ouvert : asymetrie reelle, exploitee et documentee par l'article plutot que masquee.

BENCHMARK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESULTS_DIR="$BENCHMARK_DIR/results-saturation"

# Le protocole est defini ici et nulle part ailleurs : le rendu du rapport recoit ces memes valeurs
# en arguments, il ne peut donc pas decrire un tir different de celui qui a tourne.
RATE=100
DURATION=60
VUS=50

PROFILE_ENV=(
  START_RATE="$RATE"
  TARGET_RATE="$RATE"
  DURATION_SECONDS="$DURATION"
  MAX_VUS="$VUS"
  PRE_ALLOCATED_VUS="$VUS"
  NORMALIZE_MODE=skip
)

echo "=== 1/2 — La cible MET EN FILE (QUEUE_WAIT_MS=120000), les 4 outils ==="
env "${PROFILE_ENV[@]}" \
  QUEUE_WAIT_MS=120000 \
  RESULTS_SUBDIR=results-saturation \
  "$BENCHMARK_DIR/run.sh"

echo
echo "=== 2/2 — Temoin : la cible DELESTE (QUEUE_WAIT_MS=50), Sirocco seul ==="
env "${PROFILE_ENV[@]}" \
  QUEUE_WAIT_MS=50 \
  TOOLS=sirocco \
  RESULTS_SUBDIR=results-saturation/temoin \
  "$BENCHMARK_DIR/run.sh"

echo
echo "=== Normalisation des deux passes ==="
dotnet run --project "$BENCHMARK_DIR/normalize" -c Release -- "$RESULTS_DIR" --saturation \
  --planned "$((RATE * DURATION))" --max-vus "$VUS"

echo "Termine : $RESULTS_DIR/SATURATION.md"

# Résultats du benchmark comparatif

Généré par `benchmark/normalize` à partir des sorties réelles de
`benchmark/results/{tempest.json,k6.json,gatling/console.log,nbomber.json}`.
Méthodologie complète, protocole exact et limites : voir [README](README.md).

## Vue d'ensemble — requêtes de checkout (le point de saturation)

| Outil | Requêtes | OK | Échecs | Taux d'échec |
|---|---:|---:|---:|---:|
| Tempest | 7650 | 5366 | 2284 | 29,9 % |
| k6 | 7649 | 5349 | 2300 | 30,1 % |
| Gatling | 7650 | 5125 | 2525 | 33,0 % |
| NBomber | 7524 | 4308 | 3216 | 42,7 % |

## Latence de bout en bout (itération complète : login + checkout)

Comparable seulement entre outils qui exposent un temps total par itération.
NBomber agrège ses statistiques par étape et par scénario (pool des échantillons de
chaque étape), pas en sommant les étapes d'une même itération — ce n'est donc pas la
même grandeur, d'où son absence ci-dessous plutôt qu'un chiffre trompeur.

| Outil | Métrique | p50 (ms) | p95 (ms) | p99 (ms) |
|---|---|---:|---:|---:|
| Tempest | Response (avec attente d'ordonnancement) | 246,8 | 317,4 | 337,9 |
| Tempest | Service (p99 seul, traitement pur) | — | — | 333,8 |
| k6 | iteration_duration | 247,6 | 317,6 | 342,2 |
| Gatling | Global Information (colonne Total) | 133,0 | 280,0 | 573,0 |
| NBomber | — (voir note ci-dessus) | — | — | — |

## Latence de l'étape checkout seule

Tempest et NBomber exposent un percentile par étape nommée. k6 (sans tags/groupes
par requête dans `benchmark/k6/checkout.js`) et Gatling (dont la console ne détaille
les percentiles que globalement, pas par nom de requête) ne le permettent pas avec les
artefacts capturés ici — limite réelle documentée plutôt que contournée.

| Outil | p50 (ms) | p95 (ms) | p99 (ms) |
|---|---:|---:|---:|
| Tempest | 128,0 | 185,3 | 196,6 |
| k6 | — | — | — |
| Gatling | — | — | — |
| NBomber | 144,1 | 191,6 | 201,0 |

## Le différenciateur Tempest : Response vs Service, et la dette d'ordonnancement

Aucun des trois autres outils ne publie cette distinction. Sur l'itération complète :

- **Response p99** (ce que l'appelant attend réellement, file d'attente incluse) : 337,9 ms
- **Service p99** (temps de traitement pur, une fois la requête prise en charge) : 333,8 ms
- **Dette d'ordonnancement maximale observée** : 19,1 ms

L'écart entre Response et Service sous charge est exactement le signal que k6, Gatling
et NBomber ne rendent jamais visible : le moment où les chiffres qu'ils annoncent ne
reflètent plus la réalité de la cible, sans qu'aucun indicateur ne le signale.


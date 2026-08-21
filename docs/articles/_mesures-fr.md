## Ce que chaque outil rapporte de ce tir

Débit demandé : **6 000 itérations** (débit constant au-dessus de la capacité de la cible). La cible ne refuse jamais : elle fait attendre.

| Outil | Modèle ouvert | Plafond VUs | Requêtes | Échecs | Latence rapportée (p99) | Attente d'ordonnancement |
|---|---|---:|---:|---:|---:|---|
| **Tempest** | borné | 50 | 6 000 | 0,0 % | **28 311,6 ms** | **mesurée** : dette max 27 795,8 ms |
| k6 | borné | 50 | 4 092 | 0,0 % | 1 162,6 ms | non ; `dropped_iterations` = 1 907 |
| Gatling | non borné | — | 4 827 | 0,0 % | 14 573,0 ms | non (aucune file interne) |
| NBomber | non borné | — | 5 967 | 0,2 % | 27 672,6 ms | non (aucune file interne) |

La colonne latence n'est pas la même grandeur partout, et c'est documenté plutôt que
lissé : `__iteration` Response pour Tempest, `iteration_duration` pour k6, bloc
`Global Information` pour Gatling — trois façons de dire « l'itération complète ». Pour
NBomber, c'est l'étape `checkout` seule : il n'agrège pas par itération.

## Charge réellement délivrée

Même grandeur pour les quatre : les requêtes `checkout` abouties, sur les
6 000 demandées.

| Outil | Délivrées | Manquantes | Ce que l'outil en dit |
|---|---:|---:|---|
| **Tempest** | 6 000 | 0 | `droppedCount` = 0 ; dette publiée séparément |
| k6 | 4 092 | 1 908 | `dropped_iterations` = 1 907 |
| Gatling | 4 827 | 1 173 | 1 173 × `j.n.NoRouteToHostException` ; 1 173 × `checkout: No attribute named 'token' is defined` |
| NBomber | 5 958 | 42 | `failCount` = 42 sur le scénario |

## Le même tir, les deux mesures de Tempest

| Mesure | p50 | p95 | p99 |
|---|---:|---:|---:|
| **Service** — la requête chronométrée depuis son envoi | 733,2 ms | 794,6 ms | 819,2 ms |
| **Response** — depuis l'instant où elle *devait* partir | 14 549,0 ms | 27 263,0 ms | **28 311,6 ms** |

- Écart au p99 : **27 492,4 ms**
- Dette d'ordonnancement maximale : **27 795,8 ms**
- Itérations mesurées : 6 000 sur 6 000 demandées, dont 0 abandonnées
- Taux d'échec : 0,0 %
- Durée réelle du tir : 88,6 s — l'injecteur a continué à vider son retard après la fin du profil

## Où la dette se loge

| Étape | Mesures | Response p99 | Service p99 | Dette max |
|---|---:|---:|---:|---:|
| `__iteration` | 6 000 | 28 311,6 ms | 819,2 ms | 27 795,8 ms |
| `login` | 6 000 | 27 656,2 ms | 157,7 ms | 27 795,8 ms |
| `checkout` | 6 000 | 688,1 ms | 688,1 ms | 0,0 ms |

## Témoin : le même profil contre une cible qui déleste

Tempest seul, exactement les mêmes paramètres. Une seule variable change : la cible
refuse au bout de 50 ms au lieu de faire attendre.

| Cible | Échecs | Service p99 | Response p99 | Écart p99 | Dette max |
|---|---:|---:|---:|---:|---:|
| Met en file | 0,0 % | 819,2 ms | 28 311,6 ms | 27 492,4 ms | 27 795,8 ms |
| Déleste (503) | 32,0 % | 385,0 ms | 421,9 ms | 36,9 ms | 188,4 ms |


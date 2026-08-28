# Mesure et rapport

## Métriques exposées

Meter `Sirocco` — les jauges lisent la fenêtre glissante, les compteurs le cumul :

| Instrument | Type | Étiquettes |
|---|---|---|
| `sirocco.latency` | jauge (ms) | `step`, `kind` (`response` \| `service`), `quantile` |
| `sirocco.requests` | compteur | `step`, `outcome` |
| `sirocco.bytes.received` | compteur | `step` |
| `sirocco.scheduling.delay.max` | jauge (ms) | `step` |
| `sirocco.metrics.dropped` | compteur | — |

`kind=response` est la latence corrigée du *coordinated omission*, `kind=service` la mesure brute.
Les superposer sur un même graphe montre le moment exact où l'injecteur ou la cible décroche.

```csharp
services.AddSiroccoMetrics();                       // agrégation + consommateur + instruments
services.AddSiroccoOpenTelemetry(builder => builder.AddOtlpExporter());
```


## Série temporelle

Un rapport `/report` classique ne dit que l'état final : le débit moyen sur tout le tir, jamais
le moment où les centiles ont décroché. `LoadTestReport.TimeSeries` ajoute la trajectoire — un
point relevé à intervalle régulier (`Sirocco:TimeSeriesIntervalSeconds`, 2 s par défaut), du
début à la fin du tir, chacun portant le débit, le nombre d'utilisateurs virtuels actifs, le
taux d'erreur, les centiles p50/p95/p99 et la dette d'ordonnancement maximale — tous relevés sur
la fenêtre glissante, exactement comme `/report/live`.

```
serie temporelle (fenetre glissante)
      t     it/s vus actifs   echecs       p50       p95       p99  dette max
   2,0s        0          3   0,0 %    0,00ms    0,00ms    0,00ms     0,00ms
   4,1s       12          7   0,0 % 1384,45ms 2080,77ms 2118,87ms  2043,32ms
   6,1s       38         10   0,0 %  626,69ms 1941,50ms 2118,87ms  2043,32ms
   8,1s       76         14   0,0 %  532,48ms 1736,70ms 2080,77ms  2043,32ms
  10,1s      121         18   0,0 %  434,18ms 1384,45ms 2023,42ms  2043,32ms
```

Ce relevé, pris pendant une montée de 2 à 20 utilisateurs virtuels sur 10 s, rend visible ce
qu'un seul état final aurait caché : l'effectif qui grimpe (colonne « vus actifs »), le débit qui
suit, et une dette d'ordonnancement qui apparaît dès que la cible commence à ne plus absorber la
charge — la même donnée que `/report/live`, mais gardée dans le temps plutôt qu'écrasée à chaque
sondage.

### Lire la colonne « dette max »

C'est un **maximum sur la fenêtre glissante**, pas depuis le début du tir. La distinction est ce qui
permet de séparer deux situations que la même valeur décrirait autrement :

- un **transitoire de démarrage** — la première itération de chaque utilisateur virtuel est souvent
  la plus longue (connexion, authentification, caches froids), et pendant ce temps la file de jetons
  se remplit. La dette monte, puis **redescend** dès que le pic quitte la fenêtre ;
- une **saturation en cours** — la dette monte et *reste* haute, fenêtre après fenêtre.

Exemple mesuré, modèle fermé à 4 utilisateurs virtuels sur 20 s, avec une étape `login` qui ne tourne
qu'une fois par utilisateur : la colonne affiche 363 ms pendant les dix premières secondes — le pic
est réellement dans la fenêtre — puis retombe à 105 ms, la dette du régime établi. Un p99 de 152 ms
et une durée tenue à 0,14 s près confirment que rien n'était saturé.

Deux conséquences pratiques :

- sur un tir **plus court que la fenêtre glissante**, rien ne sort jamais de la fenêtre : la colonne
  reste donc au maximum du tir, et c'est correct ;
- la ligne de bilan en fin de tir (`dette max` du résumé) est, elle, un maximum **cumulé** sur tout
  le tir, comme le rapport `/report` en portée cumulative et comme l'agrégat du mode distribué. Elle
  ne redescend jamais, par construction : c'est la série temporelle qui situe ce maximum dans le
  temps.

Techniquement, `TimeSeriesRecorder` (`Sirocco.Application.Metrics`) tourne en parallèle du moteur
plutôt qu'à l'intérieur de lui — `TargetRpsLoadEngine` ne détient aucune référence vers un
`MetricsAggregator`, il ne fait qu'écrire des mesures dans un puits — et relève à chaque
intervalle un `Snapshot(StatisticsScope.Sliding)` plus une nouvelle jauge,
`ActiveVirtualUserGauge` (`Sirocco.Application.Execution`), incrémentée/décrémentée par chaque
`VirtualUserWorker` à l'entrée et à la sortie de sa boucle de consommation — la seule façon
d'observer la concurrence *réelle* dans le temps, par opposition au plafond ou à l'effectif
configuré. `LoadTestHostedService` démarre ce relevé sur un jeton d'annulation distinct de celui
du tir : `stoppingToken` ne se déclenche qu'à l'arrêt de l'hôte, jamais à la seule fin naturelle
d'un tir à durée ou à itérations fixes, donc le relevé a besoin de son propre jeton, annulé dès
que le tir se termine, pour ne pas tourner indéfiniment après.

Un tir plus court que l'intervalle de relevé garde toujours au moins un point — le dernier
relevé est pris sans condition juste avant de rendre la main, jamais seulement à l'intérieur de
la boucle périodique.

Limite de cette version : non alimentée pour un tir à
[scénarios concurrents](../charge/scenarios-concurrents.md#scénarios-concurrents) (`MultiScenarioRunner` construit sa propre chaîne
de mesure sans enregistreur de série temporelle). Rendue en table *et* en graphe — voir
[Courbe de dette d'ordonnancement superposée](#courbe-de-dette-dordonnancement-superposée)
ci-dessous. Vérifié par un vrai tir (`--vus-from 2 --vus-to 20 --duration 10s`) contre
`Sirocco.SampleTarget` : effectif actif croissant fidèle à la rampe (3 → 7 → 10 → 14 → 18), débit
croissant en conséquence, dernier point pris après la fin du tir avec effectif retombé à 0.

### Distribution des temps de réponse

Un centile ment par omission : un p95 propre et une distribution bimodale (la moitié des requêtes
très rapides, l'autre très lentes) peuvent produire exactement le même chiffre. `LatencyHistogram`
détient déjà les paniers bruts qui feraient la différence — il les utilise pour calculer les
centiles — mais ne les exposait pas. `StepStatistics.ResponseHistogram` les publie, un histogramme
par étape, agrégés dans `LoadTestReport.ToHtml` sous une section « Distribution des temps de
réponse » : les 3 072 paniers de `LatencyHistogram` (bien trop fins pour un graphe lisible) sont
regroupés par octave — le découpage natif de l'histogramme lui-même — puis seules les octaves qui
couvrent réellement la distribution observée sont rendues en barres SVG, avec l'infobulle de
chaque barre donnant sa borne haute exacte et son nombre de mesures.

Limite : seul le temps de réponse corrigé (`Response`) est exposé, jamais le temps de service brut
(`Service`) — c'est la distribution à publier, voir la remarque de classe de `StepStatistics`.
Vérifié par un vrai tir bridé (`--rps 300 --max-rps 20`) contre `Sirocco.SampleTarget` : un
histogramme par étape dans le rapport HTML, cohérent avec les percentiles publiés à côté, et le
même histogramme brut (3 072 paniers) présent dans le rapport JSON.

### Courbe de dette d'ordonnancement superposée

Une table de centiles ne montre jamais *quand* un injecteur ou une cible décroche — seulement
l'état final. `LoadTestReport.ToHtml` superpose désormais, sur `LoadTestReport.TimeSeries`, un
graphe en ligne SVG : débit et dette d'ordonnancement maximale, chacun mis à l'échelle de son
propre maximum (leurs unités n'ont rien de comparable), sur le même axe des temps. Une dette qui
grimpe pendant que le débit stagne ou chute est la signature visuelle d'une saturation — invisible
dans un tableau de centiles, et le graphe qu'aucun outil qui ne corrige pas le *coordinated
omission* ne peut produire.

Pourquoi cette grandeur mérite son propre graphe, et ce qu'elle vaut face à ce que rapportent k6,
Gatling et NBomber sur le même tir : [Zéro erreur, et pourtant inutilisable — la dette
d'ordonnancement](../articles/dette-ordonnancement.md).

Rendu uniquement à partir de deux points de trajectoire ou plus (une seule mesure ne fait pas une
courbe) ; sans effet si `TimeSeries` est vide, même limite que la série temporelle elle-même.
Vérifié par un vrai tir volontairement bridé bien en deçà de la demande (`--rps 300 --max-rps 20`,
soit 15 fois le débit réellement transmis) : la courbe de débit plafonne immédiatement à 20 it/s
tandis que la dette d'ordonnancement grimpe de façon quasi linéaire, exactement le retard imposé
par le bridage — visible en un coup d'œil, là où le tableau juste en dessous demande de parcourir
des dizaines de lignes.

### Tableau de bord temps réel

`/report/live` existe depuis longtemps, mais reste du JSON brut — illisible pendant un tir en
cours sans outil pour l'interpréter. `/report/live.html` sert le même
`aggregator.Snapshot(StatisticsScope.Sliding)`, cette fois rendu par `LoadTestReport.ToHtml` (donc
avec la même distribution de latences et la même mise en page que le rapport final), et ajoute une
balise `<meta http-equiv="refresh">` qui recharge la page seule toutes les
`SiroccoHostOptions.LiveDashboardRefreshSeconds` (3 s par défaut) : ouvrir cette URL dans un
navigateur pendant le tir suffit, sans script ni extension.

Un rechargement de page entier plutôt qu'un flux `EventSource`/SSE — aucun des deux n'existait déjà
dans `Sirocco.Host`, et un tableau de bord d'opérateur n'a pas besoin d'une latence de mise à jour
inférieure à quelques secondes pour rester utile. Limite : comme `/report/live`, non alimenté pour
un tir à [scénarios concurrents](../charge/scenarios-concurrents.md#scénarios-concurrents). Vérifié par un vrai tir : `/report/live.html`
interrogé deux fois à quelques secondes d'écart pendant la montée en charge par défaut montre le
débit progresser (49 puis 99 itérations/s) avec la balise de rechargement présente à chaque fois,
tandis que `/report.html` (rapport cumulé, sans le paramètre d'auto-rechargement) ne la porte
jamais.


## Performances mesurées

256 utilisateurs virtuels, paliers de 3 s, 22 cœurs, ServerGC. La sonde exécute deux passes
pour que le prix de l'observabilité soit un chiffre et non une intuition :

| Cible | Moteur seul | Chaîne complète | Allocations (seul → complet) |
|---|---|---|---|
| 10 000 RPS | 9 971 | 9 996 | 115 → 205 o/itération |
| 50 000 RPS | 49 988 | 49 989 | 106 → 200 o/itération |
| 100 000 RPS | 99 974 | 99 986 | 105 → 197 o/itération |

Agréger deux distributions de latence et une fenêtre glissante coûte donc **~95 octets par
itération et zéro RPS** : le débit est identique avec et sans observabilité.

Le chemin de mesure lui-même n'alloue rien (`MetricResult` est *unmanaged*, un test le vérifie).
Les ~105 octets résiduels par itération viennent de l'`AsyncOperation` que le canal de jetons
alloue quand un utilisateur virtuel se met en attente — soit une allocation par itération, pas
par utilisateur. Les supprimer demanderait un tampon circulaire maison : pas encore justifié.


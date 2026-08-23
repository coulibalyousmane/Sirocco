# Benchmark comparatif — Sirocco, k6, Gatling, NBomber

Ce dossier démontre, sur une cible volontairement saturée, ce que
[ROADMAP.md](../ROADMAP.md#le-différenciateur-réel-nest-pas-celui-quon-croit) a établi comme le
vrai différenciateur de Sirocco : **pas** « nous évitons le biais de *coordinated omission* »
(faux — k6, Gatling et JMeter ont tous un modèle ouvert), mais **« nous sommes le seul outil qui
vous dit quand vos propres chiffres ne sont plus fiables »**. Sirocco est le seul des quatre à
publier à la fois `Response` (ce que l'appelant attend, file d'attente incluse) et `Service`
(le traitement pur une fois pris en charge) — l'écart entre les deux, sous saturation, est
exactement le signal que les trois autres ne montrent jamais.

Résultats du dernier tir : **[RESULTS.md](results/RESULTS.md)**.

## Le mécanisme

`Sirocco.SampleTarget` a un `ConcurrencyGate` sur `POST /api/checkout` (voir
[docker-compose.yml](docker-compose.yml)) réglé pour saturer à un débit modeste et reproductible
sur une seule machine :

- `MaxConcurrentCheckouts=8`
- `MinLatencyMilliseconds=80` / `MaxLatencyMilliseconds=150`
- `ErrorRate=0` (seule la saturation doit produire des échecs, signal propre)

Débit de saturation théorique ≈ 8 slots / ~0,115 s ≈ 70 req/s. Les quatre outils suivent le même
profil : une rampe de 20 à 150 req/s sur 90 s, qui traverse clairement ce seuil.

## Le scénario, identique dans les quatre outils

Un scénario dédié (pas le workflow `dynamic-checkout` intégré de Sirocco, qui fait plus d'étapes),
reproduit à l'identique dans chaque outil pour garantir une comparaison à séquence de requêtes
strictement égale :

1. `POST /api/auth/login` — `{"username":"demo","password":"demo"}` → extrait un jeton.
2. `POST /api/checkout` — `{"items":[{"productId":1,"quantity":2}]}`,
   `Authorization: Bearer <jeton>`.

Modèle ouvert (arrival-rate) pour les quatre : [scenarios/sirocco-checkout.yaml](scenarios/sirocco-checkout.yaml),
[k6/checkout.js](k6/checkout.js), [gatling/CheckoutSimulation.java](gatling/CheckoutSimulation.java),
[nbomber/Program.cs](nbomber/Program.cs).

## Reproduire

```bash
./benchmark/run.sh
```

Démarre la cible réglée, lance les quatre outils **séquentiellement** (jamais en parallèle — pour
isoler l'effet de chaque outil sur la même cible plutôt que de les faire concourir pour les mêmes
8 emplacements en même temps), normalise les quatre sorties dans `results/RESULTS.md`, puis arrête
la cible. Nécessite Docker et le SDK .NET 10.

Sans aucune variable d'environnement, ce script reproduit exactement le tir publié : le profil, le
plafond d'utilisateurs virtuels et le réglage de la cible sont paramétrables, mais leurs **défauts
sont les valeurs du tir publié**. C'est ce qui permet au second protocole ci-dessous de réutiliser
la même orchestration sans la dupliquer.

## Le second protocole : saturer l'injecteur

```bash
./benchmark/saturation.sh
```

Le benchmark ci-dessus mesure une cible qui **déleste** (503 au bout de 50 ms), donc un système qui
se protège : l'injecteur n'y prend jamais de retard, et la dette d'ordonnancement observée reste de
l'ordre de 20 ms. `saturation.sh` change **une seule variable** — la cible **met en file** au lieu
de refuser — et c'est là que le phénomène apparaît, à plusieurs dizaines de secondes.

Deux passes (cible qui met en file avec les quatre outils, puis témoin sur la cible délesteuse avec
Sirocco seul), résultats dans `results-saturation/SATURATION.md`. Commenté en détail par
[l'article sur la dette d'ordonnancement](../docs/articles/dette-ordonnancement.md)
([English](../docs/articles/scheduling-debt.md)), dont les tableaux de mesures sont générés par la
même commande.

Prérequis réseau : télécharge l'image `grafana/k6`, le bundle Gatling OSS depuis Maven Central, et
les paquets NuGet NBomber.

## Ce que chaque outil expose réellement

Les quatre outils n'ont pas la même granularité de rapport. Documenté dans
[RESULTS.md](results/RESULTS.md) plutôt que masqué :

- **Latence par itération complète (login + checkout ensemble)** : Sirocco (`__iteration`), k6
  (`iteration_duration`) et Gatling (bloc `Global Information`, colonne `Total`) l'exposent tous
  les trois. **NBomber non** : ses statistiques de scénario agrègent les échantillons de latence
  de chaque étape entre elles (un pool), pas en sommant les étapes d'une même itération — ce
  n'est pas la même grandeur, donc ce chiffre n'est pas fabriqué par extrapolation.
- **Latence de l'étape checkout seule** : Sirocco et NBomber exposent un percentile par étape
  nommée. **k6 et Gatling non**, avec les artefacts capturés ici : le script k6
  ([k6/checkout.js](k6/checkout.js)) ne tague pas les requêtes par nom, et la console Gatling ne
  détaille les percentiles que globalement (le rapport HTML complet le permettrait, mais son
  `simulation.log` est un format binaire compact dans cette version — voir plus bas — et n'est pas
  parsé ici).

## Découvertes réelles faites en construisant ce benchmark

Documentées ici plutôt que contournées silencieusement, conformément à la discipline du dépôt
(vraie exécution, jamais de résultat fabriqué) :

- **Gatling OSS n'est plus un script autonome.** Depuis la 3.15.x, le bundle téléchargé depuis
  Maven Central est un squelette de projet Maven (`pom.xml` + `mvnw` + `src/test/java`), piloté
  par `gatling-maven-plugin`, sans plugin Scala configuré. [gatling/Dockerfile](gatling/Dockerfile)
  utilise donc `./mvnw gatling:test`, et [gatling/CheckoutSimulation.java](gatling/CheckoutSimulation.java)
  est écrit en DSL Java (pas Scala) pour compiler tel quel.
- **Pas de `stats.json` ni de log texte exploitable côté Gatling.** `simulation.log` est un format
  binaire compact dans cette version, et aucun JSON n'est produit à côté du rapport HTML. La
  sortie console de Gatling ("Global Information") est elle-même un tableau texte bien structuré
  et fiable — c'est elle qui est capturée dans `results/gatling/console.log` et parsée par
  `benchmark/normalize`.
- **Le résumé par défaut de k6 n'inclut pas p99.** Il a fallu ajouter explicitement
  `summaryTrendStats: ['avg','min','med','max','p(50)','p(95)','p(99)']` aux options du script
  pour l'obtenir dans `--summary-export`.
- **NBomber n'a pas de format de rapport JSON.** `WithReportFormats` ne propose que
  Csv/Html/Md/Txt à ce jour. [nbomber/Program.cs](nbomber/Program.cs) sérialise donc lui-même les
  statistiques retournées par `NBomberRunner.Run()` (`NodeStats`) vers `nbomber.json`.
- **Licence NBomber.** La version actuelle (6.x) est sous licence commerciale (« NBomber License
  Agreement v3.0 », effective 2025-09-01), payante pour un usage **organisationnel**. Elle contient
  une clause explicite d'usage gratuit qui couvre ce dépôt : *« NBomber is free for personal use,
  including personal or hobby projects, benchmarks, tutorials... »* Sirocco étant un projet
  personnel et ceci étant littéralement un benchmark, l'usage ici est couvert par cette clause —
  vérifié en lisant le fichier `LICENSE` réel du paquet NuGet, pas supposé depuis la documentation
  marketing.

## Limites honnêtes

- **Une seule machine, sans isolation.** Les quatre outils tournent sur le même poste, parfois à
  côté d'autres processus (autres conteneurs Docker, IDE...). Les percentiles varient d'un tir à
  l'autre pour cette raison — observé concrètement entre deux tirs réels de ce benchmark (p99
  Gatling passé de ~200 ms à ~570 ms d'un tir à l'autre, sans changement de code). Ce que ce
  benchmark démontre est le **mécanisme** (l'écart Response/Service sous saturation, et l'absence
  de cet écart chez les trois autres), pas un classement précis de débit entre outils.
- **Tirs séquentiels.** Les quatre outils ne se disputent jamais les mêmes emplacements de
  concurrence en même temps : chacun sature la cible seul. C'est volontaire (isoler l'effet de
  chaque outil), mais ce n'est donc pas non plus un test de contention partagée entre outils.
- **Réglages de saturation choisis pour la démonstration.** `MaxConcurrentCheckouts=8` et les
  latences simulées sont calibrés pour qu'un tir modeste sur un poste personnel traverse le seuil
  de saturation en 90 s — pas des valeurs de production.

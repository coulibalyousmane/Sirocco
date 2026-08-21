# Modèles de charge

## Modèle fermé

Tempest ne sait piloter qu'un débit cible (modèle *ouvert*) : « exactement N utilisateurs
simultanés » — le besoin le plus courant dans les outils historiques — n'avait pas d'équivalent.
`--vus <n>` couvre ce cas, à côté du modèle ouvert plutôt qu'à sa place :

```bash
tempest run --target-url http://localhost:5281 --vus 50 --duration 30s
```

Exactement 50 utilisateurs virtuels enchaînent les itérations sans aucune pause imposée, jusqu'à
expiration de la durée — le débit résultant dépend entièrement de la latence de la cible, à
l'opposé du modèle ouvert. `--vus` est mutuellement exclusif avec `--rps`/`--from-rps`/`--to-rps`
(un seul modèle par tir) et avec `--max-vus` (`--vus` fixe déjà l'effectif exact, ce n'est pas un
plafond) ; il exige `--duration`, faute de profil de débit dont dériver une durée de tir.

**La mise en garde n'est pas cosmétique.** En modèle fermé, chaque jeton porte l'instant de sa
propre émission plutôt qu'un instant planifié à l'avance : il n'existe rien à comparer, donc pas
de correction du *coordinated omission* — précisément le biais que le modèle ouvert existe pour
éviter (voir [Décisions structurantes](../projet/architecture.md#décisions-structurantes)). `LoadTestReport.ClosedModel`
porte ce fait jusque dans le rapport JSON, et `ToTable()`/`ToHtml()` l'affichent dans le même
emplacement que l'avertissement « mesures perdues » : un opérateur qui compare deux tirs sans
relire les options de la CLI doit encore voir que l'un des deux n'est pas comparable à l'autre.

Le nombre d'utilisateurs virtuels n'est pas un paramètre du nouvel ordonnanceur
(`ClosedModelScheduler`) : il vient du nombre de travailleurs déjà créés par le moteur
(`LoadTestOptions.MaxVirtualUsers`), qui borne également la concurrence en modèle ouvert.
`ClosedModelScheduler` se contente d'émettre en continu dans le canal borné existant ; c'est la
contre-pression du canal — un nouveau jeton n'est écrit que lorsqu'un utilisateur virtuel vient de
se libérer — qui fait émerger le modèle fermé, sans aucun mécanisme de synchronisation dédié.

Limite : mode distribué non pris en charge pour ce modèle — `WorkerCoordinator` reste câblé sur
`CoordinatedRateLimiter`/`LoadProfile` (modèle ouvert) seul, comme le format scripté l'était déjà
pour d'autres raisons. Vérifié par un vrai tir (`--vus 10 --duration 5s` contre
`Tempest.SampleTarget`) : effectif exact, avertissement présent dans le rapport texte et le JSON,
modèle ouvert inchangé en régression.

## Montée d'utilisateurs

Le modèle fermé ci-dessus fixe un effectif *constant*. Beaucoup de tirs réels veulent au contraire
observer une dégradation progressive — « monter à 50 utilisateurs sur 2 minutes » — sans jamais
viser un débit. `--vus-from`/`--vus-to` couvre ce cas :

```bash
tempest run --target-url http://localhost:5281 --vus-from 0 --vus-to 50 --duration 2m
```

L'effectif concurrent passe linéairement de 0 à 50 sur la durée donnée (une rampe descendante,
`--vus-from 50 --vus-to 0`, fonctionne symétriquement). Mêmes règles d'exclusion mutuelle que
`--vus` : incompatible avec `--rps`/`--from-rps`/`--to-rps` (modèle ouvert) et avec `--max-vus`
(l'effectif suit déjà les paliers, jusqu'à leur pic) ; `--duration` est obligatoire. Une rampe
« montée, plateau, descente » à plusieurs paliers reste possible via un `appsettings.json`, section
`Tempest:RampVus` — la CLI n'exprime qu'un seul palier, comme elle ne le fait déjà que pour un seul
palier de débit (`--from-rps`/`--to-rps`).

Techniquement, `RampingVirtualUserPool` (`Tempest.Application.Execution`) remplace la création
statique de travailleurs du moteur : il en crée de nouveaux quand l'effectif cible monte et en
arrête individuellement quand il descend — chaque travailleur reçoit son propre jeton d'annulation
plutôt que celui du tir, ce qui permet d'arrêter un utilisateur virtuel sans fermer la file de
jetons partagée par les autres. L'émission des jetons elle-même reste inchangée : un
`ClosedModelScheduler` configuré sur la durée totale du profil continue d'alimenter la file en
continu, exactement comme pour un effectif fixe. Même mise en garde de rapport que le modèle fermé
à effectif fixe (`LoadTestReport.ClosedModel`) : la montée d'utilisateurs n'a pas plus
d'échéancier théorique à comparer que l'effectif constant.

Limite : mode distribué non pris en charge, comme pour l'effectif fixe. Vérifié par un vrai tir
(`--vus-from 0 --vus-to 20 --duration 8s` contre `Tempest.SampleTarget`) : débit croissant au fil
de la rampe, avertissement présent dans le rapport texte et le JSON, modèles ouvert et fermé à
effectif fixe inchangés en régression.

## Itérations partagées et itérations par utilisateur

Ni le modèle fermé ni sa montée ne répondent à « fais tourner ce script 1 000 fois » ou « chaque
utilisateur en fait exactement 20, peu importe le temps que ça prend » — deux besoins pilotés par
un nombre d'itérations plutôt qu'une durée. Deux nouveaux exécuteurs couvrent ce cas.

**Itérations partagées** (`--iterations`) : un total dispute par au plus `--max-vus` utilisateurs
virtuels, premier arrivé premier servi — même convention de plafond que le modèle ouvert.

```bash
tempest run --target-url http://localhost:5281 --iterations 1000 --max-vus 20
```

**Itérations par utilisateur** (`--vus`/`--iterations-per-vu`) : chacun des `n` utilisateurs
virtuels fixés par `--vus` en exécute exactement `k`, indépendamment des autres — contrairement à
l'exécuteur partagé, un utilisateur virtuel rapide ne « vole » jamais les itérations d'un plus
lent. `--iterations-per-vu` prend la place de `--duration` comme condition d'arrêt de `--vus` :

```bash
tempest run --target-url http://localhost:5281 --vus 10 --iterations-per-vu 20
```

Aucun des deux n'a de notion de débit ni de durée : ni `--rps`/`--from-rps`/`--to-rps`, ni
`--vus-from`/`--vus-to`, ni `--duration` n'ont de sens ici (mutuellement exclusifs). Même mise en
garde de rapport que le modèle fermé (`LoadTestReport.ClosedModel`) : sans débit cible, il n'y a
pas d'échéancier théorique à comparer.

Techniquement, les deux réutilisent un seul nouvel ordonnanceur, `IterationCountScheduler`
(`Tempest.Application.Execution`) : il émet exactement un nombre fixe de jetons puis s'arrête,
plutôt que de s'arrêter sur une durée comme `ClosedModelScheduler`. La différence entre les deux
exécuteurs se joue entièrement côté travailleur : `VirtualUserWorker` accepte désormais un quota
personnel optionnel (`LoadTestOptions.IterationsPerVirtualUser`) au-delà duquel il s'arrête de
lui-même, sans jamais fermer la file partagée par les autres. Combiné à un
`IterationCountScheduler` dimensionné à effectif × quota, cet auto-arrêt garantit — par
construction, aucun travailleur ne peut en prendre plus que son quota et le total émis égale
exactement la somme des quotas — que chaque utilisateur virtuel en fait exactement sa part.
Itérations partagées n'utilise que l'ordonnanceur, sans quota individuel : la répartition inégale
au gré de qui répond le plus vite au canal partagé est le comportement voulu.

Limite : mode distribué non pris en charge pour les deux, comme pour le reste du modèle fermé.
Vérifié par de vrais tirs (`--iterations 300 --max-vus 20` puis `--vus 10 --iterations-per-vu 20`
contre `Tempest.SampleTarget`) : 300 puis 200 itérations exactement, avertissement présent dans le
rapport texte et le JSON, modèle ouvert inchangé en régression.


## Bridage

Tous les modèles ci-dessus décrivent *ce que le tir doit produire* — un débit cible, un effectif,
un nombre d'itérations. Aucun ne répond à « quoi que produise ce profil, ne dépasse jamais X
requêtes par seconde », utile pour respecter un quota côté cible ou reproduire un plafond
d'infrastructure réel. `--max-rps` couvre ce cas, en s'appliquant *par-dessus* le modèle choisi,
jamais à sa place :

```bash
tempest run --target-url http://localhost:5281 --rps 100 --duration 30s --max-rps 20
```

À la différence de tous les indicateurs précédents, `--max-rps` n'est mutuellement exclusif avec
rien : il compose avec `--rps`/`--from-rps`/`--to-rps` (modèle ouvert), `--vus`/`--vus-from`/
`--vus-to` (modèle fermé), `--iterations`/`--iterations-per-vu`, et avec `Tempest:Scenarios`, où il
sert de plafond par défaut pour tout scénario qui ne précise pas le sien (`MaxRequestsPerSecond`)
— même convention que `TargetBaseUrl`. Sans équivalent `--max-vus` distinct par scénario avant
cette fonctionnalité, un scénario concurrent peut désormais aussi porter son propre plafond,
indépendant de celui du tir entier.

Techniquement, `RateCappedScheduler` (`Tempest.Application.Execution`) est un décorateur
d'`ILoadScheduler` : il enveloppe le `ChannelWriter` remis à l'ordonnanceur choisi (modèle ouvert,
fermé, montée d'utilisateurs ou itérations) et retarde la transmission de chaque jeton jusqu'à ce
que l'intégrale du plafond l'autorise — même principe que `CoordinatedRateLimiter` (comparer prévu
et émis, jamais un délai par jeton, pour ne pas laisser la cadence dériver). Aucun des quatre
ordonnanceurs existants n'a besoin de savoir qu'il est bridé.

**Le retard ainsi imposé se mesure comme une dette d'ordonnancement ordinaire**, pas comme un cas
particulier à masquer : `ExecutionToken.ScheduledTicks` reste celui que l'ordonnanceur enveloppé
avait prévu, jamais réécrit par le décorateur, donc l'écart entre ce qui était prévu et l'instant où
la requête part réellement apparaît dans `Response` exactement comme un injecteur saturé — cohérent
avec le reste de Tempest, qui existe pour montrer ce genre d'écart, pas pour le cacher.

Vérifié par de vrais tirs contre `Tempest.SampleTarget` : `--rps 100 --duration 5s --max-rps 20`
a produit 500 itérations (le total planifié par le profil à 100 RPS) étalées sur 25s pour ne
jamais dépasser 20 RPS, avec une dette maximale d'environ 20s reflétant fidèlement le retard
imposé ; `--vus 10 --duration 5s --max-rps 15` (modèle fermé, qui produit naturellement 176 RPS
avec ces 10 utilisateurs virtuels contre cette cible) a été ramené à 15 RPS exactement ; et un tir
à deux scénarios concurrents a confirmé le plafond propre à un scénario (5 RPS) et le repli sur le
plafond global (8 RPS) pour celui qui n'en précise pas.


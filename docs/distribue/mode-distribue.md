# Mode distribué (Master/Workers)

Plusieurs `Tempest.Host` peuvent tirer en parallèle, coordonnés par un maître qui fusionne
leurs résultats en un seul rapport :

```json
"Tempest": { "Role": "master" },
"Master": { "ExpectedWorkers": 2, "RegistrationTimeoutSeconds": 30 }
```

```json
"Tempest": { "Role": "worker" },
"Worker": { "MasterUrl": "http://master:5299", "SelfUrl": "http://worker1:5300" }
```

**Auto-enregistrement dynamique** : chaque worker s'annonce au maître à son démarrage
(`POST /master/register`, avec plusieurs tentatives — rien ne garantit que le maître soit déjà
disponible). Le maître attend `ExpectedWorkers` enregistrements ou l'expiration de
`RegistrationTimeoutSeconds`, au premier des deux, puis distribue le tir aux workers déjà là.

**Fusion statistique correcte, pas une moyenne de centiles.** Chaque worker exporte l'état
**brut** de ses histogrammes de latence (les paniers eux-mêmes) plutôt que des centiles déjà
calculés — `ClusterReportAggregator` les fusionne panier par panier
(`LatencyHistogram.Add(HistogramSnapshot)`) et ne calcule les percentiles finaux qu'**une
seule fois**, sur le résultat fusionné. Combiner directement des centiles déjà calculés (par
exemple moyenner ou maximiser des p99 individuels) serait le piège classique du "centile de
centiles" — statistiquement faux quelle que soit la formule choisie, et inacceptable pour un
outil qui existe précisément pour corriger le *coordinated omission* ailleurs.

**Deux rapports combinés, deux garanties différentes.** `/report` sur le maître reste le
verdict **final et autoritatif** : construit une seule fois, à partir des rapports que chaque
worker pousse (`POST /master/report`) à la fin de son tir local — jamais recalculé après coup.
`/report/live` (nouveau) est rafraîchi en continu **pendant** le tir : le maître sonde
`GET /worker/report/raw` sur chaque worker toutes les `LivePollIntervalSeconds` (2 s par
défaut), fusionne les histogrammes bruts reçus — la même fusion exacte que pour le rapport
final, pas une approximation — et republie le résultat. Le seul compromis est temporel :
l'instant du sondage est approximatif, pas la fusion elle-même. Un worker temporairement
injoignable est ignoré pour ce cycle, pas fatal au tableau de bord.

**Un piège vérifié en pratique** : `/worker/prepare` et `/worker/start` sont deux appels
distincts, pas un seul. Le maître prépare tous les workers d'abord (construction du moteur,
scellement du registre d'étapes), puis les démarre tous en parallèle — rapprochant leurs
départs réels — plutôt que de laisser chaque worker démarrer dès l'arrivée de sa propre
requête HTTP, ce qui aurait cumulé un décalage d'un worker à l'autre.

Le profil de charge et le plafond d'utilisateurs virtuels sont divisés par le nombre de
workers effectivement enregistrés avant distribution — un tir à 1 000 RPS avec 4 workers
devient 250 RPS par worker.

**Propagation du scénario aux workers.** `/worker/prepare` transporte désormais tout ce qu'il
faut pour qu'un worker reconstruise exactement le même scénario que le maître aurait joué
seul : le **contenu** du fichier de scénario déclaratif (`ScenarioDefinitionLoader.ReadRaw`),
pas son chemin — un worker distant (conteneur ou machine séparée) n'a aucune raison de
partager le système de fichiers du maître — et les réglages de chaque scénario codé en dur
(`WebSocketEcho`, `GrpcEcho`, `DynamicCheckout`), lus par le maître depuis sa propre
configuration et transmis tels quels. Comble la limite documentée à l'étape 10 : les workers
construisaient jusqu'ici leur scénario avec des réglages par défaut, quoi que le maître ait
configuré.

**Un bug latent trouvé par cette vérification, pas supposé** : avant cette propagation, un tir
distribué en `grpc-echo` était cassé en pratique — chaque worker construisait
`GrpcEchoWorkflow()` sans options, donc `TargetUri` restait `null` et l'appel retombait sur
`HttpClient.BaseAddress` (le port HTTP classique de la cible), qui ne sait pas parler gRPC en
clair sans le port dédié (limite Kestrel déjà documentée à l'étape 8). Aucun test ne l'avait
révélé puisque personne n'avait encore tiré `grpc-echo` en mode distribué.

Vérifié par deux vrais tirs (1 maître, 2 workers, contre `Tempest.SampleTarget`) :
- Scénario déclaratif (`scenarios/smoke-test.yaml`, référencé uniquement par le maître) :
  154 itérations fusionnées, `login`/`browse`/`checkout` à **0 % d'échec** — jeton
  d'authentification extrait et propagé correctement d'une étape à l'autre sur les deux
  workers.
- `grpc-echo` avec `GrpcEcho:TargetUri` renseigné uniquement côté maître : 154 itérations
  fusionnées, **0 % d'échec** — confirme que le port gRPC dédié est bien atteint par les
  workers, là où le bug ci-dessus aurait échoué.

Vérifié par un vrai tir (1 maître, 2 workers, contre `Tempest.SampleTarget`) : `/report/live`
interrogé à mi-parcours affichait déjà 188 itérations combinées et cohérentes ; le rapport
final en comptait 274, **0 échec** sur toutes les étapes, seuils respectés — une progression
continue, pas un saut brutal entre "rien" et "tout" à la fin.

Autre vérification (1 maître, 2 workers, contre `Tempest.SampleTarget`) : les deux
workers se sont enregistrés, préparés, démarrés, ont tiré et remonté leur rapport, fusionné
en un total de 74 itérations, **0 échec** sur toutes les étapes, seuils respectés — le rapport
combiné se lit comme s'il venait d'un seul processus.

**Authentification du control plane.** `/master/register`, `/master/report`, `/worker/prepare`
et `/worker/start` — les quatre appels qui peuvent détourner un tir distribué (enregistrer un
faux worker, imposer un scénario, falsifier un rapport) — acceptent un secret partagé optionnel :

```json
"Tempest": { "ClusterSharedSecret": "un-secret-partage" }
```

Exigé en `Authorization: Bearer <secret>` dès qu'il est configuré, comparé en temps constant
(`CryptographicOperations.FixedTimeEquals`) pour ne rien révéler par le temps de réponse.
`null` par défaut : ces endpoints restent ouverts tant que l'opérateur ne configure rien —
**délibérément inspiré, puis durci, par rapport à k6** : sa REST API locale (`localhost:6565`)
n'est jamais authentifiée, la documentation officielle recommandant de ne compter que sur le
périmètre réseau (ne pas la lier à `0.0.0.0`) ; son mode distribué via `k6-operator` ne porte
pas non plus de jeton entre l'opérateur et les pods, la sécurité y reposant sur l'isolation
Kubernetes. Le seul jeton de l'écosystème k6 est celui de l'API k6 Cloud (SaaS Grafana) — un
client s'authentifiant vers le service cloud, pas un mécanisme entre les composants d'un tir.
Tempest applique cette même idée de jeton Bearer directement entre maître et workers, ce que
k6 lui-même ne fait pas.

Vérifié par un vrai tir (1 maître, 2 workers, secret partagé configuré des deux côtés) :
154 itérations fusionnées, **0 échec**, code de sortie 0 — l'en-tête est bien envoyé et
accepté sur les quatre appels. Vérifié aussi dans l'autre sens : une requête directe sans
en-tête `Authorization` est rejetée en 401 en moins de 3 ms, sans jamais atteindre la logique
de préparation du worker.

**Prometheus en mode distribué.** L'export existait déjà en mode autonome ; il couvre
maintenant aussi maître et workers, chacun sur son `/metrics` habituel — pas de nouveau port,
pas de nouvelle configuration à activer.

- Chaque **worker** expose ses propres métriques locales, exactement comme le mode autonome :
  `TempestMeter` est construit à la main dans `WorkerCoordinator.Prepare()`, une fois le
  `MetricsAggregator` du tir connu (il n'existe pas avant, voir plus haut) — jusque-là,
  `/metrics` répond, mais sans aucune série `tempest_*`.
- Le **maître** expose une vue **agrégée** de tout le cluster, pas un simple proxy vers un
  worker : `TempestMeter` y est câblé sur `MasterCoordinator.Snapshot`, qui renvoie
  `FinalReport` une fois le tir terminé, `LiveReport` pendant qu'il tourne (le même rapport que
  `/report/live`), ou un rapport vide avant le premier sondage. Simplification assumée : le
  maître n'a pas de fenêtre glissante propre (il ne fait que fusionner des rapports déjà
  construits par les workers), donc `tempest_latency_milliseconds` y reflète le même rapport
  fusionné que les compteurs cumulés, sans distinction glissant/cumulé.
- `TempestMeter` a été découplé de `MetricsAggregator` pour rendre ça possible : son
  constructeur accepte maintenant n'importe quelle source de rapport
  (`Func<StatisticsScope, LoadTestReport>`), avec une surcharge pratique pour le cas courant
  (un agrégateur local). Le mode autonome n'a rien à changer, `MetricsAggregator.Snapshot` s'y
  passe telle quelle.

Vérifié par un vrai tir (1 maître, 2 workers) : `/metrics` sur un worker affiche ses propres
compteurs locaux (`tempest_requests_total`, etc.) ; `/metrics` sur le maître, interrogé en
plein tir, affiche les centiles et compteurs **fusionnés** des deux workers (176 itérations
combinées à l'instant du sondage) — la même donnée que `/report/live`, sous forme Prometheus.

## Reprise sur perte d'un worker

Jusqu'ici, l'enregistrement (`POST /master/register`) était le seul signal de vie qu'un worker
donnait au maître — une fois le tir lancé, plus rien. Un worker qui meurt en cours de route
(process tué, conteneur évincé, coupure réseau) laissait donc le maître attendre indéfiniment
un rapport qui ne viendrait jamais : `MasterCoordinator.WaitForReportsAsync` n'avait aucun
timeout, et son unique appelant le sollicitait avec `CancellationToken.None`.

**Heartbeat continu, pas un enregistrement ponctuel.** Chaque worker signale maintenant qu'il
est vivant en continu (`POST /master/heartbeat`, `WorkerLivenessHostedService`), toutes les
`Worker.HeartbeatIntervalSeconds` (5 s par défaut), jusqu'à l'arrêt du process — plus seulement
une fois, à l'enregistrement. Passé `Master.WorkerDeadAfterSeconds` (20 s par défaut, soit 4
heartbeats manqués) sans signal d'un worker dispatché n'ayant pas encore rapporté,
`MasterCoordinator.MarkDeadIfStale` le déclare perdu — ce qui suffit à faire avancer
`WaitForReportsAsync` (le compte "rentré" inclut désormais les workers déclarés morts, pas
seulement les rapports effectivement reçus) sans attendre le worker manquant. Un heartbeat
tardif annule un faux positif tant que l'attente n'a pas déjà rendu la main.

**Fusion partielle honnête, pas un rapport silencieusement incomplet.** `ClusterReportAggregator.Merge`
tolérait déjà un sous-ensemble de rapports (il ne lève que sur une liste vide) — c'est la
couche d'orchestration qui exigeait jusqu'ici que *tous* les workers dispatchés rapportent.
Le rapport final expose maintenant `LostWorkers` : la liste des workers dispatchés qui n'ont
pas rapporté, rendue en bandeau d'avertissement par `ToTable()`/`ToHtml()`, sur le même modèle
que l'avertissement déjà existant pour les mesures perdues. Si *tous* les workers dispatchés
sont perdus, le maître refuse de fusionner une liste vide (ce serait un rapport fabriqué à
partir de rien) et échoue proprement plutôt que de planter sur une exception non gérée.

**Filet de sécurité optionnel pour le cas résiduel non couvert par le heartbeat** : un worker
dont le *process* reste vivant (donc continue de heartbeat normalement) mais dont le *tir
local* est bloqué (deadlock dans un workflow, par exemple) n'est pas détecté par ce mécanisme.
`Master.ReportTimeoutSeconds` (`null` par défaut, donc désactivé) plafonne alors l'attente de
manière absolue, quel que soit l'état des heartbeats — à renseigner explicitement si ce risque
est réel pour le scénario exécuté. Limite assumée plutôt que résolue ici : le détecter
proprement demanderait que le heartbeat porte un signal de progression du tir, pas seulement
« le process répond ».

Vérifié par un vrai tir distribué (1 maître, 2 workers, process réels — pas de simulation,
`Master.WorkerDeadAfterSeconds` réduit à 6 s pour resserrer le test) : un des deux workers tué
(`kill -9`) en cours de tir est déclaré perdu ~6 s après son dernier heartbeat ; le rapport
final contient `lostWorkers: ["http://localhost:5301"]` et les statistiques réelles du seul
worker survivant (60 itérations, percentiles réels, ni vides ni fabriqués) — le maître ne reste
jamais bloqué. Un second tir témoin, sans tuer aucun worker, confirme l'absence de faux positif
(aucun worker déclaré perdu, rapport complet aux deux workers).

## TLS sur le control plane

`ClusterSharedSecret` protège l'**authentification** du control plane (`/master/register`,
`/master/report`, `/master/heartbeat`, `/worker/prepare`, `/worker/start`) mais rien ne
protégeait jusqu'ici sa **confidentialité** : un rapport de tir, un scénario propagé, ou le
secret partagé lui-même circulaient en clair sur le réseau.

**Un seul certificat auto-signé partagé par les trois rôles, épinglé par empreinte — pas une
PKI complète.** Même philosophie que le secret partagé : simple plutôt qu'une infrastructure
lourde. Le même certificat (paire clé publique/privée) est installé sur le maître et sur chaque
worker ; chacun le présente via Kestrel (configuration standard ASP.NET Core, aucun code
supplémentaire) et chacun valide le certificat présenté par ses pairs contre une seule
empreinte configurée — symétrique, une seule valeur de configuration, comme
`ClusterSharedSecret` :

```json
"Tempest": { "ClusterCertificateThumbprint": "9DAB455A2D1D91CA1D077E52AF6C46449E037242" }
```

Côté serveur, rien à coder : ASP.NET Core sert déjà du HTTPS par pure configuration —

```bash
ASPNETCORE_URLS=https://+:5299
Kestrel__Certificates__Default__Path=/chemin/vers/cluster.pfx
Kestrel__Certificates__Default__Password=...
```

Côté client, `ClusterCertificatePinning.CreateHandler` pose un
`ServerCertificateCustomValidationCallback` sur le client HTTP nommé partagé par
`WorkerLivenessHostedService`, `MasterOrchestrationHostedService` et
`WorkerCoordinator.SubmitReportAsync` — les trois seuls points d'appel HTTP entre maître et
workers — qui compare l'empreinte du certificat présenté à celle configurée
(`CryptographicOperations`-style, insensible à la casse et aux séparateurs `:`), plutôt que la
chaîne de confiance par défaut, inadaptée à un certificat auto-signé. `null` par défaut : la
validation standard du système s'applique alors, sans effet en HTTP, et reste utilisable si un
opérateur préfère un certificat signé par une vraie CA plutôt que le certificat partagé.

Générer un certificat de test (PowerShell) :

```powershell
$cert = New-SelfSignedCertificate -DnsName "tempest-cluster" -CertStoreLocation "Cert:\CurrentUser\My" -KeyExportPolicy Exportable
Export-PfxCertificate -Cert $cert -FilePath cluster.pfx -Password (ConvertTo-SecureString -String "..." -Force -AsPlainText)
$cert.Thumbprint
```

**Limites assumées, pas résolues ici** : un seul certificat partagé plutôt qu'une PKI avec un
certificat par nœud (une vraie infrastructure de certificats, via `cert-manager` par exemple,
trouvera naturellement sa place dans le chantier Kubernetes suivant) ; pas de révocation ; une
rotation du certificat exige de reconfigurer l'empreinte à la main sur les trois rôles.
`docker-compose.yml` reste volontairement en HTTP — monter un certificat partagé dans trois
conteneurs ajouterait de la complexité pour une démo locale sans enjeu de confidentialité réel ;
la configuration TLS ci-dessus s'ajoute à la main pour qui veut l'essayer.

**Un vrai bug trouvé par la vérification de bout en bout, pas supposé** : `WorkerCoordinator.SubmitReportAsync`
(`POST /master/report`) utilisait un client HTTP différent des deux autres points d'appel,
oublié lors du câblage initial — les appels d'enregistrement et de sondage passaient bien en
HTTPS avec l'épinglage, mais l'envoi du rapport final échouait en
`RemoteCertificateNameMismatch`. Trouvé en observant un tir réel qui ne se terminait jamais,
pas en relisant le code.

Vérifié par un vrai tir (1 maître, 2 workers, certificat auto-signé partagé, empreinte
configurée sur les trois rôles) : 224 itérations fusionnées, **0 % d'échec**, seuils respectés,
code de sortie 0 — enregistrement, heartbeat, préparation/départ, sondage live et rapport final
circulent tous en HTTPS avec l'empreinte validée. Contre-épreuve : un worker configuré avec une
empreinte volontairement fausse échoue la poignée de main TLS dès la première tentative
d'enregistrement (`AuthenticationException`, `SSL connection could not be established`),
preuve que l'épinglage est réellement appliqué, pas un no-op silencieux.

**Tir témoin sans TLS**, chiffré plutôt qu'affirmé : même topologie (1 maître, 2 workers en
process réels), même profil que [`docker-compose.yml`](https://github.com/coulibalyousmane/Tempest/blob/main/docker-compose.yml)
(rampe 0 → 10 → 0 req/s sur 30 s), et cette fois **aucun certificat ni empreinte configurés** —
`ClusterCertificateThumbprint` laissé à `null`, tout le control plane en clair. Résultat :
**224 itérations fusionnées** — le même compte que le tir HTTPS, le profil étant déterministe —
**0 % d'échec sur les quatre étapes**, p95 de 87,04 ms sur `__iteration`, les deux seuils
respectés et **code de sortie 0**. Les deux workers ont bien enregistré, battu le cœur, sondé en
direct et soumis leur rapport final.

Autrement dit, le chemin HTTP historique est intact : l'option laissée à `null` ne pose aucun
callback de validation et n'a donc réellement aucun effet, plutôt que d'être neutralisée par
chance.


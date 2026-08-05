# Tempest

> Une tempête de trafic, à la demande.

Moteur de test de charge haute performance, asynchrone et *cloud-native*, écrit en C# / .NET 10.
Conçu pour simuler des dizaines de milliers de requêtes par seconde depuis une seule machine,
avec une empreinte mémoire minimale et une mesure de latence honnête
(**correction du *coordinated omission***).

## Pourquoi

| Problème | Réponse de Tempest |
|---|---|
| Les outils naïfs sous-estiment la latence dès que la cible sature (*coordinated omission*) | Chaque mesure porte son instant de départ **théorique** ; les percentiles sont calculés depuis cette référence |
| Un injecteur saturé produit des résultats faux sans le dire | La dette d'ordonnancement est une métrique de premier ordre, exposée en continu |
| Les rapports arrivent après coup | Métriques temps réel via `System.Diagnostics.Metrics` → OpenTelemetry / Prometheus |
| Le GC ruine la précision sous charge | `MetricResult` est une structure *unmanaged* de 48 octets transitant par un `Channel<T>` sans verrou |

## Architecture

Clean Architecture, dépendances dirigées vers l'intérieur :

```
Tempest.Domain          ← aucune dépendance
   ↑          ↑
Tempest.Application   Tempest.Scenarios
   ↑
Tempest.Infrastructure
   ↑
Tempest.Host
```

| Projet | Rôle |
|---|---|
| `Tempest.Domain` | Contrats et objets-valeurs purs : `MetricResult`, `IWorkflow`, `LoadProfile`, `TempestClock` |
| `Tempest.Application` | Orchestration : `ILoadScheduler` / `CoordinatedRateLimiter`, `TargetRpsLoadEngine`, `VirtualUserWorker`, `VirtualUserContext` |
| `Tempest.Infrastructure` | `MetricsProcessor` (consommateur du channel), `TempestMeter`, câblage OpenTelemetry |
| `Tempest.Scenarios` | Parcours métier concrets (données Bogus, appels HTTP, assertions) |
| `Tempest.Host` | Point d'entrée ASP.NET Core, endpoints `/metrics`, `/report`, `/report/live` |
| `samples/Tempest.SampleTarget` | Cible HTTP de démonstration : latence simulée, capacité finie, jetons qui expirent |

### Flux d'exécution

1. `CoordinatedRateLimiter` déroule l'échéancier issu du `LoadProfile` et émet des jetons horodatés.
2. `TargetRpsLoadEngine` distribue ces jetons aux `VirtualUserWorker` disponibles.
3. Le `IWorkflow` exécute ses étapes via le `HttpClient` partagé et déclare leur issue.
4. Les `MetricResult` partent en non bloquant vers le `MetricsProcessor`, qui agrège et publie.

### Câblage par injection de dépendances

```csharp
services.AddHttpClient();
services.AddSingleton<IWorkflow, DynamicCheckoutWorkflow>();

services.AddTempestEngine(
    LoadProfile.Create()
        .RampTo(5_000, TimeSpan.FromSeconds(30))
        .Sustain(TimeSpan.FromMinutes(5))
        .Build(),
    new LoadTestOptions { MaxVirtualUsers = 1_024 });
```

Tous les enregistrements passent par `TryAdd` : déclarer son propre `ILoadScheduler` ou
`IMetricSink` avant l'appel suffit à le substituer, sans renoncer au reste du câblage.

## Conventions de code

Le [`.editorconfig`](.editorconfig) à la racine fait autorité et est appliqué à la compilation
(`EnforceCodeStyleInBuild` + `TreatWarningsAsErrors`) :

- constantes en `UPPER_CASE`, champs non publics en `_camelCase` — en `severity = warning`,
  donc **bloquants au build** ;
- types explicites lorsque le type est apparent, `var` seulement quand il ne l'est pas ;
- constructeurs primaires, `namespace` file-scoped, accolades Allman, UTF-8 BOM sans
  newline finale ;
- aucune valeur magique : codes de statut, seuils et capacités sont des constantes nommées.

```bash
dotnet format Tempest.sln --verify-no-changes --severity info
```

> Le fichier configure aussi des règles SonarAnalyzer (`S101`, `S3358`, `S1135`). Le paquet
> `SonarAnalyzer.CSharp` n'est pas référencé : ces lignes sont inertes tant qu'il ne l'est pas.
> Dis-le si tu veux l'ajouter — avec `TreatWarningsAsErrors`, prévoir une passe de nettoyage.

## Décisions structurantes

- **Temps en ticks `Stopwatch`, jamais en `DateTime`.** Horloge monotone, aucune conversion sur le chemin critique.
- **Échéancier par intégrale, pas par délai.** Le régulateur compare le nombre de requêtes *dues*
  (`LoadProfile.PlannedRequestsUpTo`) au nombre *émises*. La dérive ne s'accumule pas et le retard
  se mesure exactement.
- **Pas de `string` dans `MetricResult`.** Les noms d'étapes sont résolus une fois au démarrage via
  `StepRegistry` ; seul un `StepId` (int) circule. Un test vérifie que la structure reste *unmanaged*.
- **`StepScope` n'est pas `IDisposable`.** Un `using` oublié enregistrerait un faux succès :
  l'issue d'une étape doit être déclarée explicitement.

- **Un thread dédié pour l'horloge.** Le régulateur ne tourne pas sur le `ThreadPool` : sous
  charge, celui-ci peut faire attendre une tâche prête pendant des centaines de millisecondes,
  ce qu'une horloge de tir ne survit pas.
- **`ReadAsync`, jamais `WaitToReadAsync`, côté consommateurs.** Ce dernier réveille *tous* les
  lecteurs en attente à chaque jeton écrit ; les N−1 perdants se réinscrivent aussitôt. Coût
  mesuré : 12 407 o/itération avec 256 utilisateurs virtuels, contre 105 après correction.
- **Le moteur ne dépend que de `ILoadScheduler`.** Il ne sait pas d'où vient la cadence : un
  profil aujourd'hui, un maître distant ou un rejeu de trafic enregistré demain. C'est aussi ce
  qui permet de tester sa mécanique avec une cadence déterministe, sans horloge ni test instable.
- **`LoadTestOptions` ne contient pas le profil de charge.** *Comment* l'injecteur se comporte et
  *quoi* tirer changent pour des raisons différentes : un même réglage d'injecteur sert tous les
  profils, et inversement.
- **Deux périmètres statistiques, une seule structure.** Cumulé pour le verdict CI, glissant pour
  le temps réel. L'histogramme sait fusionner, donc une fenêtre n'est qu'une somme de paniers
  temporels : le surcoût se limite à un second incrément de tableau par mesure.
- **Un centile ne sous-estime jamais.** Les valeurs rapportées sont les bornes hautes des paniers,
  plafonnées au maximum réellement observé. Pour une vérification de SLO, se tromper par excès est
  la seule erreur acceptable.
- **Tempest n'a aucune dépendance à un exportateur.** Il alimente les instruments
  `System.Diagnostics.Metrics` de la BCL ; OpenTelemetry, Prometheus ou un simple `MeterListener`
  viennent les écouter.
- **Le panier de `DynamicCheckoutWorkflow` ne coordonne rien entre deux processus.** Il se
  construit à partir de la réponse *réelle* de l'étape `browse`, jamais d'un pool de produits
  pré-généré côté client. Deux projets indépendants (scénario, cible) qui s'accorderaient sur
  des identifiants à l'avance seraient fragiles au moindre changement de l'un des deux.
- **Un singleton enregistré mais jamais résolu ne se construit jamais.** `MetricsAggregator` et
  `TempestMeter` en ont chacun fait les frais lors du premier tir réel (détails ci-dessous) :
  un conteneur d'injection de dépendances ne garantit ni un ordre de construction entre
  singletons indépendants, ni qu'un service sans consommateur direct soit jamais instancié.

## Métriques exposées

Meter `Tempest` — les jauges lisent la fenêtre glissante, les compteurs le cumul :

| Instrument | Type | Étiquettes |
|---|---|---|
| `tempest.latency` | jauge (ms) | `step`, `kind` (`response` \| `service`), `quantile` |
| `tempest.requests` | compteur | `step`, `outcome` |
| `tempest.bytes.received` | compteur | `step` |
| `tempest.scheduling.delay.max` | jauge (ms) | `step` |
| `tempest.metrics.dropped` | compteur | — |

`kind=response` est la latence corrigée du *coordinated omission*, `kind=service` la mesure brute.
Les superposer sur un même graphe montre le moment exact où l'injecteur ou la cible décroche.

```csharp
services.AddTempestMetrics();                       // agrégation + consommateur + instruments
services.AddTempestOpenTelemetry(builder => builder.AddOtlpExporter());
```

## Démarrage

```bash
dotnet test Tempest.sln -c Release
```

Étalonnage de l'injecteur (scénario à vide, ni réseau ni agrégation) :

```bash
dotnet run --project tools/Tempest.Probe -c Release
```

Premier tir réel, contre la cible de démonstration — deux processus, deux terminaux :

```bash
dotnet run --project samples/Tempest.SampleTarget -c Release   # écoute sur :5299
dotnet run --project src/Tempest.Host -c Release                # tire, écoute sur :5280
```

Pendant et après le tir : `http://localhost:5280/report/live` (fenêtre glissante),
`.../report` (cumulé), `.../metrics` (Prometheus).

## Scénario de référence

`DynamicCheckoutWorkflow` (login → browse → checkout) illustre trois capacités du moteur en
même temps :

- **jeton mis en cache par utilisateur virtuel** — `login` n'est rejoué qu'à la première
  itération, ou après un 401 ;
- **corrélation minimale** — le panier de `checkout` référence les identifiants réellement
  renvoyés par `browse`, jamais un pool pré-généré côté client ;
- **JSON sans réflexion** — sérialisation générée à la compilation
  (`System.Text.Json.Serialization.JsonSerializerContext`) des deux côtés du contrat HTTP,
  politique `camelCase` déclarée explicitement (voir plus bas pourquoi).

`Tempest.SampleTarget` simule une vraie capacité finie (`SemaphoreSlim` bornée, 503 au-delà)
et des jetons qui expirent, pour que le scénario ait vraiment quelque chose à saturer et à
rafraîchir plutôt qu'un simple écho instantané.

## Ce que le premier tir réel a révélé

Trois défauts qu'aucun test unitaire — y compris les 128 déjà écrits à l'étape 3 — n'avait
pu voir, parce qu'aucun n'exerçait à la fois un vrai réseau et le câblage DI complet :

1. **JSON camelCase vs PascalCase.** ASP.NET Core sérialise en camelCase par défaut ; un
   `JsonSerializerContext` sans `[JsonSourceGenerationOptions]` explicite attend les noms de
   propriété exactement tels que déclarés (PascalCase). Sans accord explicite entre les deux
   contextes (client et serveur), un jeton de connexion serait arrivé au client sous un nom qui
   ne correspond à rien — `Token` resterait `null`, échec silencieux, sans exception. Corrigé en
   déclarant `PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase` explicitement des deux côtés.
2. **`MetricsAggregator` exigeait un `StepRegistry` déjà rempli à la construction.** Or le
   registre n'est peuplé qu'au démarrage du tir (`TargetRpsLoadEngine.RunAsync`), pas à la
   construction du moteur — et un conteneur DI ne garantit aucun ordre entre deux singletons
   indépendants. Corrigé en construisant les accumulateurs **paresseusement**, au premier
   enregistrement ou à la première lecture, plutôt qu'au constructeur.
3. **`TempestMeter` n'était résolu par personne.** Le `Meter` et ses instruments ne se créent
   qu'à la construction de `TempestMeter` — mais rien d'autre n'en dépend directement (il existe
   pour être observé de l'extérieur, pas pour être appelé). Résultat : Prometheus n'exposait que
   `target_info`, aucune métrique Tempest. Corrigé par un `IHostedService` dédié
   (`MeterActivationHostedService`) dont l'unique rôle est de forcer la résolution.

Les trois sont désormais couverts par des tests de régression qui reproduisent l'ordre exact
qui a échoué, sans dépendre de réseau.

Résultat du premier tir (100 utilisateurs virtuels, rampe 0→100→0 RPS sur 35 s, contre la
cible de démonstration) : 2 750 itérations, **0 échec, 0 abandon**, 100 connexions pour 100
utilisateurs virtuels (le jeton mis en cache fonctionne), dette d'ordonnancement maximale
14 ms. La queue de latence (P99 à 811 ms sur `__iteration`, contre P50 à 55 ms) vient du
démarrage à froid — JIT, première connexion TCP par utilisateur virtuel — pas d'une
saturation réelle ; c'est exactement le genre de détail qu'une moyenne aurait masqué.

## Seuils CI/CD

Un `ThresholdRule` transforme un rapport en verdict binaire : grandeur, comparaison, limite.
Volontairement absent de la liste des grandeurs disponibles : toute variante `Service*` (le
temps brut, non corrigé). Gater un pipeline sur le temps de service reviendrait à faire
confiance à la mesure même que Tempest existe pour corriger — un seuil ne peut porter que sur
la latence de réponse déjà corrigée, le taux d'erreur, la dette d'ordonnancement ou le nombre
de mesures.

```json
"Tempest": {
  "Thresholds": [
    { "StepName": "__iteration", "Metric": "ResponseP95Milliseconds", "Comparison": "LessThan", "Limit": 200 },
    { "StepName": "__iteration", "Metric": "ErrorRate", "Comparison": "LessThanOrEqual", "Limit": 0.01 }
  ],
  "ExitAfterRun": true
}
```

`ExitAfterRun` est **faux par défaut** : sans lui, l'hôte reste actif pour continuer à servir
`/metrics` comme avant, même si des seuils sont configurés. C'est le scénario CI (un script qui
attend un code de sortie) qui doit l'activer explicitement — jamais un changement de
comportement silencieux pour du code déjà en production. Une étape introuvable (nom mal
orthographié) est un **échec**, pas un succès par défaut : une règle mal configurée doit se voir
dans le verdict, pas disparaître dans un pipeline qui continue de passer au vert.

Vérifié par un vrai tir : seuil trop strict → code de sortie 1, seuil desserré → code de sortie
0, `ExitAfterRun` absent → l'hôte reste actif comme à l'étape précédente. `/thresholds` expose
le même verdict en JSON, à tout moment du tir.

## Configuration déclarative

Un scénario HTTP peut se décrire en YAML ou JSON plutôt qu'en C#, sans recompiler :

```yaml
name: smoke-test
steps:
  - name: login
    method: POST
    path: /api/auth/login
    body: '{"username":"demo","password":"demo"}'
  - name: browse
    method: GET
    path: /api/catalog/products
    expectedStatusCodes: [200]
```

```json
"Tempest": { "ScenarioFile": "scenarios/smoke-test.yaml" }
```

`ScenarioFile` absent (par défaut) : l'hôte utilise `DynamicCheckoutWorkflow`, comme avant —
même logique de non-régression que pour `ExitAfterRun`. Renseigné : il construit un
`DeclarativeWorkflow` à partir du fichier, qui reçoit le **même** traitement que n'importe
quel scénario codé en dur — métriques, seuils, `/metrics`, sans aucun cas particulier.

Sans `expectedStatusCodes`, l'heuristique usuelle s'applique (2xx = succès). Avec, la
correspondance est exacte : un 2xx absent de la liste devient un `AssertionFailed`, pas un
succès — le scénario a dit ce qu'il attendait, et un 200 inattendu ne le satisfait pas
silencieusement.

> YamlDotNet et `System.Text.Json` ne savent construire, par réflexion, ni
> `IReadOnlyList<T>` ni `IReadOnlyDictionary<K,V>` — découvert en écrivant les tests, pas en
> le supposant. `ScenarioDefinitionDto` (types concrets, mutables) isole ce compromis à la
> frontière de désérialisation ; `ScenarioDefinition` (Domain) reste un objet-valeur immuable.

### Corrélation dynamique (Regex/XPath)

Une étape peut extraire une valeur de sa réponse et la rendre disponible aux étapes
suivantes via `{{nom}}` — c'est ce qui comblait la limite décrite plus haut dans les
versions précédentes de ce document (un jeton d'authentification ne pouvait pas se propager
d'une étape à l'autre) :

```yaml
steps:
  - name: login
    method: POST
    path: /api/auth/login
    body: '{"username":"demo","password":"demo"}'
    extract:
      - variable: token
        regex: '"token":"([^"]+)"'
  - name: checkout
    method: POST
    path: /api/checkout
    headers:
      Authorization: "Bearer {{token}}"
    expectedStatusCodes: [200]
```

Exactement une expression par règle : `regex` (universelle, sur texte brut) ou `xpath` (pour
un corps XML) — pas de JSONPath, hors périmètre choisi ; une extraction Regex suffit sur un
corps JSON tant qu'aucune expression dédiée n'est nécessaire. Les deux syntaxes sont validées
au chargement du scénario, pas au premier appel : un motif Regex ou une expression XPath
mal formés échouent immédiatement, avant le premier tir.

Portée volontairement limitée à ce seul vocabulaire — pas de branchement, pas de boucle. Les
variables extraites sont **locales à une itération** : une étape qui référence `{{nom}}` sans
qu'aucune extraction précédente ne l'ait renseigné échoue en `AssertionFailed` *avant même
l'envoi de la requête* — une erreur de configuration du scénario, pas un échec de transport à
mesurer comme tel. De même, une extraction configurée mais manquée transforme un 2xx en
`AssertionFailed` : le scénario attendait une valeur que la réponse n'a pas fournie, ce n'est
pas un succès silencieux.

[`scenarios/smoke-test.yaml`](scenarios/smoke-test.yaml) démontre le cas réel : `login`
extrait le jeton effectivement émis par `Tempest.SampleTarget`, `checkout` le réutilise dans
son en-tête `Authorization` — vérifié par un vrai tir, `checkout` passe désormais à 0 %
d'échec (il retournait systématiquement 401 dans les versions précédentes de ce fichier,
faute de pouvoir propager un jeton réel).

## Protocole WebSocket

Un scénario peut ouvrir une connexion WebSocket exactement comme il ouvre une requête HTTP,
via le même `IVirtualUserContext` :

```csharp
WebSocketConnection connection = await context.ConnectWebSocketAsync(uri, configureOptions: null, cancellationToken);
await connection.SendTextAsync("ping", cancellationToken);
WebSocketMessage reply = await connection.ReceiveAsync(cancellationToken);
await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken);
```

Aucun ajout à `StepScope` : `Success()` / `Fail()` suffisaient déjà, le protocole n'a rien de
spécifique à mesurer que la mécanique existante ne couvre pas. Contrairement à `HttpClient`,
une `ClientWebSocket` ne se mutualise pas — `ConnectWebSocketAsync` en crée une nouvelle à
chaque appel ; un scénario qui veut garder une connexion ouverte entre deux itérations doit la
conserver lui-même dans `IVirtualUserContext.State`.

`WebSocketEchoWorkflow` (scénario de référence : connexion → aller-retour d'un message texte →
fermeture propre) s'active via :

```json
"Tempest": { "Workflow": "websocket-echo" }
```

`Workflow` est sans effet dès que `ScenarioFile` est renseigné, qui garde la priorité —
`DynamicCheckoutWorkflow` reste le comportement par défaut si ni l'un ni l'autre n'est précisé.
`Tempest.SampleTarget` expose la cible correspondante, un écho Kestrel pur sur `/ws/echo`.

**Poignée de main de fermeture : un piège vérifié en pratique avant d'écrire la moindre ligne
de production.** Une sonde jetable (`ClientWebSocket` + `HttpListener`, hors du dépôt) a permis
de confirmer l'interopérabilité *avant* de construire la fonctionnalité — et a aussi révélé le
piège à éviter : `WebSocket.CloseAsync` effectue une poignée de main **complète** (elle attend
la trame de fermeture du pair) ; si un seul côté ne participe pas à cet échange, l'appel reste
bloqué indéfiniment. `WebSocketConnection.CloseAsync` délègue tel quel à
`ClientWebSocket.CloseAsync`, mais `WebSocketEchoWorkflow` et `Tempest.SampleTarget` s'assurent
tous deux de répondre à une fermeture reçue — vérifié par un test qui échouerait par *timeout*,
pas par assertion, en cas de régression.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 10 s, contre
`Tempest.SampleTarget`) : 75 itérations, **0 échec, 0 abandon**, `ws-connect` et `ws-echo`
tous deux à 0 % d'échec, seuils respectés.

## Protocole gRPC — unaire

Portée délibérément minimale (choix explicite) : un appel unaire, un aller-retour.

```csharp
EchoService.EchoServiceClient client = new(channel);
PingResponse response = await client.PingAsync(new PingRequest { Message = "ping" }, cancellationToken: cancellationToken);
```

Aucune connexion à mesurer séparément, contrairement à WebSocket : l'établissement HTTP/2 est
transparent et mutualisé par le `GrpcChannel`, exactement comme le pool de `HttpClient`. Une
seule étape (`grpc-ping`) suffit donc. Le contrat (`protos/tempest_echo.proto`) est un fichier
unique référencé par `Tempest.Scenarios` (client), `Tempest.SampleTarget` (serveur) et
`Tempest.UnitTests` (serveur de test) : un désaccord de contrat échoue à la compilation, pas à
l'exécution — même discipline que pour la déclaration JSON `camelCase` de l'étape 4.

`GrpcEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-echo" }
```

**Un piège vérifié en pratique, pas supposé** : un vrai démarrage a révélé que Kestrel, en
clair (`http://`, sans TLS), ne multiplexe **pas** HTTP/1.1 et HTTP/2 sur un même port — sans
négociation ALPN (qui exige TLS), un point d'écoute mixte reste silencieusement en HTTP/1.1
seul, ce qui casserait gRPC sans le moindre message d'erreur explicite au niveau applicatif.
`Tempest.SampleTarget` expose donc gRPC sur un port dédié, HTTP/2 pur
(`SampleTargetOptions.GrpcPort`, 5287 par défaut), à côté du port REST/WebSocket habituel.
`GrpcEchoWorkflowOptions.TargetUri` renseigne cette adresse séparée ; omis, le canal est dérivé
de la `BaseAddress` du client HTTP — suffisant dès que la cible négocie les deux protocoles via
TLS, le cas courant en production.

Second piège, cette fois côté client : `SocketsHttpHandler` refuse par défaut de négocier
HTTP/2 en clair (h2c). `GrpcEchoWorkflow` active explicitement
`AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)`
avant d'ouvrir le canal — sans ce commutateur, l'appel échoue silencieusement plutôt que de
révéler la vraie cause.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 10 s, contre
`Tempest.SampleTarget`) : 75 itérations, **0 échec, 0 abandon**, `grpc-ping` à 0 % d'échec,
seuils respectés.

## Protocole gRPC — streaming serveur

Sur les trois modes de streaming gRPC, un seul est couvert par cette version : le **streaming
serveur** (un appel, un flux de messages reçus). Streaming client et bidirectionnel restent un
chantier séparé — chacun redéfinit différemment ce que "succès d'une étape" veut dire pour un
flux ouvert, et méritent une conception à part plutôt qu'une extension mécanique de celle-ci.

```csharp
AsyncServerStreamingCall<StreamEchoMessage> call = client.StreamEcho(new StreamEchoRequest { Message = "ping" }, cancellationToken: cancellationToken);
while (await call.ResponseStream.MoveNext(cancellationToken))
{
    StreamEchoMessage current = call.ResponseStream.Current;
}
```

**Chaque message reçu est mesuré comme sa propre étape** (`grpc-stream-message`), pas l'appel
entier : `GrpcStreamEchoWorkflow` ouvre un `StepScope` frais juste avant chaque
`MoveNext`, donc la latence rapportée est celle de l'attente entre deux messages — la mesure
naturelle pour une API de flux, distincte du temps total de l'appel. Aucune étape "connexion"
séparée, même raisonnement que pour l'appel unaire : l'établissement HTTP/2 reste transparent.

Le nombre de messages envoyés est décidé par le **serveur**
(`SampleTargetOptions.StreamMessageCount`, 5 par défaut), jamais par le client : un client
réaliste ne dicte pas le comportement d'un flux auquel il s'abonne, il lit jusqu'à ce que le
serveur cesse d'émettre (`MoveNext` renvoie `false`) — ce dernier appel n'est pas mesuré comme
un message, puisqu'il n'en est pas un.

`GrpcStreamEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-stream-echo" },
"GrpcEcho": { "TargetUri": "http://localhost:5287" }
```

Réutilise la même section `GrpcEcho` (donc la même `TargetUri`) que le scénario unaire : même
besoin, mêmes réglages, pas de raison d'en dupliquer une deuxième.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 10 s, contre
`Tempest.SampleTarget`) : 75 itérations × 5 messages = 375 mesures sur `grpc-stream-message`,
**0 échec** — un nombre qui correspond exactement à la configuration serveur, confirmant que
chaque message du flux est bien compté une fois, ni plus ni moins.

## Mode distribué (Master/Workers)

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

## Conteneurisation

Une seule image sert les trois rôles (autonome/maître/worker) — c'est `Tempest:Role`, pas
l'image, qui les distingue. `docker-compose.yml` démontre le mode distribué en conteneurs
réels, joints par le DNS interne de Docker (nom de service, pas adresse IP) :

```bash
docker compose up --build --exit-code-from master
```

Quatre services : `sampletarget` (la cible), `master` et deux `worker*`. Le maître s'arrête
seul une fois le tir terminé (`Tempest__ExitAfterRun=true`, code de sortie reflétant le
verdict des seuils — utilisable directement en CI) ; les workers restent actifs comme de
vrais services long-vivants, `docker compose down` pour tout arrêter.

**Un piège réel, pas supposé** : le premier démarrage conteneurisé a plané avec
`UriFormatException: Invalid URI: The hostname could not be parsed.` — `Tempest.SampleTarget`
extrayait son port principal via `new Uri(configuration["urls"])`, mais la convention
`ASPNETCORE_URLS=http://+:5281` (« toutes les interfaces », nécessaire en conteneur) utilise
`+` comme hôte, qui n'est pas une syntaxe d'hôte valide au sens strict de `System.Uri` — cette
même ligne fonctionnait en local uniquement parce que `--urls http://localhost:5281` n'a
jamais cette forme. Corrigé par une extraction manuelle du numéro de port, sans passer par
`Uri`.

Vérifié par un vrai `docker compose up` (4 conteneurs réels, résolution par nom de service
Docker) : même pipeline que la vérification manuelle — enregistrement, préparation, démarrage
synchronisé, tir local, remontée des rapports — fusionné en 224 itérations, **0 échec** sur
toutes les étapes, seuils respectés, code de sortie 0.

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

## État d'avancement

- [x] **Étape 1** — Squelette Clean Architecture + couche Domain (contrats, métriques, profils de charge)
- [x] **Étape 2** — `CoordinatedRateLimiter` + `TargetRpsLoadEngine` + `VirtualUserContext`
- [x] **Étape 3** — `LatencyHistogram` + agrégation cumulée *et* glissante + `MetricsProcessor` + instruments
- [x] **Étape 4** — `DynamicCheckoutWorkflow` + `Tempest.SampleTarget` + Host ASP.NET (`/metrics`, `/report`) + premier tir réel
- [x] **Étape 5** — `ThresholdRule`/`ThresholdReport` + `ExitAfterRun` + endpoint `/thresholds` (roadmap P2, avancée en premier : s'appuyait déjà sur le rapport de l'étape 3)
- [x] **Étape 6** — `ScenarioDefinition`/`DeclarativeWorkflow` (YAML/JSON) + `ScenarioFile` (roadmap P1, scope minimal : un scénario = une séquence de requêtes HTTP sans corrélation)
- [x] **Étape 7** — `IVirtualUserContext.ConnectWebSocketAsync` + `WebSocketConnection` + `WebSocketEchoWorkflow` + endpoint `/ws/echo` (roadmap P1, protocoles avancés : WebSocket fait, gRPC restant)
- [x] **Étape 8** — `GrpcEchoWorkflow` (unaire) + `protos/tempest_echo.proto` + port gRPC dédié sur `Tempest.SampleTarget` (roadmap P1, protocoles avancés : gRPC fait en scope minimal — unaire seul, streaming hors périmètre)
- [x] **Étape 9** — `ExtractionRule` (Regex/XPath) + substitution `{{nom}}` dans `DeclarativeWorkflow` (roadmap P3, comble la limite de l'étape 6 : un jeton d'authentification peut désormais se propager d'une étape à l'autre)
- [x] **Étape 10** — Mode distribué Master/Workers : auto-enregistrement dynamique, `ClusterReportAggregator` (fusion d'histogrammes bruts), agrégation en fin de tir (roadmap P2, scope minimal — pas de tableau de bord combiné en temps réel, pas de propagation des options de scénario avancées aux workers)
- [x] **Étape 11** — Conteneurisation : `Dockerfile` pour `Tempest.Host` et `Tempest.SampleTarget`, `docker-compose.yml` démontrant le mode distribué en conteneurs réels, joints par le DNS Docker
- [x] **Étape 12** — Tableau de bord distribué en temps réel : `GET /worker/report/raw` + sondage continu du maître (`MasterOrchestrationHostedService`) + `GET /report/live` combiné, comblant la limite de l'étape 10
- [x] **Étape 13** — `GrpcStreamEchoWorkflow` (streaming serveur) : chaque message reçu mesuré comme sa propre étape, complétant le protocole gRPC de l'étape 8 (roadmap P1, scope minimal — streaming client et bidirectionnel restent hors périmètre)
- [x] **Étape 14** — Propagation du scénario et des options aux workers : `ScenarioDefinitionLoader.ReadRaw` (contenu, pas chemin) + `WebSocketEcho`/`GrpcEcho`/`DynamicCheckout` transmis dans `WorkerPrepareRequest`, comblant la limite de l'étape 10 — a mis au jour un bug latent (`grpc-echo` en mode distribué joignait le mauvais port faute de propagation de `TargetUri`)

## Roadmap

Les trois priorités identifiées sont désormais faites, chacune dans un scope volontairement
minimal documenté à sa section :

| Priorité | Fonctionnalité |
|---|---|
| ~~P1~~ | ~~Protocoles avancés~~ : WebSockets, gRPC unaire et gRPC streaming serveur faits — streaming client et bidirectionnel resteraient un chantier séparé s'ils sont un jour repris |
| ~~P2~~ | ~~Mode distribué Master/Workers~~ fait (étape 10), tableau de bord combiné en temps réel fait (étape 12) |
| ~~P3~~ | ~~Corrélation avancée : extraction par Regex / XPath~~ fait (étape 9) |

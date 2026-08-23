# Étendre Sirocco

## Contrat de plugin

Premier chantier de la phase 6 : k6 n'a pas porté seul ses dizaines de protocoles, il a ouvert
`xk6` et laissé la communauté le faire. Porter seul SQL, Kafka, MQTT, AMQP et le reste serait un
puits sans fond — le modèle d'extension doit exister **avant** les protocoles qu'il doit
accueillir, pas après.

Le contrat lui-même n'est pas nouveau : `IWorkflow`/`IVirtualUserContext`/`StepScope`
(`Sirocco.Domain`) sont agnostiques du protocole depuis toujours — `DynamicCheckoutWorkflow` et
les scénarios scriptés en sont déjà la preuve. Ce qui manquait, c'est un moyen pour l'hôte de
**charger** un `IWorkflow` compilé dans une assembly indépendante de ce dépôt, sans
`ProjectReference` — `PluginWorkflowLoader` (`Sirocco.Scenarios`) comble ce trou :

```bash
sirocco run mon-plugin.dll --plugin-type MonNamespace.MonWorkflow --target-url http://localhost:5299 --rps 20 --duration 30s
```

`WorkflowFileLoader` reconnaît maintenant l'extension `.dll` en plus de `.yaml`/`.json`
(déclaratif) et `.csx`/`.cs` (scripté) : il charge l'assembly (`Assembly.LoadFrom`), résout le
type à instancier — `--plugin-type` s'il est renseigné (nom complet ou simple), sinon le seul
type public implémentant `IWorkflow` si l'assembly n'en expose qu'un — puis l'instancie via son
constructeur public sans paramètre. Même flag disponible par scénario en mode scénarios
concurrents (`ScenarioOptions.PluginWorkflowType`, section `Sirocco:Scenarios`).

Limites volontaires de cette première version :
- **Aucune configuration injectée** dans le type instancié — pas de section `appsettings.json`
  liée automatiquement, contrairement à `DynamicCheckoutWorkflowOptions` pour les scénarios
  intégrés. Un plugin gère son propre réglage (variable d'environnement, fichier dédié...), voir
  `samples/Sirocco.SamplePlugin`.
- **Pas de résolution NuGet** : le chemin donné à `sirocco run`/`--scenario-file` doit déjà
  exister sur le disque (assembly compilée localement ou publiée puis téléchargée à la main).
  Résoudre un plugin par identifiant de paquet reste un chantier séparé, plus grand (roadmap
  phase 6, bullet suivant).
- **Mode distribué non pris en charge**, même limite que pour un scénario scripté :
  `WorkerCoordinator` ne sait construire qu'un `DeclarativeWorkflow` à partir du contenu propagé
  aux workers.

`samples/Sirocco.SamplePlugin` est la preuve réelle du contrat, pas un exemple théorique : un
projet de bibliothèque .NET ordinaire, **jamais référencé par `Sirocco.Host`/`Sirocco.Cli`/
`Sirocco.Scenarios`**, dont la seule dépendance est `Sirocco.Domain` (`ProjectReference` ici
uniquement parce que le dépôt reste privé — un vrai plugin tiers utiliserait le paquet NuGet). Il
implémente un `IWorkflow` minimal qui appelle `IVirtualUserContext.HttpClient` exactement comme
`DynamicCheckoutWorkflow`. Vérifié par deux vrais tirs contre `Sirocco.SampleTarget`, l'assembly
compilée séparément puis chargée par son chemin : sélection automatique du seul type disponible,
puis sélection explicite via `--plugin-type` — les deux à 0 % d'échec.

### Résolution NuGet

Deuxième moitié du bullet « chargement dynamique/résolution NuGet » de la phase 6 : plutôt que
d'exiger un chemin de `.dll` déjà présent sur le disque, `--plugin-package` résout un plugin par
identifiant de paquet, comme n'importe quelle dépendance NuGet ordinaire :

```bash
sirocco run --plugin-package MonEntreprise.SiroccoPlugins.Sql --plugin-package-version 1.4.0 --plugin-source https://mon-flux-prive/index.json --target-url http://localhost:5299 --rps 20 --duration 30s
```

`NuGetPluginResolver` (`Sirocco.Scenarios`, client officiel `NuGet.Protocol`) interroge
`--plugin-source` (répétable, nuget.org seul si omis) dans l'ordre — la première source qui
connaît le paquet gagne —, télécharge le `.nupkg` dans un cache local persistant entre les tirs,
puis extrait la bibliothèque du groupe `lib/<tfm>` le plus proche de `net10.0` (`FrameworkReducer`,
le même algorithme que NuGet/MSBuild eux-mêmes) avant de la remettre à `PluginWorkflowLoader`,
exactement comme pour une `.dll` locale. Sans `--plugin-package-version`, la dernière version
stable est résolue — mais **une version explicite déjà en cache ne redéclenche aucun trafic
réseau**, une version publiée étant immuable, contrairement à « dernière version stable » qui doit
toujours revalider auprès de la source.

Limite assumée : aucune résolution de dépendances transitives du paquet — seule sa propre
bibliothèque est extraite. Un plugin qui dépend d'un paquet tiers au-delà de `Sirocco.Domain` doit
être publié en assembly fusionnée, ou accepter que le chargement de type échoue si une référence ne
se résout pas.

Vérifié par un vrai tir : `Sirocco.SamplePlugin` empaqueté via `dotnet pack` dans un dossier local
(un flux NuGet à part entière, celui d'un miroir d'entreprise hors ligne — pas une approximation de
nuget.org), résolu par `--plugin-package Sirocco.SamplePlugin --plugin-source <dossier>` contre
`Sirocco.SampleTarget` réellement démarré : 0 % d'échec, confirmé une deuxième fois avec
`--plugin-package-version` explicite pour vérifier le chemin de cache.

Pour refaire ce tir, il faut lever explicitement le verrou d'empaquetage :

```bash
dotnet pack samples/Sirocco.SamplePlugin -c Release -p:IsPackable=true -o ./local-feed
```

`Directory.Build.props` pose `IsPackable=false` par défaut et seuls les cinq paquets réellement
publiés le remettent à `true` — un envoi sur nuget.org étant définitif, mieux vaut oublier de
publier que squatter un identifiant pour toujours. Ce plugin est un exemple : il n'a rien à faire
sur nuget.org, mais il doit rester empaquetable à la demande, puisque c'est tout son objet.

## Protocoles de référence

Troisième bullet de la phase 6 : des extensions écrites contre le contrat de plugin, pour le
valider en conditions réelles plutôt que dans l'abstrait. `Sirocco.SamplePlugin` prouvait le
mécanisme de chargement ; ces extensions prouvent qu'un protocole *différent* de HTTP tient dans
le même contrat, sans rien changer au cœur.

### SQL

`extensions/Sirocco.Extensions.Sql` interroge une vraie base SQLite plutôt que
`IVirtualUserContext.HttpClient` — la roadmap avait explicitement écarté SQL des [jeux de
données](../scenarios/donnees-assertions.md#jeux-de-données) (phase 2) faute de scope pour un chantier séparé ; celui-ci le referme,
sous un angle différent (protocole de charge, pas source de paramètres).

```bash
dotnet publish extensions/Sirocco.Extensions.Sql -o publish/sql-plugin
SIROCCO_SQL_PLUGIN_CONNECTION_STRING="Data Source=/chemin/vers/ma-base.db" sirocco run publish/sql-plugin/Sirocco.Extensions.Sql.dll --target-url http://localhost:1 --rps 20 --duration 30s
```

SQLite plutôt qu'un serveur SQL (PostgreSQL, SQL Server...) : base embarquée, aucune
infrastructure supplémentaire à démarrer pour vérifier ce chantier de bout en bout — cohérent avec
la discipline de scope du reste du projet. `SetUpAsync` sème un nombre configurable de produits de
référence (graine fixe, comme `DynamicCheckoutWorkflow`) et active le mode WAL, indispensable dès
que plusieurs utilisateurs virtuels partagent le même fichier en même temps. Chaque itération
exécute deux étapes réelles : un `SELECT` paramétré et un `INSERT`, chacune chronométrée par
`StepScope` comme n'importe quelle étape HTTP.

**`--target-url` reste exigé par la CLI mais n'est d'aucun usage pour ce protocole** — même
convention que `grpc-echo`/`websocket-echo`, qui dérivent leur propre cible d'une configuration
séparée plutôt que du client HTTP partagé. La configuration du plugin (chemin de la base, nombre de
lignes de référence) passe par des variables d'environnement, pas par `appsettings.json` :
`PluginWorkflowLoader` n'injecte aucune configuration dans le type qu'il instancie, voir
[Contrat de plugin](#contrat-de-plugin).

**Trouvaille réelle, documentée plutôt que corrigée dans le cœur** : un plugin chargé par
`Assembly.LoadFrom` doit être **publié** (`dotnet publish`), pas seulement compilé —
`dotnet build` seul ne copie pas les dépendances NuGet transitives à côté de l'assembly, que
`PluginWorkflowLoader` ne résout alors plus (`Microsoft.Data.Sqlite` introuvable au chargement).
Publier suffit pour une dépendance gérée, mais pas pour sa bibliothèque **native** : `SQLitePCLRaw`
la cherche par défaut à côté de l'assembly *hôte* (`Sirocco.Cli`, qui ne l'a jamais référencée), pas
à côté du plugin. `SqlWorkflow` enregistre son propre `NativeLibrary.SetDllImportResolver` pour la
chercher à côté de lui-même — la solution est dans le plugin, pas dans le contrat : un protocole
tiers reste responsable de ses propres dépendances natives, exactement l'esprit de la phase 6
(« s'ajoute sans toucher au cœur »).

Vérifié par un vrai tir : plugin publié, exécuté contre une base SQLite fraîche — 30 itérations,
0 % d'échec sur les deux étapes (`SELECT`/`INSERT`), lignes effectivement persistées (vérifié aussi
par des tests unitaires qui interrogent directement le fichier après coup).

### SSE

`extensions/Sirocco.Extensions.Sse` valide le contrat sous un angle différent de SQL : plutôt
qu'un protocole *différent* de HTTP, un **usage** différent d'`IVirtualUserContext.HttpClient` — une
réponse en flux continu (`text/event-stream`) lue événement par événement au fil de l'eau, plutôt
que l'aller-retour requête/réponse unique de tout le reste du dépôt.

```bash
dotnet build extensions/Sirocco.Extensions.Sse
sirocco run extensions/Sirocco.Extensions.Sse/bin/Debug/net10.0/Sirocco.Extensions.Sse.dll --target-url http://localhost:5281 --rps 20 --duration 30s
```

Contrairement au plugin SQL, celui-ci **utilise** `--target-url` normalement : le client HTTP
partagé pointe déjà vers la cible, seul le chemin relatif (`SIROCCO_SSE_PLUGIN_PATH`, défaut
`/api/events/stream`) et le nombre d'événements attendu (`SIROCCO_SSE_PLUGIN_EVENT_COUNT`, défaut
20) se configurent par variable d'environnement, comme `Sirocco.SamplePlugin`. Chaque itération
exécute deux étapes réelles : `SSE connect` (ouverture de la réponse en tête seule via
`HttpCompletionOption.ResponseHeadersRead`, vérification du `Content-Type`) et `SSE receive events`
(lecture ligne à ligne jusqu'à la fin du flux, comptage des événements portant au moins une ligne
`data:`). Un flux qui ne se termine jamais est borné par un délai par itération
(`SIROCCO_SSE_PLUGIN_TIMEOUT_SECONDS`, défaut 10s) plutôt que de bloquer indéfiniment l'utilisateur
virtuel.

Conséquence directe de rester au-dessus de HTTP plutôt que d'un protocole distinct : contrairement
à SQL, cette extension ne dépend d'aucun paquet NuGet au-delà de `Sirocco.Domain` — `HttpClient` et
la lecture de flux viennent du BCL. Un simple `dotnet build` suffit, `dotnet publish` n'est pas
nécessaire ici, confirmant que l'exigence de publication découverte pour SQL tient à une dépendance
réellement externe, pas au contrat de plugin lui-même.

Vérifié par deux vrais tirs contre un nouvel point d'écoute de `Sirocco.SampleTarget`
(`GET /api/events/stream`, nombre d'événements piloté par la requête) : sélection par défaut sans
aucune variable d'environnement, puis avec `--plugin-type` explicite et un nombre d'événements/délai
personnalisés — les deux à 0 % d'échec sur `SSE connect` et `SSE receive events`.

### MQTT

`extensions/Sirocco.Extensions.Mqtt` revient à un protocole réellement différent de HTTP, comme
SQL, mais orienté publication/abonnement plutôt que requête/réponse : chaque itération s'abonne à
un sujet qui lui est propre (`{préfixe}/{utilisateur}/{itération}`), y publie un message, puis
attend sa propre réception — le round-trip complet jusqu'au courtier et retour, pas un simple
accusé de publication.

```bash
dotnet publish extensions/Sirocco.Extensions.Mqtt -o publish/mqtt-plugin
SIROCCO_MQTT_PLUGIN_PORT=1883 sirocco run publish/mqtt-plugin/Sirocco.Extensions.Mqtt.dll --target-url http://localhost:1 --rps 20 --duration 30s
```

Sujet propre à chaque itération plutôt que partagé : sans cela, un utilisateur virtuel pourrait
recevoir le message publié par un autre, rendant le round-trip mesuré non attribuable à la bonne
itération. Deux étapes réelles par itération : `MQTT connect` (ouverture de la connexion TCP et
poignée de main MQTT) et `MQTT publish/receive` (abonnement, publication, attente bornée par
`SIROCCO_MQTT_PLUGIN_TIMEOUT_SECONDS` de la réception du même message).

Client MQTTnet uniquement (`MQTTnet`, pas `MQTTnet.Server`) : aucun courtier n'est porté par ce
plugin, contrairement à `Sirocco.SampleTarget` qui en héberge un embarqué (`MQTTnet.Server`, sur un
port dédié distinct du port MQTT conventionnel 1883) pour que la vérification de bout en bout ne
dépende d'aucune infrastructure externe — même logique que SQLite pour le protocole de référence
SQL, transposée à un courtier plutôt qu'un serveur de base de données.

**Confirmation réelle plutôt que nouvelle trouvaille** : comme le plugin SQL, celui-ci doit être
**publié** (`dotnet publish`), pas seulement compilé — vrai même ici où la seule dépendance ajoutée
(`MQTTnet`) est entièrement gérée, sans composant natif. Un `dotnet build` seul charge le type sans
erreur (`PluginWorkflowLoader` résout la réflexion sans toucher à MQTTnet), mais `ExecuteAsync`
échoue dès le premier accès à un type MQTTnet, faute de trouver `MQTTnet.dll` à côté de l'assembly.
Confirmé en isolant le problème via un harnais direct (mêmes appels, sans passer par
`Assembly.LoadFrom`) avant de le reproduire puis de le corriger par publication — la limite tient
bien au chargement dynamique d'un plugin avec dépendance externe, pas à un aléa propre à SQLite.

Vérifié par deux vrais tirs contre le courtier MQTT embarqué de `Sirocco.SampleTarget` : sélection
par défaut, puis `--plugin-type` explicite avec un préfixe de sujet personnalisé — les deux à 0 %
d'échec sur `MQTT connect` et `MQTT publish/receive`.

### GraphQL

`extensions/Sirocco.Extensions.GraphQl` clôt les quatre protocoles de référence de la phase 6.
Comme SSE, il reste au-dessus de HTTP plutôt que d'en changer, mais valide un autre aspect du
contrat : un point d'entrée unique (toujours `POST {chemin}`, toujours le même) où le succès ou
l'échec se lit dans le corps JSON (champ `errors`), jamais dans le code de statut — qui reste 200
même quand une mutation échoue côté métier. Toute autre étape HTTP du dépôt
(`DynamicCheckoutWorkflow`, `Sirocco.SamplePlugin`...) utilise au contraire le code de statut comme
seul signal.

```bash
dotnet build extensions/Sirocco.Extensions.GraphQl
sirocco run extensions/Sirocco.Extensions.GraphQl/bin/Debug/net10.0/Sirocco.Extensions.GraphQl.dll --target-url http://localhost:5281 --rps 20 --duration 30s
```

Deux étapes réelles par itération, mêmes deux natures d'opération que SQL sous revêtement HTTP :
`GraphQL query` (liste le catalogue, vérifie qu'elle n'est jamais vide) et `GraphQL mutation`
(passe une commande pour un identifiant tiré dans `[1, SIROCCO_GRAPHQL_PLUGIN_PRODUCT_ID_MAX]`,
20 par défaut). Ni variables GraphQL ni alias : les valeurs sont inlinées dans la chaîne de requête,
la cible de référence n'en a pas besoin pour prouver le contrat.

`Sirocco.SampleTarget` héberge un schéma GraphQL réel (`GraphQL`, moteur GraphQL.NET — pas une
simulation par correspondance de chaîne) exposé à la main sur `POST /graphql`, dans le même esprit
que REST/WebSocket : `products` en lecture, `placeOrder` en écriture, qui échoue avec une entrée
`errors` plutôt qu'un code de statut différent de 200 pour un identifiant de produit inconnu —
exactement le comportement que ce protocole de référence existe pour vérifier. Aucune dépendance
NuGet au-delà de `Sirocco.Domain` côté plugin (`System.Text.Json` et `HttpClient` suffisent) : comme
SSE, un simple `dotnet build` suffit, sans avoir besoin de publier.

Vérifié par deux vrais tirs contre le vrai schéma GraphQL de `Sirocco.SampleTarget` : sélection par
défaut, puis `--plugin-type` explicite avec une plage d'identifiants réduite — les deux à 0 %
d'échec sur `GraphQL query` et `GraphQL mutation`.


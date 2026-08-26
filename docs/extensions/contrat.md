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
(déclaratif) et `.csx`/`.cs` (scripté) : il charge l'assembly (`PluginWorkflowLoader`, dans un
`AssemblyLoadContext` isolé — voir « Isolation et signature » plus bas), résout le
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

**Dépendances transitives.** Le graphe déclaré par le paquet est parcouru en largeur : chaque
paquet atteint est téléchargé dans le même cache, et ses assemblies `lib/<tfm>` sont extraites **à
plat, à côté de celle du plugin** — c'est de là que le contexte de chargement les résout. Sans ce
parcours, un plugin distribué par paquet n'obtenait que sa propre `.dll` et échouait dès qu'il
touchait une de ses dépendances, ce qui réservait la distribution par paquet aux extensions sans
aucune dépendance. Le parcours n'est mené qu'une fois par version : un témoin
`.sirocco-dependencies` écrit à côté du plugin court-circuite tout trafic réseau aux tirs suivants.

Trois limites assumées, énoncées plutôt que devinées :

- **Les paquets que l'hôte fournit déjà** (`Sirocco.Domain`, `Sirocco.Application`,
  `Sirocco.Infrastructure`, `Sirocco.Scenarios`, `Sirocco.Cli`) sont ignorés : les télécharger
  exigerait qu'ils soient publiés sur la source pour que le moindre plugin se résolve, et en charger
  une copie privée dédoublerait les types du contrat partagé. C'est une liste **exacte**, pas un
  préfixe `Sirocco.*` — une extension tierce nommée `Sirocco.Extensions.Quelquechose` est restaurée
  comme n'importe quelle autre dépendance.
- **Seuls les actifs `lib/<tfm>`** sont extraits, jamais `runtimes/<rid>/native` : une dépendance à
  bibliothèque native (SQLite, par exemple) reste à distribuer via `dotnet publish`, comme le
  [plugin SQL](#sql).
- **L'arbitrage de version est sommaire** : première occurrence gagnante dans le parcours, et la
  version retenue est la plus basse qui satisfait l'intervalle déclaré (la règle de NuGet pour une
  dépendance directe). Un vrai solveur ferait du « nearest wins » sur le graphe complet ; deux
  dépendances exigeant des versions incompatibles du même paquet ne sont pas signalées comme un
  conflit.

La signature (ci-dessous) est vérifiée sur les paquets de dépendance comme sur celui du plugin : une
dépendance est du code qui s'exécutera dans le même processus.

**Isolation et signature (SEC-7, AUDIT.md).** Chaque assembly de plugin (résolue via
`--plugin-package` ou chargée directement par chemin) se charge dans son propre
`AssemblyLoadContext` collectible plutôt que dans le contexte par défaut — les dépendances propres
d'un plugin ne se mélangent pas à celles de l'hôte ni d'un autre plugin chargé dans le même
processus. Les assemblies que l'hôte fournit déjà (la liste exacte ci-dessus, `Sirocco.Domain` en
tête) restent explicitement résolues depuis le contexte par défaut, sans quoi le cast vers
`IWorkflow` échouerait sur des types dédoublés.

Les dépendances du plugin sont cherchées d'abord via son `.deps.json` (`AssemblyDependencyResolver`,
ce que produit `dotnet publish`), puis à défaut par simple sondage de son répertoire — c'est ce
second chemin qui sert un plugin résolu par `--plugin-package`, dont le graphe est restauré à plat à
côté de lui sans `.deps.json`. Conséquence assumée : une dépendance présente dans ce répertoire
prime sur celle de l'hôte, y compris pour une bibliothèque que les deux partagent. C'est l'isolation
voulue, mais un plugin qui y déposerait une assembly de framework s'exposerait à un conflit de types
dès qu'elle traverserait la frontière.

Un paquet résolu par `--plugin-package` doit en outre être signé, sans quoi la résolution échoue :
`NuGetPluginResolver` vérifie la présence d'une signature et que le contenu du paquet correspond
toujours à ce qui a été signé (`ISignedPackageReader.ValidateIntegrityAsync`). Cette vérification
ne prouve **pas** que le certificat signataire est de confiance, valide ou non révoqué — une
tentative de vérification de chaîne complète a été testée contre un vrai paquet nuget.org
légitimement signé (`Newtonsoft.Json`) et l'a rejeté à cause de l'expiration du certificat de
signature depuis, un cas que nuget.org authentifie via un contre-scellé RFC3161 que `NuGet.Packaging`
seul ne rejoue pas — implémenter cette partie à la main aurait été un faux sentiment de sécurité
plutôt qu'une correction réelle. Le typosquatting (un paquet valablement signé par son propre
auteur sous un nom proche) n'est donc pas détecté, voir `SECURITY.md`. Une source privée qui ne
signe pas ses paquets doit lever explicitement ce refus : `--plugin-allow-unsigned` en CLI,
`AllowUnsignedPlugins`/`Sirocco:AllowUnsignedPlugins` en configuration.

Vérifié par un vrai tir : `Sirocco.SamplePlugin` empaqueté via `dotnet pack` dans un dossier local
(un flux NuGet à part entière, celui d'un miroir d'entreprise hors ligne — pas une approximation de
nuget.org), résolu par `--plugin-package Sirocco.SamplePlugin --plugin-source <dossier>` contre
`Sirocco.SampleTarget` réellement démarré : refusé tel quel (`dotnet pack` ne signe pas), 0 %
d'échec une fois `--plugin-allow-unsigned` ajouté — confirmé une deuxième fois avec
`--plugin-package-version` explicite pour vérifier le chemin de cache.

Pour refaire ce tir, il faut lever explicitement le verrou d'empaquetage :

```bash
dotnet pack samples/Sirocco.SamplePlugin -c Release -p:IsPackable=true -o ./local-feed
sirocco run --plugin-package Sirocco.SamplePlugin --plugin-source ./local-feed --plugin-allow-unsigned --target-url http://localhost:5299 --rps 20 --duration 30s
```

`Directory.Build.props` pose `IsPackable=false` par défaut et seuls les neuf paquets réellement
publiés le remettent à `true` — un envoi sur nuget.org étant définitif, mieux vaut oublier de
publier que squatter un identifiant pour toujours. Ce plugin est un exemple : il n'a rien à faire
sur nuget.org, mais il doit rester empaquetable à la demande, puisque c'est tout son objet.
`--plugin-allow-unsigned` n'est nécessaire ici que parce que `dotnet pack` ne signe pas — un vrai
paquet publié sur nuget.org ou signé via `nuget sign`/`dotnet nuget sign` n'en a pas besoin.

### Extensions publiées et convention de découverte

Les cinq protocoles de référence sont publiés comme paquets NuGet, pas seulement présents dans le
dépôt : sans une seule extension publiée, la convention d'écriture d'extension n'a aucun exemple
consommable à copier. Chacun porte l'étiquette **`sirocco-extension`** — la convention de découverte
du projet, `nuget.org` n'offrant pas de réservation de préfixe pour une communauté. **Cette
étiquette n'est pas un adoubement** : n'importe qui peut la poser, et la poser ne fait pas d'un
paquet quelque chose de vérifié par ce dépôt. Rien ne remplace le fait de savoir quel code vous
exécutez (voir `SECURITY.md` sur le typosquatting). L'index des paquets qui la portent, généré
depuis une vraie requête nuget.org, est sur [Extensions publiées](communaute.md).

Les deux voies de consommation d'un paquet d'extension ne sont pas équivalentes, et laquelle
fonctionne dépend de la nature des dépendances — **vérifié par un vrai tir sur les quatre, pas
déduit** :

| Extension | `--plugin-package` | `PackageReference` + `dotnet publish` |
|---|---|---|
| [`Sirocco.Extensions.Sse`](#sse) | ✅ aucune dépendance | ✅ |
| [`Sirocco.Extensions.GraphQl`](#graphql) | ✅ aucune dépendance | ✅ |
| [`Sirocco.Extensions.Mqtt`](#mqtt) | ✅ `MQTTnet` restauré transitivement | ✅ |
| [`Sirocco.Extensions.Sql`](#sql) | ❌ `DllNotFoundException: e_sqlite3` | ✅ |
| [`Sirocco.Extensions.Browser`](#navigateur-web-vitals) | ❌ binaires de navigateur | ✅ (+ `playwright install`) |

Le cas SQL est la limite « actifs natifs » énoncée plus haut, observée en vrai : la chaîne
**managée** est bien restaurée sur quatre niveaux (`Microsoft.Data.Sqlite` →
`SQLitePCLRaw.batteries_v2`/`core`/`provider.e_sqlite3`), mais la bibliothèque native `e_sqlite3`
vit dans `runtimes/<rid>/native`, que ce chemin ne sert pas. `PackageReference` + `dotnet publish`
fonctionne, parce que MSBuild sait résoudre les actifs par identifiant de plateforme — c'est la voie
documentée dans le README de ce paquet.

## Protocoles de référence

Troisième bullet de la phase 6 : des extensions écrites contre le contrat de plugin, pour le
valider en conditions réelles plutôt que dans l'abstrait. `Sirocco.SamplePlugin` prouvait le
mécanisme de chargement ; ces extensions prouvent qu'un protocole *différent* de HTTP tient dans
le même contrat, sans rien changer au cœur.

Quatre au titre de la phase 6 (SQL, SSE, MQTT, GraphQL), plus un cinquième ajouté depuis —
[le navigateur](#navigateur-web-vitals), qui ne parle aucun protocole réseau lui-même et se
contente de rapporter les mesures du navigateur.

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
`PluginWorkflowLoader` doit être **publié** (`dotnet publish`), pas seulement compilé —
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
`PluginWorkflowLoader`) avant de le reproduire puis de le corriger par publication — la limite tient
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


### Navigateur (Web Vitals)

Cinquième protocole de référence, et le seul qui ne parle **aucun** protocole réseau lui-même :
`Sirocco.Extensions.Browser` pilote un vrai Chromium via
[Playwright](https://playwright.dev/dotnet/) et rapporte ce que le **navigateur** a mesuré — LCP,
FCP, TTFB et CLS. Il valide donc le contrat sous un angle qu'aucun des quatre autres n'exerce : le
plugin ne mesure pas ses propres appels, il transporte les mesures de quelqu'un d'autre.

```bash
dotnet publish extensions/Sirocco.Extensions.Browser -o publish/browser-plugin
pwsh -File publish/browser-plugin/playwright.ps1 install chromium
sirocco run publish/browser-plugin/Sirocco.Extensions.Browser.dll --target-url http://localhost:5281 --vus 2 --duration 30s
```

**Modèle fermé obligatoire.** Un contexte de navigateur coûte des centaines de Mo et une navigation
prend des secondes : ce plugin tourne à concurrence à un chiffre. Il se pilote en `--vus`, jamais en
`--rps` — sous un profil en débit, le moteur serait en dette d'ordonnancement permanente et
rapporterait une dette catastrophique à chaque tir : exact, et inutile. Le partage habituel
s'applique : le navigateur *mesure l'expérience*, un tir protocolaire *génère la charge*.

**Où va chaque vital, et pourquoi.** LCP, FCP et TTFB sont des durées en millisecondes, non
négatives et bornées : publiées comme des **étapes** plutôt que comme des métriques personnalisées,
elles héritent gratuitement de l'histogramme de latence, donc des centiles *et* des seuils.
`ResponseP75Milliseconds` existe déjà, et c'est exactement le centile auquel les Web Vitals sont
définis — `--threshold "LCP:ResponseP75Milliseconds:LessThanOrEqual:2500"` s'écrit directement.
Elles sont publiées avec un instant théorique égal à l'instant réel, donc **sans dette
d'ordonnancement** : un Web Vital n'a pas de départ planifié, et l'inverse ferait entrer le retard
de l'injecteur dans la valeur rapportée.

**Limite connue, énoncée plutôt que contournée** : CLS n'a **ni centile ni seuil**. C'est un score
sans unité (typiquement 0 à 1, fractionnaire) qu'un histogramme de millisecondes ne représente pas ;
il part donc en métrique personnalisée `trend`, dont `CustomMetricSnapshot` n'expose que min,
moyenne et max. Le commentaire de cette classe anticipait déjà le correctif — « un histogramme dédié
resterait à construire si le besoin de centiles se confirmait » — et ce protocole est précisément
cette confirmation, laissée à un chantier suivant plutôt que bâclée ici.

**Un vrai défaut du moteur trouvé en vérifiant.** Le premier tir réel a donné 74 itérations en
71,8 s pour `--vus 1 --duration 10s`, avec 63 s de dette et une étape `navigation` à 35 s de médiane.
Cause : la file de jetons vaut `max(vus × 2, 64)`, donc **64** dès que l'effectif est petit.
L'ordonnanceur du modèle fermé horodate chaque jeton à l'émission, remplit la file d'un coup, et les
travailleurs la vident longtemps après l'expiration du palier — d'où un débordement de durée, une
dette fantôme, et surtout des **centiles faussés**, puisque `ResponseTicks` se mesure depuis cet
horodatage. Les modèles tirés par les utilisateurs virtuels (fermé et nombre d'itérations) plafonnent
désormais la file à l'effectif lui-même, ce que la remarque de classe de `ClosedModelScheduler`
décrivait déjà comme le comportement voulu. Même tir après correction : 11 itérations en 12,5 s,
dette 4,1 s. Le modèle ouvert garde le défaut : sa file doit absorber une rafale, et la dette qu'elle
produit y est le vrai signal de saturation.

Il reste une attente en file **bornée à environ une itération** — un jeton est écrit dès qu'un
utilisateur virtuel se libère, il attend donc au plus la durée de l'itération en cours. Sur l'étape
`navigation`, lisez `p99 brut` (le temps de service) pour le chargement lui-même ; les trois vitals,
eux, ne sont pas concernés.

`Sirocco.SampleTarget` sert une page réelle sur `/demo`, construite pour que les trois mesures soient
**non nulles** — sans quoi un tir vert ne prouverait rien : la latence simulée donne le TTFB, une
ligne unique est peinte en premier (le FCP), un grand bloc est inséré à 150 ms et devient le plus
grand élément peint (le LCP, donc postérieur au FCP), et une bannière insérée après coup au-dessus du
contenu provoque un vrai glissement de mise en page (le CLS). Vérifié par un vrai tir : TTFB p50
32,8 ms, FCP p50 76,3 ms, **LCP p50 220,2 ms**, CLS 0,03 sur 45 itérations, 0 % d'échec — les quatre
valeurs cohérentes entre elles et avec la construction de la page.

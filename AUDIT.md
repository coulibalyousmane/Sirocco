# Audit Sirocco — 23 août 2026

Audit transversal mené sur `main` à `ff324e8`, juste avant la première publication publique
(paquets nuget.org + tag `v0.1.0`). Ce calendrier oriente les priorités : ce qui devient
irréversible ou visible d'un inconnu passe devant.

## Méthode

Chaque constat ci-dessous est adossé à une commande ou à une lecture de code, référencée. Rien
n'est déduit d'un nom de fichier ni supposé d'après la documentation.

Outillé : `dotnet list package --vulnerable --include-transitive` et `--deprecated` sur les 20
projets ; graphe complet des `ProjectReference`/`PackageReference` ; `dotnet format
--verify-no-changes --severity info` ; `dotnet pack` réel avec inspection du contenu des `.nupkg` ;
`docker run` sur l'image de base pour trancher l'utilisateur effectif ; exécution réelle du CLI ;
comparaison des drapeaux documentés contre le parseur.

**Hors périmètre, à énoncer plutôt qu'à laisser croire** : aucune mesure de couverture de code
n'avait été produite à la date de l'audit — elle l'a été depuis, en corrigeant QUAL-2 (70,8 % de
lignes) ; aucun fuzzing du parseur déclaratif, aucun audit de performance
sous charge, aucune revue ligne à ligne des 187 fichiers de `src` — les constats de code viennent
de motifs recherchés et de lectures ciblées sur les chemins sensibles.

---

## Ce qui est solide

À dire aussi, sans quoi un audit ne renseigne pas sur l'état réel.

- **Architecture respectée, vérifiée et non postulée.** `Sirocco.Domain` a **zéro** dépendance
  NuGet et **zéro** référence projet. Sur les 21 projets, aucune couche ne pointe vers l'intérieur :
  `Domain ← Application ← Infrastructure/Scenarios ← Host ← Cli`.
- **Zéro paquet vulnérable, zéro déprécié**, transitives incluses.
- **Hygiène de code inhabituelle** : aucun `async void`, **aucun `catch` vide**, aucun
  `TODO`/`FIXME`/`HACK` dans tout le dépôt, et le seul `Thread.Sleep` vit dans `PrecisionWait` avec
  un commentaire expliquant pourquoi il ne doit pas être remplacé.
- **Les 187 fichiers de `src` sont tous nommés dans les tests** (761 tests verts), et depuis la
  correction de QUAL-2 ce n'est plus une métrique de mention : la couverture réelle est de
  **70,8 % de lignes / 66,7 % de branches**, mesurée à chaque run et gardée par un plancher.
- **`FixedTimeEquals` réellement employé** pour le secret partagé
  (`ClusterAuthentication.cs:47`) : la comparaison en temps constant annoncée existe.
- **CI sans exposition de secrets** : `ci.yml` n'en référence aucun, donc son déclencheur
  `pull_request` ne peut rien exfiltrer, et aucun `${{ }}` risqué n'est interpolé dans un `run`.
- **Documentation exacte sur ses propres limites.** Les drapeaux documentés existent tous dans le
  parseur (deux exceptions traitées en FONC-1 et FONC-2), et la limite RBAC est écrite noir sur
  blanc en `docs/distribue/kubernetes.md:70`.

---

## Sécurité

### SEC-1 — ~~Élevé~~ → **corrigé le 24 août 2026** — Un worker joignable était un générateur de charge télécommandé

`Sirocco__ClusterSharedSecret` est **nul par défaut**, et
`ClusterAuthentication.IsAuthorized` rend alors `true` sans regarder la requête
(`ClusterAuthentication.cs:31-34`). Les endpoints `/worker/prepare` et `/worker/start`
(`Program.cs:54` et `:60`) sont donc ouverts, et `WorkerPrepareRequest` porte
**`TargetBaseUrl`**, le profil de charge et `MaxVirtualUsers` — tous fournis par l'appelant.

Conséquence : quiconque atteint le port d'un worker lui fait marteler l'hôte de son choix, au
débit de son choix. Un déploiement laissé en configuration par défaut est un amplificateur d'abus.

**Ce que ce n'est pas, vérifié** : il n'y a **pas** d'exécution de code à distance. `ScenarioFormat`
ne connaît que `Yaml` et `Json` (`ScenarioFormat.cs`), et `WorkerCoordinator.CreateWorkflow` ne
peut atteindre ni `ScriptedWorkflowLoader` ni `PluginWorkflowLoader` — aucun des deux n'est
référencé dans `src/Sirocco.Host/Distributed/`. La distinction change la gravité, elle mérite
d'être établie plutôt que supposée dans un sens ou dans l'autre.

**Corrigé.** `ClusterAuthentication.EnsureConfigured` est appelée avant même la construction de
l'hôte : un rôle `master` ou `worker` sans secret **refuse de démarrer**, code de sortie non nul,
avec un message qui nomme les deux issues. Le mode autonome n'expose rien et n'a pas changé.

Trois choix qui méritent d'être énoncés plutôt que devinés :

- **Une échappatoire nommée**, `Sirocco__AllowUnauthenticatedClusterControlPlane`. Sans elle, la
  correction serait une impasse pour qui tourne délibérément sur un réseau confiné, et le
  contournement serait alors un faux secret partout — pire que l'ouverture assumée. Le nom est long
  à dessein : il doit être choisi et lu, pas hérité.
- **Un minimum de 16 caractères**, y compris quand l'échappatoire est active. Rien ne limite le
  nombre d'essais côté serveur : la taille du secret est la seule défense contre la devinette, et un
  garde qu'un secret d'un caractère franchit est décoratif. Un secret court accepté serait le pire
  des deux états — protégé en apparence, devinable en fait.
- **L'opérateur Kubernetes rend `clusterSharedSecretRef` requis** : sans lui, la `TestRun` passe en
  `Failed` avec le motif dans `kubectl describe`, **et aucune ressource fille n'est créée** — plutôt
  que des pods en `CrashLoopBackOff` dont la cause ne se lirait qu'en fouillant leurs journaux.
  L'échappatoire n'est pas exposée dans la ressource : dans un cluster, un `Secret` est à portée de
  main.

Vérifié sur de vrais processus et un vrai cluster, pas en raisonnement :

| Épreuve | Résultat |
|---|---|
| `worker` / `master` sans secret | Refus au démarrage, code de sortie non nul |
| Secret de 5 caractères | Refus, message donnant le minimum |
| Secret de 5 caractères **+** échappatoire | Refus quand même |
| Échappatoire seule (l'ancien défaut) | Démarre ; `POST /worker/prepare` **anonyme** vers un tiers arbitraire à 5000 req/s → **HTTP 200** |
| Secret configuré | Anonyme → **401** (45 ms) ; mauvais jeton → **401** ; bon jeton → **200** |
| 1 maître + 2 workers, secret des deux côtés | 160 itérations fusionnées, **0 échec**, code de sortie 0 |
| Mode autonome | 80 itérations, **0 échec**, code de sortie 0 — inchangé |
| `TestRun` sans `clusterSharedSecretRef` (cluster réel) | `Failed`, motif lisible, **0 ressource fille** |
| `TestRun` avec la référence (cluster réel) | `Running`, les 4 ressources créées, secret câblé en `secretKeyRef` |

La ligne « échappatoire seule » est la mesure de ce qui était ouvert par défaut : ce `200` était le
comportement livré, pas une hypothèse.

**Découvert en vérifiant, à savoir avant de tester l'authentification à la main** : le corps de la
requête est lié **avant** le filtre d'authentification. Un corps JSON incomplet obtient donc `400`,
pas `401` — sans conséquence (un `400` ne prépare aucun tir), mais mon premier essai a lu un `400`
comme une preuve de rejet alors qu'il ne prouvait rien. C'est un corps valide qui prouve le `401`.

**Reste ouvert, délibérément** : l'allow-list d'hôtes cibles côté worker, qui serait la défense en
profondeur. Elle traite un risque différent — un appelant **authentifié** qui désigne un tiers — et
mérite son propre arbitrage sur la forme (liste fixe ? motifs ? par worker ?) plutôt que d'être
glissée ici.

### SEC-2 — ~~Moyen~~ → **corrigé le 23 août 2026** — Le scénario généré depuis un HAR contenait les jetons en clair

`HarConverter._headersToStrip` (`HarConverter.cs:28-33`) ne retire que des en-têtes régénérés
mécaniquement : `host`, `content-length`, `connection`, `content-type`, `accept-encoding` et les
pseudo-en-têtes HTTP/2. **Ni `authorization`, ni `cookie`.** Et le proxy enregistreur ne filtre pas
davantage : `ProxyHeaders` ne traite que les en-têtes de bout en bout de la RFC 7230 §6.1.

Le C# produit contient donc littéralement
`request0.Headers.TryAddWithoutValidation("Authorization", "Bearer …")`
(`HarConverter.cs:247`) — dans un fichier destiné à être committé.

**Corrigé.** Nouveau jeu `_sensitiveHeaders` (`authorization`, `proxy-authorization`, `cookie`,
`set-cookie`, `x-api-key`, `x-auth-token`, `x-csrf-token`) retiré du script généré. À la place, une
ligne **commentée** lisant une variable d'environnement (`SIROCCO_AUTHORIZATION`…), pour que le
scénario reste réparable en une seconde sans qu'un jeton vivant ait jamais touché le disque. Le
retrait est **compté et annoncé** par l'outil, comme il comptait déjà les requêtes ignorées.

Vérifié par une conversion réelle d'un HAR portant un jeton et un cookie : **0 occurrence** de l'un
et de l'autre dans le `.csx` produit, les lignes commentées présentes, et un en-tête métier anodin
(`X-Tenant`) qui traverse bien. Le test qui *assertait la fuite*
(`HarConverterTests.cs:194`) est inversé.

### SEC-3 — ~~Moyen~~ → **corrigé le 23 août 2026** — L'épinglage TLS reposait sur une empreinte SHA-1

`ClusterCertificatePinning.ValidateThumbprint` compare `certificate.Thumbprint`, propriété
qui est un **SHA-1** par définition. Les collisions à préfixe choisi sur SHA-1 sont démontrées
depuis 2017.

**Corrigé.** `ValidateThumbprint` compare désormais
`certificate.GetCertHash(HashAlgorithmName.SHA256)`. Et parce qu'une empreinte SHA-1 laissée en
configuration ne correspondrait plus *jamais* — symptôme : des connexions refusées sans motif
lisible —, `CreateHandler` **refuse de démarrer** sur une empreinte qui n'a pas les 64 caractères
d'un SHA-256, avec un message disant quoi recalculer.

Trois tests ajoutés, dont la contre-épreuve qui compte : `The_certificate_s_sha1_thumbprint_is_no_longer_accepted`
échouerait si quelqu'un revenait à `X509Certificate2.Thumbprint`, même par inadvertance. La
documentation TLS est corrigée en conséquence — elle recommandait `$cert.Thumbprint`, qui est
précisément le SHA-1 ; elle donne maintenant `$cert.GetCertHashString("SHA256")` et l'équivalent
`openssl`.

### SEC-4 — ~~Moyen~~ → **corrigé le 24 août 2026** — Les quatre conteneurs tournaient en root

Aucun des quatre `Dockerfile` ne porte de directive `USER`. Vérifié empiriquement plutôt que
supposé : `docker run --rm --entrypoint sh mcr.microsoft.com/dotnet/aspnet:10.0 -c id` rend
`uid=0(root)`. Cela vaut aussi pour `Sirocco.Operator`, qui détient des droits RBAC de cluster.

**Corrigé.** `USER $APP_UID` dans l'étage runtime des trois images .NET — UID **numérique** (1654)
et non le nom `app`, parce que Kubernetes évalue `runAsNonRoot` avant de résoudre `/etc/passwd` et
refuse un conteneur dont l'utilisateur est un nom.

La quatrième, `benchmark/gatling`, n'a pas de base .NET et **écrit réellement** dans son répertoire
de travail : `mvnw` y compile la simulation et télécharge son dépôt Maven. Elle a donc reçu un
utilisateur dédié (`useradd`, UID **1001** — vérifié sur l'image, pas supposé : la base est passée à
Ubuntu 26.04, où l'UID 1000 est déjà pris par l'utilisateur `ubuntu`, et `useradd` aurait échoué).

**L'échec qui a imposé le vrai correctif.** Basculer d'utilisateur et faire un `chown -R
/opt/gatling` ne suffisait pas : le tir échouait sur `Cannot create resource output directory:
/opt/gatling/target/test-classes`. Cause réelle — `target/gatling` est le **point de montage** du
dossier de résultats, et Docker crée les répertoires manquants d'un montage **à l'exécution, en
root**. `target/` appartenait donc à root pendant le tir, et aucun `chown` au build ne pouvait le
prévoir puisque le répertoire n'existait pas encore. D'où le `mkdir -p /opt/gatling/target/gatling`
avant le `chown`. Trouvé en faisant tourner l'image, jamais visible en la relisant.

**Au-delà de la lettre du constat**, parce qu'une image peut être lancée avec `--user 0` : les pods
que l'opérateur fabrique (`TestRunResources`) et l'opérateur lui-même portent maintenant
`runAsNonRoot: true` + `seccompProfile: RuntimeDefault` au niveau du pod, et
`allowPrivilegeEscalation: false` + `capabilities.drop: [ALL]` au niveau du conteneur. La différence
est réelle : le cluster **refuse de démarrer** un pod dont l'image reviendrait à root, au lieu de le
laisser passer en silence. `readOnlyRootFilesystem` en est délibérément absent — un scénario scripté
est compilé par Roslyn et un plugin NuGet atterrit dans un cache, tous deux hors de `/app` ; le
verrouiller demanderait de monter les emplacements temporaires un par un.

Vérifié en exécutant, pas en relisant :

| Épreuve | Résultat |
|---|---|
| `id` dans les 4 images construites | `uid=1654(app)` pour hôte, cible et opérateur ; `uid=1001(gatling)` pour Gatling |
| `docker compose up --exit-code-from master` | Les 4 conteneurs sortent en 0, seuils respectés, 224 étapes `checkout`, **0 échec** |
| Image Gatling, vrai tir contre la cible | Sortie 0, `simulation.log` **et** `index.html` écrits dans le volume monté depuis l'hôte |
| `TestRun` de démonstration sur le cluster réel | `Succeeded` ; 224 itérations, **0 échec**, seuils `[OK]` |
| Pods du tir, relus sur le cluster | maître et 2 workers : `runAsNonRoot=true`, `allowPrivilegeEscalation=false`, `drop=[ALL]` |
| Pod de l'opérateur | `Running` avec `runAsNonRoot=true` |

**Reste ouvert** : `readOnlyRootFilesystem` (ci-dessus), et le fait que `docker-compose.yml` ne pose
aucun `security_opt` — la propriété y repose entièrement sur le `USER` de l'image, sans le filet que
Kubernetes apporte.

### SEC-5 — Faible — Un certificat épinglé expiré reste accepté

Dès qu'une empreinte est configurée, `CreateHandler` installe un
`ServerCertificateCustomValidationCallback` qui rend `true` sur la seule correspondance
d'empreinte. Toute la validation de chaîne est donc remplacée : expiration, révocation et nom
d'hôte ne sont plus vérifiés. C'est cohérent avec un certificat auto-signé partagé, mais la
non-vérification de l'expiration mérite d'être écrite à côté de « rotation manuelle ».

### SEC-6 — ~~Faible~~ → **corrigé le 25 août 2026** — Le `ClusterRole` accordait `'*'` sur des ressources jamais supprimées ni modifiées en bloc

Constat initial : `verbs: ['*']` sur `services`, `jobs` et `statefulsets`, à l'échelle du cluster
(`ClusterRole`), permettait de créer un pod dans n'importe quel namespace — la limite de portée
elle-même était déjà documentée et assumée, mais pas sa conséquence en verbes.

**Corrigé, mais pas comme prévu à l'origine.** Le premier réflexe — passer d'un `ClusterRole` à un
`Role` namespaced — aurait changé un choix de conception distinct et assumé ailleurs (l'opérateur
surveille les `TestRun` sur tout le cluster, pas un seul namespace) : ce n'était pas ce que ce
constat reprochait. Relire `TestRunController.ReconcileAsync` a montré la vraie cible : les trois
attributs `[EntityRbac]` demandaient `RbacVerb.All` alors que le contrôleur ne fait jamais que
créer ces ressources, les relire par nom, et (seul le `StatefulSet`, pour l'autoscaling et le
retour à 0 réplique) les mettre à jour — jamais les supprimer ni les lister/surveiller. Réduits à
`Get`+`Create` (`Job`, `Service`) et `Get`+`Create`+`Update` (`StatefulSet`), régénérés via
`dotnet kubeops generate operator` plutôt qu'édités à la main, pour rester l'artefact reproductible
qu'ils prétendent être.

**La vérification a trouvé un second problème, plus concret que le premier.** La contre-épreuve
RBAC (`kubectl auth can-i delete jobs --as=<le compte de service de l'opérateur>`) répondait encore
`yes` après le déploiement du rôle corrigé. Cause : un `ClusterRole`/`ClusterRoleBinding`
**résiduel** (`operator-role`/`operator-role-binding`, sans préfixe), oublié sur ce cluster de
développement depuis une session antérieure ayant appliqué les manifestes individuellement plutôt
que via kustomize — il ciblait le **même compte de service** et portait encore `'*'`. Par union
RBAC, ce résidu neutralisait silencieusement le resserrement tant que les deux rôles coexistaient.
Supprimé (avec un second résidu sans rapport, `tempest-operator-*`, vestige d'avant le renommage du
projet, déjà signalé sans suite lors d'une session précédente). **Leçon générale plutôt que
spécifique à ce cluster** : sur un cluster de développement longue durée, une contre-épreuve RBAC
doit interroger l'état réellement en vigueur, pas seulement le manifeste qu'on vient d'appliquer —
un durcissement peut être correct sur le papier et neutralisé par un artefact oublié.

| Vérification | Avant | Après |
|---|---|---|
| `can-i get/create jobs`, `create/update statefulsets`, `create services` | permis | **permis** (inchangé) |
| `can-i delete jobs/statefulsets/services` | permis | **refusé** |
| `can-i list services`, `can-i watch statefulsets` (jamais exercés) | permis | **refusé** |
| Vrai cycle de réconciliation (création des 4 ressources filles, lecture du statut du `Job`, réduction du `StatefulSet` à 0 réplique) | — | **0 erreur d'autorisation**, journaux de l'opérateur à l'appui |
| Résidu RBAC neutralisant, trouvé en vérifiant l'état réel du cluster | — | supprimé (avec le vestige `tempest-operator-*` sans rapport) |

**Reste ouvert** : le rôle demeure un `ClusterRole` (portée cluster assumée, constat distinct, non
rouvert ici). Les règles générées sur les sous-ressources `*/status` (get/update/patch) restent
plus larges que ce qu'utilise le contrôleur — KubeOps les émet inconditionnellement pour tout type
annoté, retaillage non fait pour ne pas diverger de l'artefact généré. Détail complet dans
[Opérateur Kubernetes](docs/distribue/kubernetes.md#opérateur-kubernetes).

### SEC-7 — Faible — Les plugins sont chargés sans isolation ni vérification de signature

`PluginWorkflowLoader.cs:45` utilise `Assembly.LoadFrom` : pas d'`AssemblyLoadContext` isolé, donc
ni déchargement ni cloisonnement des dépendances. Aucune vérification de signature de paquet dans
`src/Sirocco.Scenarios/Plugins/`. `--plugin-package X` contre nuget.org revient donc à exécuter le
code d'un nom saisi au clavier, typosquatting compris. Inhérent à un système de plugins, mais à
énoncer comme frontière de confiance.

### SEC-8 — Info — Secret de démonstration en clair dans `docker-compose.yml`

`Sirocco__ClusterSharedSecret: "demo-cluster-secret"` apparaît trois fois (lignes 32, 57, 72). Le
nom annonce la couleur et c'est un fichier de démo ; le risque est la copie telle quelle vers un
environnement réel. Aucun secret réel n'a été trouvé dans le dépôt.

### SEC-9 — ~~Moyen~~ → **corrigé le 25 août 2026** — Aucune façon d'injecter un secret dans un scénario déclaratif

Constat qui n'appartient pas aux 20 relevés le 23 août : trouvé en évaluant la maturité du
projet pour un usage en entreprise, en cherchant `Environment.GetEnvironmentVariable` dans tout
`src/` — zéro occurrence. Les seules sources de substitution `{{...}}` d'un scénario déclaratif
étaient les jeux de données (des fichiers, donc committables) et la corrélation depuis une
réponse précédente. Un jeton d'API statique devait donc être écrit **en clair** dans le YAML —
exactement la faute que SEC-2 avait corrigée sur le convertisseur HAR, réintroduite par une autre
porte, à la parité fonctionnelle de k6 (`__ENV`), Gatling (`System.getenv`) et NBomber près.

**Corrigé.** `{{env.NOM}}` lit la variable d'environnement `NOM` du processus, résolue
directement dans `DeclarativeWorkflow.TrySubstitute` (`src/Sirocco.Scenarios/DeclarativeWorkflow.cs`) —
sans énumérer tout l'environnement à chaque itération, pour ne pas payer sur le chemin critique
d'un générateur de charge un coût proportionnel à des variables jamais référencées. `env` est un
nom de jeu de données désormais réservé : `ScenarioDefinition.Validate()` rejette un jeu de
données ainsi nommé, pour qu'une collision échoue au chargement plutôt que de changer
silencieusement de sens à la substitution. Documenté dans
[Variables d'environnement](docs/scenarios/donnees-assertions.md#variables-denvironnement).

| Vérification | Résultat |
|---|---|
| Vrai tir, variable non définie | 100 % d'échec, 0 requête envoyée à la cible |
| Vrai tir, variable définie | 0 % d'échec, seuil `ErrorRate<=0.01` respecté, code de sortie 0 |
| Jeu de données nommé `env` | rejeté par `Validate()` |
| Tests unitaires | 2 nouveaux sur `DeclarativeWorkflow`, 1 sur `ScenarioDefinition` |

**Reste ouvert, énoncé plutôt que corrigé ici** : `{{env.NOM}}` donne accès à n'importe quelle
variable du processus, pas à une liste que l'opérateur aurait explicitement autorisée — même
modèle que les trois concurrents cités, pas une restriction propre à Sirocco, mais c'est la
**première** chose qui donne à un scénario déclaratif une autorité ambiante hors de son propre
fichier. Documenté comme frontière de confiance dans la même page : ne faites tourner un
scénario dont vous n'êtes pas l'auteur que dans un processus qui ne détient aucun secret sans
rapport avec le tir.

---

## Défauts fonctionnels

### FONC-1 — ~~Moyen~~ → **corrigé le 23 août 2026** — `sirocco run --help` échouait, alors que le README le prescrit

Reproduit :

```
$ sirocco --help          → affiche l'aide
$ sirocco run --help      → « Option non reconnue ou incomplete : '--help'. »
```

`Program.cs:88` ne teste `-h`/`--help` que sur `args[0]`. Avec `run --help`, `args[0]` vaut `run`,
le contrôle de verbe passe, et `CliOptions.Parse(args[1..])` reçoit un drapeau qu'il ne connaît
pas. Or `sirocco run --help` est exactement la forme écrite dans **`README.md:22`** et
**`docs/demarrer/cli.md:43`**.

C'est la première commande qu'un nouvel arrivant tape pour découvrir l'outil, et le premier
paquet public est sur le point d'être publié.

**Corrigé.** L'aide est reconnue n'importe où dans la ligne de commande, plus seulement en premier
argument. Vérifié sur le binaire, pas dans le source — les cinq formes répondent :

```
sirocco --help                     → Usage : sirocco run [scenario.yaml] [options]
sirocco -h                         → Usage : …
sirocco run --help                 → Usage : …
sirocco run -h                     → Usage : …
sirocco run scenario.yaml --help   → Usage : …
```

Contre-épreuve indispensable, sans quoi le correctif aurait pu rendre le parseur permissif :
`sirocco run --pas-une-option` répond toujours « Option non reconnue ou incomplete ».

### FONC-2 — Faible — `--scenario-file` documenté mais inexistant

`docs/extensions/contrat.md:32` écrit `` `sirocco run`/`--scenario-file` ``. Ce drapeau n'existe
pas : le chemin se donne en argument positionnel, ou par la clé de configuration
`Sirocco:ScenarioFile` (`src/Sirocco.Cli/Program.cs:147`). Formulation à corriger.

---

## Qualité et outillage

### QUAL-1 — ~~Moyen~~ → **corrigé le 24 août 2026** — `.editorconfig` ne fixait pas `end_of_line`

Le fichier posait `insert_final_newline` et `charset` mais pas `end_of_line`. Le diagnostic initial
supposait que `dotnet format` se rabattait sur le défaut de plateforme. **La mesure a montré pire** :
sans `end_of_line`, `dotnet format` ne regarde pas les fins de ligne **du tout**. Un arbre mi-CRLF
mi-LF passait à 0 violation des deux côtés. Rien ne signalait la dérive — c'est ainsi qu'un `sed -i`
de Git Bash (qui retire les CR au passage) a pu convertir des fichiers en silence pendant le
renommage.

**Corrigé.** `end_of_line = lf` dans `.editorconfig`, plus un `.gitattributes` neuf
(`* text=auto eol=lf`). Les deux moitiés sont nécessaires : sous Windows l'installateur de git pose
`core.autocrlf = true`, donc sans `.gitattributes` la règle `.editorconfig` deviendrait fausse dès
le premier clone et signalerait chaque fichier du dépôt.

**LF et non le `crlf` que ce constat proposait initialement**, sur trois mesures et non par
préférence : l'index git contenait déjà du LF sur les 374 fichiers suivis (`git add --renormalize .`
n'a produit aucun changement de contenu) ; la CI tourne sous Linux ; et `benchmark/*.sh` comme les
Dockerfile s'exécutent dans des conteneurs Linux, où un CRLF casse le shebang. Un choix CRLF aurait
exigé une liste d'exceptions, LF n'en exige aucune. Aucun `.bat` ni `.cmd` dans le dépôt.

| Vérification | Avant | Après |
|---|---|---|
| `ClusterCertificatePinningTests.cs` (CRLF sur disque) devant `dotnet format` | accepté, 0 violation | **refusé**, `ENDOFLINE` ligne par ligne |
| Fichiers suivis, index | 374 `i/lf` | 374 `i/lf` (inchangé) |
| Fichiers suivis, copie de travail | 19 en `w/crlf`/`w/mixed` | 0 |
| Diff de contenu après correction | — | **vide** (seules les fins de ligne ont bougé) |
| Barrière | — | 761 tests verts, `dotnet format` exit 0 sur la solution **et** les deux projets du harnais |

La contre-épreuve n'a pas eu à être fabriquée : le fichier qui passait avant le changement est celui
que `dotnet format` a nommé après. C'est la démonstration que le réglage mord réellement.

**Reste ouvert** : `dotnet format` ne couvre que les `.cs` de la solution. Les 15 fichiers `.yml`,
`.json`, `.md` et `.csx` restants ont été alignés à la main ; rien ne les empêchera de dériver à
nouveau côté copie de travail. Le filet qui compte est ailleurs : `.gitattributes` normalise à
l'entrée dans l'index, donc un clone neuf et la CI verront toujours du LF quoi qu'il arrive sur le
disque de qui commite.

### QUAL-2 — ~~Moyen~~ → **corrigé le 25 août 2026** — La couverture était mesurable mais jamais mesurée

`coverlet.collector` 6.0.4 était référencé (`Sirocco.UnitTests.csproj:20`), mais **aucune étape de
CI ne collectait ni ne publiait de couverture** : aucun `--collect` dans les workflows. 761 tests
verts sans savoir quelles branches sont exercées.

**Corrigé.** `build-and-test` collecte désormais à chaque run, et la mesure est à la fois lisible et
contraignante : ReportGenerator (déclaré dans `.config/dotnet-tools.json`, donc reproductible en
local par `dotnet tool restore`) pousse un tableau par assembly dans le **résumé du run**, publie le
**rapport HTML en artefact** (`couverture-html`), et un **plancher à 65 % de lignes** fait échouer le
job en dessous. Détail complet dans [Pipeline CI](docs/projet/ci.md#couverture-de-code).

**Le chiffre : 70,8 % de lignes, 66,7 % de branches** (5 449/7 693, mesuré en `Release`, la
configuration de la CI).

Trois décisions énoncées plutôt que subies :

- **Pas de Codecov ni de Coveralls.** Ce constat-ci en aurait détruit un autre : la liste « Ce qui
  est solide » relève que `ci.yml` ne référence aucun secret, donc que son déclencheur
  `pull_request` ne peut rien exfiltrer depuis un fork. Un jeton d'envoi à un service tiers, sur un
  dépôt public, aurait échangé cette propriété contre l'affichage d'un chiffre que le résumé du run
  donne déjà.
- **Un plancher, pas une cible**, et volontairement sous la mesure réelle : il doit attraper la
  disparition des tests d'un sous-système, pas signaler la variation normale d'un runner partagé.
- **Aucun filtre d'exclusion.** Le chiffre est brut. Écarter le code généré (protobuf, générateur de
  regex, qui pèsent sur `Sirocco.Scenarios`) l'aurait embelli sans rien changer à ce qui est
  réellement vérifié.

**Ce que la mesure a appris, au-delà du constat.** Le détail par assembly confirme chiffres à l'appui
les choix de vérification déjà écrits dans les commentaires du `.csproj` : `Application` 94,3 %,
`Domain` 92,9 %, `Infrastructure` 91,4 % — mais `Sirocco.Host` à **14 %**, et `Sirocco.Operator` à
66,1 % de lignes pour seulement 22,6 % de branches. Ces deux chiffres bas ne sont pas des lacunes :
ce sont les zones délibérément vérifiées par de vrais tirs (tout le câblage distribué) ou impossibles
à tester sans cluster (`TestRunController`, KubeOps n'offrant pas de harnais hors cluster). La mesure
ne contredit donc pas la stratégie de test du projet, **elle la chiffre**.

| Vérification | Résultat |
|---|---|
| Séquence exacte de la CI rejouée en local (`--results-directory`, glob ReportGenerator, 3 sorties) | 3 rapports écrits, exit 0 |
| Barrière au plancher réel (65 %) | exit **0** |
| Contre-épreuve, plancher porté à 99 % | exit **1**, message nommant le chiffre |
| Lecture de la valeur | `"linecoverage"` mesuré **unique** dans `Summary.json` (contre 209 `"branchcoverage"`) — lecture sans ambiguïté |

**Reste ouvert** : le plancher est calibré sur une mesure Windows/`Release`. Le premier run Linux
donnera la vraie base ; il sera resserré à ce moment-là, pas avant. Et `awk` plutôt que `jq` pour
lire le JSON — non par méfiance envers `jq`, présent sur les runners, mais parce qu'il est absent du
Git Bash où l'étape a été mise au point, donc invérifiable en local.

### QUAL-3 — Faible — Blocage synchrone sur du code asynchrone

`ScriptedWorkflowLoader.cs:67` : `LoadFromSourceAsync(...).GetAwaiter().GetResult()`. Sans contexte
de synchronisation ASP.NET Core le risque d'interblocage est faible, et l'appel n'a lieu qu'au
démarrage. L'autre occurrence, `BlockingTokenWriter.cs:36`, est délibérée et nommée comme telle.

---

## Dépendances

### DEP-1 — Aucune vulnérabilité, aucune dépréciation

Sur les 20 projets, transitives incluses. C'est un état, pas une garantie : voir DEP-2.

### DEP-2 — Faible — Aucun `dependabot.yml`

Rien ne surveille l'apparition d'un avis de sécurité. L'enjeu monte d'un cran dès que cinq paquets
sont publiés : une vulnérabilité transitive se propage alors aux consommateurs.

---

## Architecture

### ARCH-1 — Clean Architecture tenue

Voir « Ce qui est solide ». Vérifié sur les 21 projets, pas déduit des noms de dossiers.

### ARCH-2 — Faible — L'opérateur duplique les chaînes de rôle

`SiroccoHostOptions` expose `ROLE_MASTER = "master"` et `ROLE_WORKER = "worker"` (lignes 42 et 45),
mais `Sirocco.Operator` n'a **aucune référence projet** et écrit les littéraux `"master"` et
`"worker"` en quatre endroits (`TestRunResources.cs:147, 153, 203, 268`).

Un changement de ces chaînes ne casserait pas la compilation : il produirait des pods au rôle
invalide, défaut visible seulement à l'exécution.

---

## Gouvernance du dépôt

### GOUV-1 — ~~Moyen~~ → **corrigé le 23 août 2026** — Pas de `SECURITY.md`

Aucune politique de divulgation sur un dépôt public, pour un outil à double usage dont SEC-1
décrit un abus possible. C'était le fichier que cherche quelqu'un qui veut signaler quelque chose
sans l'écrire dans une issue publique.

**Corrigé.** [`SECURITY.md`](SECURITY.md) écrit. Il ne se contente pas d'une adresse de contact :
pour un générateur de charge, la frontière entre « comportement attendu » et « vulnérabilité » n'est
pas celle d'un logiciel ordinaire, donc le fichier la trace explicitement. Saturer une cible qu'on
lui désigne, ou exécuter un scénario scripté qu'on lui fournit, sont sa fonction. En revanche
divulguer un secret configuré, contourner l'authentification quand elle est active, ou exécuter du
code depuis une **donnée** (YAML, HAR, OpenAPI, réponse de la cible) sont des failles.

Les délais annoncés — accusé de réception à 7 jours, évaluation à 30 — sont présentés comme les
objectifs d'un projet mené par une personne seule, pas comme un engagement d'entreprise.

**Une action t'appartient** : le formulaire de signalement privé ne fonctionne que si *Private
vulnerability reporting* est activé dans les réglages du dépôt.

### GOUV-2 — Faible — Pas de `CHANGELOG.md`

On s'apprête à publier des paquets versionnés ; rien ne dira ce que `0.2.0` change.

### GOUV-3 — Faible — `.gitignore` incomplet sur les matériels cryptographiques

`.env` (ligne 7) et `*.pfx` (ligne 247) sont couverts, mais ni `*.pem`, ni `*.key`, ni `*.p12` —
formats qu'un utilisateur suivant la procédure TLS peut très bien produire.

Absents également, sans que ce soit gênant pour un projet solo : `CONTRIBUTING.md`,
`CODE_OF_CONDUCT.md`, modèles d'issue.

---

## Priorités

**À traiter avant le tag `v0.1.0`** — ce qui devient irréversible, public, ou visible dès la
première minute :

**Les cinq sont traités.**

| # | Pourquoi maintenant | État |
|---|---|---|
| SEC-3 | Changer le format d'empreinte après que quelqu'un a noté la sienne serait cassant | ✅ SHA-256 + refus au démarrage d'une empreinte SHA-1 |
| FONC-1 | C'est la première commande que tape un nouvel arrivant, et elle échouait | ✅ 5 formes vérifiées sur le binaire |
| GOUV-1 | Un dépôt public sans canal de signalement, sur un outil dual-use | ✅ `SECURITY.md` |
| SEC-2 | Un utilisateur pouvait committer ses jetons dès le premier usage du convertisseur | ✅ rédaction + report, prouvé par conversion réelle |
| SEC-1 | Change un défaut : à livrer dans une version qui l'annonce, donc avant le premier tag, jamais après | ✅ refus au démarrage + échappatoire nommée, prouvé sur processus réels et cluster réel |

Barrière repassée après correction : build 0/0, **761 tests** verts, `dotnet format` à 0 violation
sur la solution **et** sur les deux projets du harnais, DocFX 0 avertissement.

**Ensuite, par ordre de gain** : ~~SEC-4~~ et ~~QUAL-1~~ (corrigés le 24 août), ~~QUAL-2~~,
~~SEC-9~~ et ~~SEC-6~~ (corrigés le 25 août). **Les trois constats « Moyen » et deux des trois
« Faible » de sécurité sont traités** ; ne subsistent que SEC-5, SEC-7, SEC-8 et les constats
hors sécurité classés « Faible »/« Info ».

**À ne pas traiter** : SEC-5 et SEC-7 décrivent des frontières de confiance assumées. Ils
demandent une phrase de documentation, pas du code.

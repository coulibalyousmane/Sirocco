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
n'a été produite (voir QUAL-2), aucun fuzzing du parseur déclaratif, aucun audit de performance
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
- **Les 187 fichiers de `src` sont tous nommés dans les tests** (750 tests verts). Métrique de
  mention, pas de couverture — mais aucune classe n'est ignorée en bloc.
- **`FixedTimeEquals` réellement employé** pour le secret partagé
  (`ClusterAuthentication.cs:47`) : la comparaison en temps constant annoncée existe.
- **CI sans exposition de secrets** : `ci.yml` n'en référence aucun, donc son déclencheur
  `pull_request` ne peut rien exfiltrer, et aucun `${{ }}` risqué n'est interpolé dans un `run`.
- **Documentation exacte sur ses propres limites.** Les drapeaux documentés existent tous dans le
  parseur (deux exceptions traitées en FONC-1 et FONC-2), et la limite RBAC est écrite noir sur
  blanc en `docs/distribue/kubernetes.md:70`.

---

## Sécurité

### SEC-1 — Élevé — Un worker joignable est un générateur de charge télécommandé

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

**Correctif proposé** : refuser de démarrer en rôle `master`/`worker` sans secret configuré, plutôt
que de démarrer ouvert. Le mode autonome, lui, n'expose rien et n'a pas à changer. Une allow-list
d'hôtes cibles côté worker serait la défense en profondeur.

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

### SEC-4 — Moyen — Les quatre conteneurs tournent en root

Aucun des quatre `Dockerfile` ne porte de directive `USER`. Vérifié empiriquement plutôt que
supposé : `docker run --rm --entrypoint sh mcr.microsoft.com/dotnet/aspnet:10.0 -c id` rend
`uid=0(root)`. Cela vaut aussi pour `Sirocco.Operator`, qui détient des droits RBAC de cluster.

**Correctif proposé** : `USER $APP_UID` dans l'étage runtime — la variable est déjà définie par les
images .NET.

### SEC-5 — Faible — Un certificat épinglé expiré reste accepté

Dès qu'une empreinte est configurée, `CreateHandler` installe un
`ServerCertificateCustomValidationCallback` qui rend `true` sur la seule correspondance
d'empreinte. Toute la validation de chaîne est donc remplacée : expiration, révocation et nom
d'hôte ne sont plus vérifiés. C'est cohérent avec un certificat auto-signé partagé, mais la
non-vérification de l'expiration mérite d'être écrite à côté de « rotation manuelle ».

### SEC-6 — Faible — Le rayon d'action du `ClusterRole` n'est pas énoncé

La limite est documentée (`docs/distribue/kubernetes.md:70`) et le choix assumé. Ce qui manque est
la conséquence : `verbs: ['*']` sur `services`, `jobs` et `statefulsets` **à l'échelle du cluster**
permet de créer un pod dans n'importe quel namespace. Un opérateur compromis devient donc un
chemin d'élévation vers le cluster entier — d'autant plus qu'il tourne en root (SEC-4).

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

### QUAL-1 — Moyen — `.editorconfig` ne fixe pas `end_of_line`

Le fichier pose `insert_final_newline` et `charset` (lignes 22-23) mais pas `end_of_line`.
`dotnet format` se rabat donc sur le défaut de plateforme : CRLF sous Windows, LF en CI Linux. Un
même arbre peut être conforme d'un côté et fautif de l'autre.

Ce n'est pas théorique : c'est la cause racine de violations WHITESPACE rencontrées en local
pendant le renommage, invisibles pour la CI. **Correctif** : ajouter `end_of_line = crlf`.

### QUAL-2 — Moyen — La couverture est mesurable mais jamais mesurée

`coverlet.collector` 6.0.4 est référencé (`Sirocco.UnitTests.csproj:20`), mais **aucune étape de
CI ne collecte ni ne publie de couverture** : aucun `--collect` dans les workflows. 750 tests
verts sans savoir quelles branches sont exercées — et c'est précisément ce qui permettrait de
transformer le « 187/187 fichiers nommés » ci-dessus en une vraie mesure.

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

**Les quatre sont traités** (23 août 2026, non committés).

| # | Pourquoi maintenant | État |
|---|---|---|
| SEC-3 | Changer le format d'empreinte après que quelqu'un a noté la sienne serait cassant | ✅ SHA-256 + refus au démarrage d'une empreinte SHA-1 |
| FONC-1 | C'est la première commande que tape un nouvel arrivant, et elle échouait | ✅ 5 formes vérifiées sur le binaire |
| GOUV-1 | Un dépôt public sans canal de signalement, sur un outil dual-use | ✅ `SECURITY.md` |
| SEC-2 | Un utilisateur pouvait committer ses jetons dès le premier usage du convertisseur | ✅ rédaction + report, prouvé par conversion réelle |

Barrière repassée après correction : build 0/0, **753 tests** verts, `dotnet format` à 0 violation
sur la solution **et** sur les deux projets du harnais, DocFX 0 avertissement.

**Ensuite, par ordre de gain** : SEC-1 (le plus important sur le fond, mais il change un
comportement par défaut — mieux vaut le faire dans une version qui l'annonce), SEC-4, QUAL-1,
QUAL-2, puis le reste.

**À ne pas traiter** : SEC-5, SEC-6 et SEC-7 décrivent des frontières de confiance assumées. Ils
demandent une phrase de documentation, pas du code.

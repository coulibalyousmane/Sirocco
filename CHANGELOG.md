# Journal des modifications

Toutes les modifications notables de Sirocco. Le format suit
[Keep a Changelog](https://keepachangelog.com/fr/1.1.0/), et le versionnement
[SemVer 2.0.0](https://semver.org/lang/fr/) — voir
[Versionnement, compatibilité et support](docs/projet/versionnement.md) pour ce que chaque paquet
promet, et à quel niveau.

Une règle propre à un outil de mesure, énoncée là parce que c'est ici qu'elle se lit : **quand une
version corrige un biais de mesure, la valeur rapportée pour la même cible peut changer.** Ce n'est
pas une rupture de compatibilité, c'est la correction d'un résultat faux — et l'effet attendu sur les
chiffres est écrit dans l'entrée correspondante, sous « Corrigé ».

## [0.1.0] — à paraître

Première version publiée. La ligne de version est déjà figée dans `Directory.Build.props`, et
`release.yml` refuse de publier si le tag `vX.Y.Z` en diverge ; il ne manque que le tag.

### Ajouté

- Moteur d'injection à **modèle ouvert** (débit cible) et à **modèle fermé** sous quatre formes :
  effectif fixe à durée, montée d'utilisateurs par paliers, itérations par utilisateur, itérations
  partagées ; plus un bridage de débit applicable par-dessus n'importe lequel.
- **Mesure de la dette d'ordonnancement** publiée à côté du temps de service, sur chaque étape et en
  série temporelle — la grandeur qui distingue un système sain d'un système saturé dont l'outil
  n'aurait rien vu.
- Scénarios **déclaratifs** (YAML : corrélation, jeux de données, checks, groupes, étiquettes,
  métriques personnalisées, temps de réflexion) et **scriptés** (Roslyn).
- Protocoles intégrés : HTTP, WebSocket, et gRPC dans ses quatre modes.
- **Contrat de plugin** avec résolution depuis un paquet NuGet, dépendances transitives incluses,
  isolation par `AssemblyLoadContext` et vérification de signature ; cinq extensions de référence
  (SQL, SSE, MQTT, GraphQL, navigateur/Web Vitals).
- Convertisseurs **HAR, OpenAPI, Postman** et un **proxy enregistreur**.
- Rapports : tableau texte, HTML, JSON, série temporelle, distribution, courbe de dette, tableau de
  bord rafraîchi en direct, export Prometheus/OpenTelemetry, seuils CI/CD et comparaison entre tirs.
- **Mode distribué** maître/workers avec fusion d'histogrammes exacte, secret partagé exigé,
  épinglage TLS, reprise sur perte d'un worker ; **opérateur Kubernetes** (`TestRun`) avec
  autoscaling des workers selon le débit visé.
- `sirocco --version`, qui rapporte la version, le commit dont elle est issue, le runtime et le
  système — à joindre à tout rapport de bug.
- Paquets de symboles (`.snupkg`) et SourceLink sur les dix paquets publiés : le code d'un paquet
  publié est retrouvable au commit exact.

### Corrigé

- **Centiles faussés en modèle tiré.** La file de jetons valait `max(vus × 2, 64)`, donc 64 dès que
  l'effectif était petit ; les ordonnanceurs tirés horodatant chaque jeton à l'émission, l'attente en
  file entrait dans `ResponseTicks`. Effet sur les chiffres : un tir `--vus 1 --duration 10s` sur un
  scénario d'une seconde par itération rapportait 74 itérations en 71,8 s avec 63 s de dette ; après
  correction, 11 itérations en 12,5 s et 4,1 s de dette. Le modèle ouvert conserve l'ancien défaut,
  sa file devant absorber une rafale.
- **Courbe de dette monotone par construction.** La dette d'ordonnancement était la seule grandeur
  non fenêtrée : la portée glissante rendait un maximum cumulé, si bien que la colonne « dette max »
  de la série temporelle et la courbe du rapport HTML ne pouvaient jamais redescendre. Un pic de
  démarrage y restait affiché jusqu'à la fin du tir, indistinguable d'une saturation en cours. Effet
  sur les chiffres : sur un tir de 20 s à 4 utilisateurs virtuels, la colonne affichait 363 ms de la
  première à la dernière ligne ; elle retombe désormais à 105 ms dès que le pic sort de la fenêtre.
  Les portées cumulative et distribuée sont inchangées — un maximum historique y est le bon
  comportement.

### Sécurité

- Le control plane distribué **exige** un secret partagé au démarrage au lieu de démarrer ouvert.
- Les scénarios générés depuis un HAR ne recopient plus les jetons en clair.
- Épinglage TLS en SHA-256, avec refus au démarrage d'une empreinte SHA-1.
- Les quatre images conteneur tournent sous un utilisateur non privilégié.
- `ClusterRole` de l'opérateur réduit aux verbes réellement exercés.
- Les plugins sont chargés dans un `AssemblyLoadContext` isolé, signature vérifiée.
- Un scénario déclaratif ne lit une variable d'environnement que si l'opérateur l'autorise
  explicitement (`--allow-env`), le refus étant le défaut.

Le détail de chacun de ces points, avec la commande ou la lecture de code qui l'établit, est dans
[AUDIT.md](AUDIT.md) et [AUDIT-MATURITE.md](AUDIT-MATURITE.md).

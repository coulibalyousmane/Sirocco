# Roadmap concurrentielle

> État au 6 août 2026, après l'étape 21. Ce document est **prospectif** : il traite de ce qui
> manque à Tempest pour exister face aux outils établis. Pour ce qui est déjà fait, voir
> [État d'avancement](README.md#état-davancement) dans le README.

Tempest tient 100 000 requêtes par seconde sur une machine, avec une mesure de latence plus
honnête que la plupart de ses concurrents, 299 tests unitaires et une CI verte. Mais personne
ne peut l'installer, personne ne peut écrire un scénario réaliste avec, et il n'a jamais tourné
ailleurs que sur la machine de son auteur.

**L'écart avec les leaders est à 70 % un problème de distribution, pas de moteur.**

## Le paysage concurrentiel

Trois concurrents comptent réellement. **k6** (Grafana) domine par l'écosystème et l'intégration.
**Gatling** reste la référence du rapport d'analyse. **NBomber** est le concurrent direct sur
.NET — même terrain, même public, et il a déjà une offre managée (NBomber Studio, ordonnancement
Kubernetes).

Légende : ● solide · ◐ partiel · ○ absent

| Capacité | Tempest | k6 | Gatling | NBomber |
|---|---|---|---|---|
| Modèle de charge ouvert (*arrival rate*) | ● natif, seul modèle | ● executors dédiés | ● `injectOpen` | ● |
| Modèle fermé (utilisateurs concurrents) | ○ | ● | ● `injectClosed` | ● |
| Dette d'ordonnancement mesurée et publiée | ● `Response` + `Service` | ○ | ○ | ○ |
| Scénarios programmables (branchement, boucle) | ◐ C# recompilé, ou YAML linéaire | ● JavaScript/TS | ● DSL Scala/Java/Kotlin | ● C#/F# |
| Jeux de données (CSV, JSON, SQL) | ○ | ● `SharedArray` | ● *feeders* | ● *data feed* |
| Checks, groupes, étiquettes, métriques custom | ○ | ● | ● | ◐ |
| Rapport avec séries temporelles | ◐ tableau HTML ; temporel via Prometheus | ● Grafana natif | ● référence du marché | ● HTML + temps réel |
| Mode distribué | ● fusion d'histogrammes exacte | ● `k6-operator` | ◐ Enterprise | ● Studio / K8s |
| Installation en une commande | ○ cloner et compiler | ● brew, apt, docker | ● bundle, maven | ● NuGet |
| Écosystème d'extensions communautaire | ○ | ● `xk6` | ◐ | ◐ plugins officiels |
| Conversion HAR / OpenAPI / Postman | ○ | ● | ● *recorder* proxy | ○ |
| Test navigateur (Web Vitals) | ○ | ● k6 browser | ◐ Enterprise | ○ |

## Le différenciateur réel n'est pas celui qu'on croit

En vérifiant l'état du marché pour ce document, un point s'est révélé faux : **k6, Gatling et
JMeter proposent tous un modèle de charge ouvert** — respectivement `constant-arrival-rate`,
`injectOpen`, et l'Open Model Thread Group (JMeter 5.5+). L'argument « Tempest corrige le
*coordinated omission*, contrairement aux autres » est donc **faux** et ne doit plus être formulé
ainsi : il se ferait démonter immédiatement.

Ce qui reste vrai et défendable : un modèle ouvert *évite* le biais au niveau de l'injection
**tant que l'injecteur tient la cadence**. Dès qu'il sature, l'écart réapparaît — et aucun des
trois ne le montre. Tempest est le seul à publier `Response` (corrigé) et `Service` (brut) côte à
côte, plus la dette d'ordonnancement maximale. **L'écart entre les deux est la mesure du problème
résiduel.**

Le positionnement juste n'est donc pas « nous évitons le biais » mais **« nous sommes le seul
outil qui vous dit quand vos propres chiffres ne sont plus fiables »**. C'est vérifiable,
démontrable par un benchmark comparatif, et personne d'autre ne le revendique.

## Les huit phases

Les phases 1 à 3 sont séquentielles : chacune est bloquée par la précédente. Les phases 4 à 6
peuvent se paralléliser une fois la 3 terminée. Les phases 7 et 8 supposent des utilisateurs
réels — les lancer avant serait construire pour personne.

### Phase 1 — Rendre Tempest installable

*Effort faible · impact existentiel*

Tant qu'il faut cloner un dépôt privé et compiler une solution pour lancer un tir, Tempest n'a pas
d'utilisateurs possibles, quelle que soit la qualité du moteur. C'est le seul point de cette liste
qui bloque littéralement tout le reste, et c'est aussi le moins cher à traiter.

- ~~**Une vraie CLI**~~ — fait (`Tempest.Cli`, voir [Interface de ligne de
  commande](README.md#interface-de-ligne-de-commande)) : `tempest run scenario.yaml --rps 50
  --duration 30s`, options qui priment sur la configuration. Le modèle fermé (`--vus`) suit la
  phase 3 ; en attendant, le profil se pilote en débit (`--rps` ou `--from-rps`/`--to-rps`).
- ~~**Packaging `dotnet tool`**~~ — fait (`PackAsTool`, commande `tempest`, voir
  [Installation](README.md#installation)) : `dotnet tool install -g Tempest.Cli` fonctionne
  aujourd'hui depuis une source locale ou un flux privé, faute de publication sur nuget.org (le
  dépôt reste privé — bullet suivant).
- ~~**Binaires autonomes**~~ — fait pour Windows, Linux et macOS (x64 et arm64), voir [Binaires
  autonomes](README.md#binaires-autonomes) : self-contained, fichier unique, workflow de release
  GitHub prêt (`vX.Y.Z` poussé), pas encore déclenché. Native AOT essayé réellement et
  abandonné : `YamlDotNet.DeserializerBuilder` et une désérialisation JSON par réflexion dans
  `ScenarioDefinitionLoader` échouent la compilation AOT (`IL3050`/`IL2026`) — le graphe de
  dépendances actuel ne le permet pas sans réécrire ce pipeline.
- **Paquets NuGet bibliothèque** — `Tempest.Domain` et `Tempest.Scenarios`, pour écrire des
  scénarios en C# depuis un projet xUnit, comme le permet NBomber. **Pas encore fait** — traité
  après le bullet suivant plutôt qu'avant, par choix explicite plutôt que par oubli.
- ~~**Rendre le dépôt public**~~ — licence ([Apache License 2.0](LICENSE), celle de k6 et
  Gatling), README d'accueil et démarrage rapide en trois commandes faits. Changer la visibilité
  du dépôt sur GitHub reste un geste que seul le propriétaire du dépôt peut faire (Réglages →
  Danger Zone → Change visibility) — non automatisable depuis cet environnement (`gh` CLI absent,
  et l'installer sans qu'on le demande serait aller plus loin que ce qui a été convenu).

### Phase 2 — Des scénarios qu'on peut réellement écrire

*Effort élevé · impact décisif*

Le format déclaratif actuel joue une séquence linéaire de requêtes HTTP. Aucun test de charge réel
ne ressemble à ça : il faut des données variables, des branchements, des assertions qui
n'interrompent pas le tir, et des dimensions pour découper les résultats. C'est le plus gros trou
fonctionnel.

- **Jeux de données** — alimenter un scénario depuis CSV/JSON/SQL avec des stratégies d'itération
  (circulaire, aléatoire, unique par utilisateur virtuel). Sans ça, tous les utilisateurs virtuels
  envoient les mêmes identifiants. Le pool généré par Bogus dans `DynamicCheckoutWorkflow` ne
  couvre pas ce besoin : il est codé en dur dans un scénario précis.
- **Checks** — assertions qui enregistrent un échec logique sans faire échouer la requête,
  distinctes des erreurs de transport.
- **Groupes et étiquettes** — hiérarchie d'étapes et découpage par dimension (`endpoint`, `région`,
  `version`) dans le rapport.
- **Métriques personnalisées** — compteur, jauge, taux, tendance, alimentés depuis le scénario et
  agrégés comme les métriques natives.
- **Temps de réflexion et rythme** — pauses configurables entre étapes, indispensables pour simuler
  un parcours utilisateur crédible.

> **Décision structurante à trancher ici.** Enrichir le format déclaratif (conditions, boucles)
> **ou** embarquer un moteur de script. Trois options réelles : Roslyn (C# scripté, cohérent avec
> l'écosystème mais lent à démarrer), un moteur JavaScript type Jint (attire les utilisateurs k6,
> mais un runtime de plus à porter), ou pousser le déclaratif au maximum (plus sûr, plafonne vite).
> Ce choix conditionne les phases 5 et 6 — à ne pas repousser par accumulation.

### Phase 3 — Modèles de charge complets

*Effort moyen · impact fort*

Tempest ne sait piloter qu'un débit cible. C'est le bon modèle par défaut, mais beaucoup de besoins
réels s'expriment autrement — « exactement 50 utilisateurs simultanés », « 1 000 itérations
réparties », « ce scénario à 10 RPS pendant que celui-là monte en charge ». Refuser le modèle fermé
par purisme coûterait des utilisateurs.

- **Modèle fermé** à côté du modèle ouvert, avec une mise en garde explicite dans le rapport quand
  il est utilisé — cohérent avec la posture d'honnêteté de mesure.
- **Exécuteurs multiples** — utilisateurs constants, montée d'utilisateurs, itérations partagées,
  itérations par utilisateur.
- **Scénarios concurrents** dans un même tir, chacun avec son profil, ses étiquettes et ses seuils.
- **Bridage** — plafond de débit global indépendant du profil.

### Phase 4 — Un rapport au niveau de Gatling

*Effort moyen · impact fort*

Le rapport de Gatling est la raison pour laquelle beaucoup d'équipes le choisissent. Celui de
Tempest est un tableau statique : il donne l'état final, jamais la trajectoire. Or c'est la
trajectoire — le moment où les centiles décrochent — qui explique une dégradation.

- **Séries temporelles** — centiles dans le temps, débit, utilisateurs actifs, taux d'erreur, sur
  un même axe de temps.
- **Distribution des temps de réponse** en histogramme : la donnée que `LatencyHistogram` possède
  déjà (ses paniers) et que le rapport n'expose pas.
- **Courbe de dette d'ordonnancement** superposée — le graphe que personne d'autre ne peut
  produire, à mettre en avant.
- **Interface web temps réel** pendant le tir, au-delà du JSON de `/report/live`.

### Phase 5 — Réduire le coût du premier scénario

*Effort moyen · impact sur l'adoption*

Écrire un premier scénario à la main est le moment où l'on abandonne un outil. Les convertisseurs
sont le levier d'adoption le moins cher du marché : on part d'un trafic déjà capturé plutôt que
d'une page blanche.

- **HAR vers scénario** — un export du navigateur devient un tir jouable.
- **OpenAPI vers scénario** — squelette généré à partir de la spécification d'une API.
- **Collection Postman vers scénario.**
- **Proxy enregistreur** à la Gatling, si les convertisseurs rencontrent leur public.

### Phase 6 — Élargir sans tout porter soi-même

*Effort élevé · impact structurel*

k6 n'a pas écrit ses dizaines de protocoles : il a ouvert `xk6` et la communauté l'a fait. Porter
seul SQL, Kafka, MQTT, AMQP et le reste est un puits sans fond. Le modèle d'extension doit venir
**avant** les protocoles, pas après — chaque protocole écrit dans le cœur est une dette permanente.

- **Contrat de plugin** stable — un protocole tiers s'ajoute sans toucher au cœur, en s'appuyant
  sur `IWorkflow` et `StepScope`, déjà agnostiques du protocole.
- **Chargement dynamique** d'extensions et résolution depuis NuGet.
- **Protocoles de référence** écrits comme extensions pour valider le contrat : SQL, SSE, MQTT,
  GraphQL.
- **Guide d'écriture d'extension** — sans documentation, un modèle de plugin reste théorique.

### Phase 7 — Échelle cloud-native

*Effort moyen · impact conditionnel*

Le mode distribué existe et fonctionne, mais il se déploie à la main via `docker-compose`. À partir
de quelques dizaines de workers, ça ne tient plus. À ne lancer que lorsque des utilisateurs réels
atteignent ce plafond.

- **Opérateur Kubernetes** — une ressource `TestRun`, des workers créés et détruits automatiquement.
- **TLS sur le control plane** — tout est en clair aujourd'hui ; le secret partagé de l'étape 15
  protège l'authentification, pas la confidentialité.
- **Autoscaling** des workers selon le débit cible.
- **Reprise sur perte d'un worker** en cours de tir, aujourd'hui non gérée : le maître attend
  indéfiniment un rapport qui ne viendra jamais.

### Phase 8 — Prouver publiquement le différenciateur

*Effort faible · impact sur la notoriété*

Un outil technique inconnu ne se diffuse pas par ses fonctionnalités mais par une démonstration
qu'on ne peut pas ignorer. Tempest en a une à disposition, et elle est reproductible.

- **Benchmark comparatif publié** — même cible saturée, même profil, Tempest contre k6, Gatling et
  NBomber, montrant l'écart entre latence rapportée et latence subie quand l'injecteur décroche.
  Dépôt reproductible, méthode ouverte.
- **Article de fond** sur la dette d'ordonnancement résiduelle : le sujet a un public (SRE,
  ingénieurs performance) et personne ne l'occupe.
- **Site de documentation** avec exemples exécutables.
- **Décider de l'offre managée** — seulement si l'adoption open source la justifie.

## Trois façons de perdre du temps

**Courir après la parité avec k6.** Grafana finance une équipe entière. Rattraper l'étendue
fonctionnelle de k6 fonctionnalité par fonctionnalité est perdu d'avance. La seule stratégie viable
est d'être meilleur sur un axe étroit et défendable, puis d'élargir.

**Construire le SaaS trop tôt.** C'est le réflexe qui tue les outils open source naissants :
monétiser avant d'avoir des utilisateurs. Tant que personne n'utilise Tempest gratuitement,
personne ne le paiera.

**Ajouter des protocoles avant le modèle d'extension.** La phase 6 place volontairement le contrat
de plugin avant les protocoles qu'il doit accueillir.

## Recommandation

**La phase 1 en entier, avant tout le reste.** Quelques jours de travail, aucune difficulté
technique, et elle transforme un projet personnel en logiciel utilisable. Tout ce qui a été
construit en vingt-et-une étapes — la fusion d'histogrammes exacte, les quatre modes gRPC, le mode
distribué sécurisé — n'a aucune valeur tant que l'installation demande de cloner un dépôt privé.

Ensuite **la décision de la phase 2** : script ou déclaratif enrichi. C'est le seul choix de cette
roadmap difficile à défaire ensuite, et il conditionne quatre phases sur huit.

Le reste peut attendre des retours d'utilisateurs réels. Construire les phases 4 à 8 sans eux,
c'est deviner.

## Sources

Consultées le 6 août 2026 :

- [k6 2.0 release — Grafana Labs](https://grafana.com/blog/k6-2-0-release/)
- [k6 Extensions xk6 Complete Reference 2026](https://qaskills.sh/blog/k6-extensions-xk6-complete-reference)
- [JMeter vs k6 vs Gatling 2026 — modèles ouvert et fermé](https://qaskills.sh/blog/jmeter-vs-k6-vs-gatling-2026)
- [Gatling — rapports statiques HTML](https://docs.gatling.io/reference/stats/reports/oss/)
- [NBomber — framework distribué .NET](https://nbomber.com/)
- [Coordinated Omission — Red Hat Performance](https://redhatperf.github.io/post/coordinated-omission/)

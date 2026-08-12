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
| Modèle fermé (utilisateurs concurrents, itérations) | ● `--vus`/`--vus-from`/`--vus-to`/`--iterations`/`--iterations-per-vu`, mise en garde explicite | ● | ● `injectClosed` | ● |
| Dette d'ordonnancement mesurée et publiée | ● `Response` + `Service` | ○ | ○ | ○ |
| Scénarios programmables (branchement, boucle) | ◐ C# recompilé, ou YAML linéaire | ● JavaScript/TS | ● DSL Scala/Java/Kotlin | ● C#/F# |
| Scénarios concurrents dans un même tir | ● `Tempest:Scenarios`, isolement complet par scénario | ● plusieurs `scenario` par script | ● plusieurs `scenario()` par simulation | ● |
| Jeux de données (CSV, JSON, SQL) | ◐ CSV/JSON, pas SQL | ● `SharedArray` | ● *feeders* | ● *data feed* |
| Checks, groupes, étiquettes, métriques custom, rythme | ● checks, groupes, étiquettes, métriques custom, temps de réflexion | ● | ● | ◐ |
| Rapport avec séries temporelles | ◐ tableau HTML ; temporel via Prometheus | ● Grafana natif | ● référence du marché | ● HTML + temps réel |
| Mode distribué | ● fusion d'histogrammes exacte | ● `k6-operator` | ◐ Enterprise | ● Studio / K8s |
| Installation en une commande | ○ cloner et compiler | ● brew, apt, docker | ● bundle, maven | ● NuGet |
| Écosystème d'extensions communautaire | ○ | ● `xk6` | ◐ | ◐ plugins officiels |
| Conversion HAR / OpenAPI / Postman | ◐ HAR seul | ● | ● *recorder* proxy | ○ |
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
- ~~**Paquets NuGet bibliothèque**~~ — fait, voir [Paquets NuGet](README.md#paquets-nuget) :
  élargi de `Tempest.Domain` et `Tempest.Scenarios` (le texte initial de ce bullet) à
  `Tempest.Application` et `Tempest.Infrastructure` aussi — sans le moteur ni la chaîne de
  mesure, un projet externe pouvait écrire un scénario mais pas le lancer, contrairement à
  NBomber, cité en référence quelques lignes plus haut. Vérifié par un vrai tir depuis un projet
  xUnit externe qui ne référence que ces quatre paquets (aucun `ProjectReference` vers ce dépôt).
  Pas encore publiés sur nuget.org — le dépôt reste privé (bullet précédent, maintenant fait :
  seule la visibilité GitHub reste un geste manuel).
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

- ~~**Jeux de données**~~ — fait, voir [Jeux de données](README.md#jeux-de-données) : `DataSet`
  (`Tempest.Domain.Data`) + `DataSetLoader` CSV/JSON (`Tempest.Scenarios.Data`), trois stratégies
  (circulaire, aléatoire, unique par utilisateur virtuel), accessible depuis le format déclaratif
  (`{{jeu.colonne}}`) et depuis un scénario scripté. Réduit à CSV/JSON pour ce premier tour — SQL
  écarté : une source de données arbitraire (requête, pool de connexions, ré-exécution par tir)
  dépasse le scope d'un chargement de fichier unique et mériterait son propre chantier plutôt
  qu'un troisième format ajouté en hâte à `DataSetLoader`. Vérifié par de vrais tirs.
- ~~**Checks**~~ — fait, voir [Checks](README.md#checks) : `CheckRule` (même vocabulaire
  Regex/XPath/JsonPath que l'extraction), section `checks` par étape. Chaque check devient sa
  propre étape du rapport — reutilise `StepId`/`StepScope` tels quels, aucun changement dans
  `Tempest.Application`/`Tempest.Infrastructure`. Une assertion qui échoue ne fait jamais échouer
  la requête HTTP dont elle dérive, mais compte comme n'importe quelle étape pour l'issue de
  l'itération. Sans effet sur les scénarios scriptés, qui pouvaient déjà publier ce genre
  d'assertion directement via `StepRegistry`/`StepScope`. Vérifié par de vrais tirs.
- ~~**Groupes et étiquettes**~~ — fait, voir [Groupes et étiquettes](README.md#groupes-et-étiquettes) :
  un `group` par étape (préfixé au nom pour former le nom qualifié, réutilise `StepId` tel quel —
  couvre la dimension `endpoint`) et des `tags` de scénario (métadonnée de tir reportée dans
  l'en-tête du rapport — couvre `région`/`version`, qui varient par tir, pas par requête). Rendu
  délibérément plat : pas de hiérarchie visuelle (indentation, sous-total par groupe) déduite du
  nom, ni de tags par requête façon k6 — les deux exigeraient de faire d'une valeur choisie à
  l'exécution une clé d'agrégation à part entière, jusque dans `StepAccumulator`/`MetricResult`.
  Limite : étiquettes non propagées au rapport fusionné en mode distribué. Vérifié par un vrai tir.
- ~~**Métriques personnalisées**~~ — fait, voir [Métriques personnalisées](README.md#métriques-personnalisées) :
  `CustomMetricKind` (compteur/jauge/taux/tendance), `CustomMetricRegistry`/`MetricRule` (même
  vocabulaire Regex/XPath/JsonPath que les checks), section `metrics` par étape. Première
  fonctionnalité de cette phase à ne pas pouvoir réutiliser `StepId`/`StepAccumulator` tels
  quels — une valeur métier arbitraire n'est pas une durée de requête — d'où une seconde chaîne
  d'agrégation parallèle (canal borné, accumulateur, agrégateur), même discipline « consommateur
  unique » que la chaîne native. Rendue dans le rapport et dans Prometheus. Limites : pas de
  centiles pour la tendance (`LatencyHistogram` est bâti pour une durée non négative bornée), pas
  de fenêtre glissante, pas de fusion inter-workers en mode distribué. Vérifié par un vrai tir.
- ~~**Temps de réflexion et rythme**~~ — fait, voir [Temps de réflexion et rythme](README.md#temps-de-réflexion-et-rythme) :
  `ThinkTimeDefinition` (durée fixe ou plage tirée uniformément), `thinkTime`/`thinkTimeMax` par
  étape du format déclaratif. Aucun changement dans le moteur : une pause n'est qu'un `Task.Delay`
  hors de la portée de mesure de l'étape, que le modèle ouvert absorbe nativement en dette
  d'ordonnancement — exactement comme le ferait une reponse HTTP lente. Vérifié par de vrais
  tirs : une pause fixe fait chuter le débit effectif d'un seul utilisateur virtuel exactement à
  la valeur attendue, sans jamais affecter la latence brute rapportée pour l'étape HTTP.

> **Décision structurante tranchée et mise en œuvre : Roslyn (C# scripté).** Voir [Scénarios
> scriptés](README.md#scénarios-scriptés-roslyn) — `ScriptedWorkflowLoader`/`WorkflowFileLoader`,
> vérifiés par un vrai tir (`scenarios/scripted-checkout.csx`). Limite restante : mode distribué
> non pris en charge pour ce format, `WorkerCoordinator` reste câblé sur le déclaratif seul.
>
> Un scénario scripté devient un
> `IWorkflow` compilé à la volée (`Microsoft.CodeAnalysis.CSharp.Scripting`) : zéro couche
> d'interop à concevoir, réutilisation directe de `Tempest.Domain`/`Application`/`Infrastructure`,
> déjà publiés en paquets NuGet (voir [Paquets NuGet](README.md#paquets-nuget)) — un script
> scénario et une bibliothèque scénario deviennent le même code, à la compilation près. L'AOT
> était déjà hors de portée pour d'autres raisons (`YamlDotNet`/JSON par réflexion dans
> `ScenarioDefinitionLoader`, voir [Binaires autonomes](README.md#binaires-autonomes)) : Roslyn
> n'aggrave donc pas une limite qui n'existait pas encore.
>
> Écarté : un moteur JavaScript type Jint aurait mieux attaqué la position dominante de k6 (mêmes
> scripts, syntaxe familière), mais au prix d'un runtime de plus à maintenir dans la durée et
> d'une couche de binding JS↔C# entière à concevoir et tester, qui n'existe nulle part
> aujourd'hui. Pousser le déclaratif plus loin (conditions, boucles en YAML/JSON) restait la
> voie la plus sûre à court terme, mais c'est exactement l'impasse qui a motivé cette phase :
> un YAML avec des boucles devient vite un langage de programmation mal conçu.
>
> **Implications pour les phases 5 et 6** : les convertisseurs (HAR/OpenAPI/Postman) généreront
> du C#, pas du YAML ni du JS — plus proche d'une génération de client typé que d'un simple
> mapping de requêtes. Le contrat de plugin de la phase 6 n'a besoin d'aucune couche de binding
> supplémentaire : un protocole tiers reste un assemblage .NET ordinaire, chargé dynamiquement.
> Le format déclaratif existant (`DeclarativeWorkflow`) n'est pas remplacé — il reste la voie la
> plus sûre pour un scénario linéaire sans logique, les deux coexistent.

### Phase 3 — Modèles de charge complets

*Effort moyen · impact fort*

Tempest ne sait piloter qu'un débit cible. C'est le bon modèle par défaut, mais beaucoup de besoins
réels s'expriment autrement — « exactement 50 utilisateurs simultanés », « 1 000 itérations
réparties », « ce scénario à 10 RPS pendant que celui-là monte en charge ». Refuser le modèle fermé
par purisme coûterait des utilisateurs.

- ~~**Modèle fermé**~~ — fait, voir [Modèle fermé](README.md#modèle-fermé) : `--vus <n>
  --duration <d>` fait tourner exactement N utilisateurs virtuels sans aucune pause imposée,
  `ClosedModelScheduler` (`ILoadScheduler`) prenant la place de `CoordinatedRateLimiter` derrière
  le même moteur. Mise en garde explicite dans le rapport (`LoadTestReport.ClosedModel`) : sans
  échéancier théorique, il n'y a pas de correction du *coordinated omission* en modèle fermé — les
  chiffres ne sont jamais comparables à un tir en modèle ouvert. Limite : mode distribué non pris
  en charge pour ce modèle. Vérifié par un vrai tir.
- ~~**Exécuteurs multiples**~~ — fait. Utilisateurs constants (voir ci-dessus) ; montée
  d'utilisateurs, voir [Montée d'utilisateurs](README.md#montée-dutilisateurs) (`--vus-from`/
  `--vus-to`, `RampingVirtualUserPool`) ; itérations partagées et itérations par utilisateur, voir
  [Itérations partagées et itérations par
  utilisateur](README.md#itérations-partagées-et-itérations-par-utilisateur) (`--iterations`,
  `--vus <n> --iterations-per-vu <k>`, `IterationCountScheduler`). Les quatre partagent la même
  mise en garde de rapport, faute d'échéancier théorique à comparer.
- ~~**Scénarios concurrents**~~ — fait, voir [Scénarios concurrents](README.md#scénarios-concurrents) :
  `Tempest:Scenarios` dans un `appsettings.json` (comme `Tempest:RampVus`, pas d'équivalent CLI
  plat), chaque scénario avec son propre profil/modèle de charge, ses étiquettes et ses seuils.
  Chaque scénario construit sa propre chaîne de mesure à la main (pas via le conteneur
  d'injection de dépendances, qui ne loge qu'un singleton par type) : c'est cet isolement complet
  qui garantit qu'aucune mesure ne se mélange entre deux scénarios, même s'ils partagent un nom
  d'étape. Limites : mode distribué non pris en charge, `/report/live` et `/metrics` non
  alimentés — seuls `/report`, `/report.html` et `/thresholds` le sont, une fois le tir terminé.
  Vérifié par un vrai tir à deux scénarios concurrents.
- ~~**Bridage**~~ — fait, voir [Bridage](README.md#bridage) : `RateCappedScheduler`
  (`Tempest.Application.Execution`), décorateur d'`ILoadScheduler` qui retarde la transmission des
  jetons plutôt que de réécrire leur échéance planifiée — le retard qu'il impose se mesure donc
  comme une dette d'ordonnancement ordinaire, pas comme un cas particulier masqué. `--max-rps`
  n'est un cinquième modèle mutuellement exclusif avec rien : il compose avec tous les modèles
  précédents et avec `Tempest:Scenarios` (`ScenarioOptions.MaxRequestsPerSecond`, avec repli sur le
  plafond global). **Clôt entièrement la phase 3.** Vérifié par de vrais tirs : modèle ouvert à
  100 RPS ramené à 20, modèle fermé à 176 RPS naturels ramené à 15, plafond par scénario et repli
  global tous deux respectés dans un tir à deux scénarios concurrents.

### Phase 4 — Un rapport au niveau de Gatling

*Effort moyen · impact fort*

Le rapport de Gatling est la raison pour laquelle beaucoup d'équipes le choisissent. Celui de
Tempest est un tableau statique : il donne l'état final, jamais la trajectoire. Or c'est la
trajectoire — le moment où les centiles décrochent — qui explique une dégradation.

- ~~**Séries temporelles**~~ — fait, voir [Série temporelle](README.md#série-temporelle) :
  `TimeSeriesRecorder` relève périodiquement centiles, débit, utilisateurs actifs (nouvelle
  `ActiveVirtualUserGauge`) et taux d'erreur sur un même axe de temps, gardés dans
  `LoadTestReport.TimeSeries`. Limites : non alimentée pour les scénarios concurrents, rendue en
  table pour l'instant — la visualisation en courbe reste aux deux bullets suivants.
- ~~**Distribution des temps de réponse**~~ — fait : `StepStatistics.ResponseHistogram` expose les
  paniers bruts de `LatencyHistogram` (déjà là, jamais publiés), regroupés par octave — le
  découpage natif de l'histogramme, pas une résolution inventée pour l'occasion — et rendus en
  barres SVG, une distribution par étape, dans `ToHtml`.
- ~~**Courbe de dette d'ordonnancement**~~ — fait : `LoadTestReport.ToHtml` superpose désormais
  débit et dette d'ordonnancement sur le même axe de temps (`LoadTestReport.TimeSeries`), chacun à
  l'échelle de son propre maximum, en SVG inline — le graphe que personne d'autre ne peut produire.
- ~~**Interface web temps réel**~~ — fait : `/report/live.html` (`TempestHostOptions.LiveDashboardRefreshSeconds`,
  3 s par défaut) rend le même rapport HTML sur la fenêtre glissante, avec une balise
  `<meta http-equiv="refresh">` qui le recharge seul pendant le tir — au-delà du JSON brut de
  `/report/live`. **Clôt entièrement la phase 4.**

### Phase 5 — Réduire le coût du premier scénario

*Effort moyen · impact sur l'adoption*

Écrire un premier scénario à la main est le moment où l'on abandonne un outil. Les convertisseurs
sont le levier d'adoption le moins cher du marché : on part d'un trafic déjà capturé plutôt que
d'une page blanche.

- ~~**HAR vers scénario**~~ — fait, voir [Convertisseur HAR](README.md#convertisseur-har) :
  `tools/Tempest.HarConvert` traduit un export du navigateur en scénario **scripté** C# (`.csx`),
  conformément à la décision structurante ci-dessus — jouable sans aucun câblage supplémentaire.
  Actifs statiques ignorés par extension, hôte cible retenu par fréquence (pas par ordre
  d'apparition — un bug réel de la première version, trouvé et corrigé en vérifiant un vrai HAR
  où un appel tiers sans extension reconnue precédait le premier appel à la cible). Limite
  documentée : authentification/cookies capturés à revoir manuellement (valeurs de session
  probablement expirées), corps multipart non pris en charge. Vérifié par un vrai tir : HAR
  reconstitué d'un aller-retour réel login/catalogue/checkout contre `Tempest.SampleTarget`,
  mêlé à un actif statique et un hôte secondaire, converti puis exécuté par `tempest run`.
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

**La phase 1 est faite** — CLI, packaging `dotnet tool`, binaires autonomes, paquets NuGet
bibliothèque, licence et démarrage rapide. Un seul geste manuel reste : basculer la visibilité du
dépôt GitHub de privé à public (Réglages → Danger Zone), réservé au propriétaire du dépôt. Tant
qu'il reste privé, rien de ce qui a été construit — la fusion d'histogrammes exacte, les quatre
modes gRPC, le mode distribué sécurisé, l'installation en une commande — n'a de valeur pour
quiconque d'autre que son auteur.

**La décision de la phase 2 est tranchée et le moteur de script existe** : Roslyn (C# scripté),
voir [Scénarios scriptés](README.md#scénarios-scriptés-roslyn). C'était le seul choix de cette
roadmap difficile à défaire une fois pris ; il conditionne les phases 5 et 6, toujours valables
telles que décrites dans la note de la phase 2. **Le contenu de la phase 2 est maintenant
entièrement traité** : jeux de données, checks, groupes/étiquettes, métriques personnalisées et
temps de réflexion sont faits, voir [Jeux de données](README.md#jeux-de-données),
[Checks](README.md#checks), [Groupes et étiquettes](README.md#groupes-et-étiquettes),
[Métriques personnalisées](README.md#métriques-personnalisées) et
[Temps de réflexion et rythme](README.md#temps-de-réflexion-et-rythme).

**La phase 3 est entièrement traitée.** Effectif fixe, montée d'utilisateurs et les deux exécuteurs
par itérations sont faits, voir [Modèle fermé](README.md#modèle-fermé) (`--vus <n> --duration <d>`),
[Montée d'utilisateurs](README.md#montée-dutilisateurs) (`--vus-from <n> --vus-to <n> --duration <d>`)
et [Itérations partagées et itérations par
utilisateur](README.md#itérations-partagées-et-itérations-par-utilisateur) (`--iterations <n>`,
`--vus <n> --iterations-per-vu <k>`) — mise en garde explicite dans le rapport pour les quatre.
[Scénarios concurrents](README.md#scénarios-concurrents) (`Tempest:Scenarios`) fait tourner
plusieurs de ces modèles en parallèle dans le même tir, chacun isolé jusque dans sa propre chaîne
de mesure. [Bridage](README.md#bridage) (`--max-rps`) plafonne le débit réel par-dessus n'importe
lequel de ces modèles, y compris par scénario dans un tir à scénarios concurrents.

**La phase 4 est entièrement traitée.** Voir [Série temporelle](README.md#série-temporelle),
[Distribution des temps de réponse](README.md#distribution-des-temps-de-réponse) et [Tableau de
bord temps réel](README.md#tableau-de-bord-temps-réel) : `TimeSeriesRecorder` relève
périodiquement la trajectoire du tir, gardée dans `LoadTestReport.TimeSeries` ; `ToHtml` la rend
désormais en graphe (débit et dette d'ordonnancement superposés) et ajoute un histogramme par
étape à partir des paniers déjà détenus par `LatencyHistogram` ; `/report/live.html` transforme la
fenêtre glissante déjà servie en JSON par `/report/live` en tableau de bord HTML qui se recharge
seul pendant le tir.

**La phase 5 est engagée : le convertisseur HAR est fait.** Voir [Convertisseur
HAR](README.md#convertisseur-har) : `tools/Tempest.HarConvert` traduit un export du navigateur en
scénario scripté C# (`.csx`), conformément à la décision structurante prise en phase 2 — pas de
YAML/JSON généré. Restent OpenAPI vers scénario, collection Postman vers scénario et le proxy
enregistreur — les phases 6 à 8 peuvent toujours attendre des retours d'utilisateurs réels.

## Sources

Consultées le 6 août 2026 :

- [k6 2.0 release — Grafana Labs](https://grafana.com/blog/k6-2-0-release/)
- [k6 Extensions xk6 Complete Reference 2026](https://qaskills.sh/blog/k6-extensions-xk6-complete-reference)
- [JMeter vs k6 vs Gatling 2026 — modèles ouvert et fermé](https://qaskills.sh/blog/jmeter-vs-k6-vs-gatling-2026)
- [Gatling — rapports statiques HTML](https://docs.gatling.io/reference/stats/reports/oss/)
- [NBomber — framework distribué .NET](https://nbomber.com/)
- [Coordinated Omission — Red Hat Performance](https://redhatperf.github.io/post/coordinated-omission/)

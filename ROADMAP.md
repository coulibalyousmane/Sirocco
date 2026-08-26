# Roadmap concurrentielle

> État au 6 août 2026, après l'étape 21. Ce document est **prospectif** : il traite de ce qui
> manque à Sirocco pour exister face aux outils établis. Pour ce qui est déjà fait, voir
> [État d'avancement](README.md#état-davancement) dans le README.

Sirocco tient 100 000 requêtes par seconde sur une machine, avec une mesure de latence plus
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

| Capacité | Sirocco | k6 | Gatling | NBomber |
|---|---|---|---|---|
| Modèle de charge ouvert (*arrival rate*) | ● natif, seul modèle | ● executors dédiés | ● `injectOpen` | ● |
| Modèle fermé (utilisateurs concurrents, itérations) | ● `--vus`/`--vus-from`/`--vus-to`/`--iterations`/`--iterations-per-vu`, mise en garde explicite | ● | ● `injectClosed` | ● |
| Dette d'ordonnancement mesurée et publiée | ● `Response` + `Service` | ○ | ○ | ○ |
| Scénarios programmables (branchement, boucle) | ◐ C# recompilé, ou YAML linéaire | ● JavaScript/TS | ● DSL Scala/Java/Kotlin | ● C#/F# |
| Scénarios concurrents dans un même tir | ● `Sirocco:Scenarios`, isolement complet par scénario | ● plusieurs `scenario` par script | ● plusieurs `scenario()` par simulation | ● |
| Jeux de données (CSV, JSON, SQL) | ◐ CSV/JSON, pas SQL | ● `SharedArray` | ● *feeders* | ● *data feed* |
| Checks, groupes, étiquettes, métriques custom, rythme | ● checks, groupes, étiquettes, métriques custom, temps de réflexion | ● | ● | ◐ |
| Rapport avec séries temporelles | ◐ tableau HTML ; temporel via Prometheus | ● Grafana natif | ● référence du marché | ● HTML + temps réel |
| Mode distribué | ● fusion d'histogrammes exacte | ● `k6-operator` | ◐ Enterprise | ● Studio / K8s |
| Installation en une commande | ○ cloner et compiler | ● brew, apt, docker | ● bundle, maven | ● NuGet |
| Écosystème d'extensions communautaire | ○ | ● `xk6` | ◐ | ◐ plugins officiels |
| Conversion HAR / OpenAPI / Postman / *recorder* proxy | ● les quatre | ● | ● *recorder* proxy | ○ |
| Test navigateur (Web Vitals) | ○ | ● k6 browser | ◐ Enterprise | ○ |

**L'écosystème d'extensions reste `○` sciemment**, malgré un modèle d'extension entièrement fait
(phase 6) : un écosystème se mesure à des extensions **tierces** publiées, pas à la mécanique qui
les rendrait possibles. Trois prérequis, dont deux sont désormais faits :

1. Un tag `vX.Y.Z` qui publie enfin `Sirocco.Domain` sur nuget.org — sans quoi un tiers ne peut pas
   compiler contre le contrat. **Reste à faire**, et c'est le seul blocage réel : il demande la clé
   d'API nuget.org en secret de dépôt.
2. La résolution des dépendances transitives — **faite le 26 août 2026**. Sans elle, seules des
   extensions sans aucune dépendance étaient distribuables par paquet, ce qui exclut de fait la
   plupart des protocoles réels.
3. Les quatre extensions de référence empaquetées comme premiers exemples publiés — **fait le
   26 août 2026** (étiquette `sirocco-extension`, README par paquet, garde-fou de release porté de
   cinq à neuf paquets).

Le `●` de k6 est une conséquence d'utilisateurs, pas d'implémentation.

## Le différenciateur réel n'est pas celui qu'on croit

En vérifiant l'état du marché pour ce document, un point s'est révélé faux : **k6, Gatling et
JMeter proposent tous un modèle de charge ouvert** — respectivement `constant-arrival-rate`,
`injectOpen`, et l'Open Model Thread Group (JMeter 5.5+). L'argument « Sirocco corrige le
*coordinated omission*, contrairement aux autres » est donc **faux** et ne doit plus être formulé
ainsi : il se ferait démonter immédiatement.

Ce qui reste vrai et défendable : un modèle ouvert *évite* le biais au niveau de l'injection
**tant que l'injecteur tient la cadence**. Dès qu'il sature, l'écart réapparaît — et aucun des
trois ne le montre. Sirocco est le seul à publier `Response` (corrigé) et `Service` (brut) côte à
côte, plus la dette d'ordonnancement maximale. **L'écart entre les deux est la mesure du problème
résiduel.**

Le positionnement juste n'est donc pas « nous évitons le biais » mais **« nous sommes le seul
outil qui vous dit quand vos propres chiffres ne sont plus fiables »**. C'est vérifiable,
démontrable par un benchmark comparatif, et personne d'autre ne le revendique.

## Les huit phases

Les phases 1 à 3 sont séquentielles : chacune est bloquée par la précédente. Les phases 4 à 6
peuvent se paralléliser une fois la 3 terminée. Les phases 7 et 8 supposent des utilisateurs
réels — les lancer avant serait construire pour personne.

### Phase 1 — Rendre Sirocco installable

*Effort faible · impact existentiel*

Tant qu'il faut cloner un dépôt privé et compiler une solution pour lancer un tir, Sirocco n'a pas
d'utilisateurs possibles, quelle que soit la qualité du moteur. C'est le seul point de cette liste
qui bloque littéralement tout le reste, et c'est aussi le moins cher à traiter.

- ~~**Une vraie CLI**~~ — fait (`Sirocco.Cli`, voir [Interface de ligne de
  commande](docs/demarrer/cli.md#interface-de-ligne-de-commande)) : `sirocco run scenario.yaml --rps 50
  --duration 30s`, options qui priment sur la configuration. Le modèle fermé (`--vus`) suit la
  phase 3 ; en attendant, le profil se pilote en débit (`--rps` ou `--from-rps`/`--to-rps`).
- ~~**Packaging `dotnet tool`**~~ — fait (`PackAsTool`, commande `sirocco`, voir
  [Installation](docs/demarrer/installation.md#installation)) : `dotnet tool install -g Sirocco.Cli` fonctionne
  aujourd'hui depuis une source locale ou un flux privé, faute de publication sur nuget.org (le
  dépôt reste privé — bullet suivant).
- ~~**Binaires autonomes**~~ — fait pour Windows, Linux et macOS (x64 et arm64), voir [Binaires
  autonomes](docs/demarrer/installation.md#binaires-autonomes) : self-contained, fichier unique, workflow de release
  GitHub prêt (`vX.Y.Z` poussé), pas encore déclenché. Native AOT essayé réellement et
  abandonné : `YamlDotNet.DeserializerBuilder` et une désérialisation JSON par réflexion dans
  `ScenarioDefinitionLoader` échouent la compilation AOT (`IL3050`/`IL2026`) — le graphe de
  dépendances actuel ne le permet pas sans réécrire ce pipeline.
- ~~**Paquets NuGet bibliothèque**~~ — fait, voir [Paquets NuGet](docs/demarrer/installation.md#paquets-nuget) :
  élargi de `Sirocco.Domain` et `Sirocco.Scenarios` (le texte initial de ce bullet) à
  `Sirocco.Application` et `Sirocco.Infrastructure` aussi — sans le moteur ni la chaîne de
  mesure, un projet externe pouvait écrire un scénario mais pas le lancer, contrairement à
  NBomber, cité en référence quelques lignes plus haut. Vérifié par un vrai tir depuis un projet
  xUnit externe qui ne référence que ces quatre paquets (aucun `ProjectReference` vers ce dépôt).
  Pas encore publiés sur nuget.org — le dépôt reste privé (bullet précédent, maintenant fait :
  seule la visibilité GitHub reste un geste manuel).
- ~~**Rendre le dépôt public**~~ — fait, licence ([Apache License 2.0](LICENSE), celle de k6 et
  Gatling), README d'accueil et démarrage rapide en trois commandes faits, et le dépôt est
  désormais public sur GitHub (`github.com/coulibalyousmane/Sirocco`). **Clôt entièrement la
  phase 1.**

### Phase 2 — Des scénarios qu'on peut réellement écrire

*Effort élevé · impact décisif*

Le format déclaratif actuel joue une séquence linéaire de requêtes HTTP. Aucun test de charge réel
ne ressemble à ça : il faut des données variables, des branchements, des assertions qui
n'interrompent pas le tir, et des dimensions pour découper les résultats. C'est le plus gros trou
fonctionnel.

- ~~**Jeux de données**~~ — fait, voir [Jeux de données](docs/scenarios/donnees-assertions.md#jeux-de-données) : `DataSet`
  (`Sirocco.Domain.Data`) + `DataSetLoader` CSV/JSON (`Sirocco.Scenarios.Data`), trois stratégies
  (circulaire, aléatoire, unique par utilisateur virtuel), accessible depuis le format déclaratif
  (`{{jeu.colonne}}`) et depuis un scénario scripté. Réduit à CSV/JSON pour ce premier tour — SQL
  écarté : une source de données arbitraire (requête, pool de connexions, ré-exécution par tir)
  dépasse le scope d'un chargement de fichier unique et mériterait son propre chantier plutôt
  qu'un troisième format ajouté en hâte à `DataSetLoader`. Vérifié par de vrais tirs.
- ~~**Checks**~~ — fait, voir [Checks](docs/scenarios/donnees-assertions.md#checks) : `CheckRule` (même vocabulaire
  Regex/XPath/JsonPath que l'extraction), section `checks` par étape. Chaque check devient sa
  propre étape du rapport — reutilise `StepId`/`StepScope` tels quels, aucun changement dans
  `Sirocco.Application`/`Sirocco.Infrastructure`. Une assertion qui échoue ne fait jamais échouer
  la requête HTTP dont elle dérive, mais compte comme n'importe quelle étape pour l'issue de
  l'itération. Sans effet sur les scénarios scriptés, qui pouvaient déjà publier ce genre
  d'assertion directement via `StepRegistry`/`StepScope`. Vérifié par de vrais tirs.
- ~~**Groupes et étiquettes**~~ — fait, voir [Groupes et étiquettes](docs/scenarios/donnees-assertions.md#groupes-et-étiquettes) :
  un `group` par étape (préfixé au nom pour former le nom qualifié, réutilise `StepId` tel quel —
  couvre la dimension `endpoint`) et des `tags` de scénario (métadonnée de tir reportée dans
  l'en-tête du rapport — couvre `région`/`version`, qui varient par tir, pas par requête). Rendu
  délibérément plat : pas de hiérarchie visuelle (indentation, sous-total par groupe) déduite du
  nom, ni de tags par requête façon k6 — les deux exigeraient de faire d'une valeur choisie à
  l'exécution une clé d'agrégation à part entière, jusque dans `StepAccumulator`/`MetricResult`.
  Limite : étiquettes non propagées au rapport fusionné en mode distribué. Vérifié par un vrai tir.
- ~~**Métriques personnalisées**~~ — fait, voir [Métriques personnalisées](docs/scenarios/donnees-assertions.md#métriques-personnalisées) :
  `CustomMetricKind` (compteur/jauge/taux/tendance), `CustomMetricRegistry`/`MetricRule` (même
  vocabulaire Regex/XPath/JsonPath que les checks), section `metrics` par étape. Première
  fonctionnalité de cette phase à ne pas pouvoir réutiliser `StepId`/`StepAccumulator` tels
  quels — une valeur métier arbitraire n'est pas une durée de requête — d'où une seconde chaîne
  d'agrégation parallèle (canal borné, accumulateur, agrégateur), même discipline « consommateur
  unique » que la chaîne native. Rendue dans le rapport et dans Prometheus. Limites : pas de
  centiles pour la tendance (`LatencyHistogram` est bâti pour une durée non négative bornée), pas
  de fenêtre glissante, pas de fusion inter-workers en mode distribué. Vérifié par un vrai tir.
- ~~**Temps de réflexion et rythme**~~ — fait, voir [Temps de réflexion et rythme](docs/scenarios/donnees-assertions.md#temps-de-réflexion-et-rythme) :
  `ThinkTimeDefinition` (durée fixe ou plage tirée uniformément), `thinkTime`/`thinkTimeMax` par
  étape du format déclaratif. Aucun changement dans le moteur : une pause n'est qu'un `Task.Delay`
  hors de la portée de mesure de l'étape, que le modèle ouvert absorbe nativement en dette
  d'ordonnancement — exactement comme le ferait une reponse HTTP lente. Vérifié par de vrais
  tirs : une pause fixe fait chuter le débit effectif d'un seul utilisateur virtuel exactement à
  la valeur attendue, sans jamais affecter la latence brute rapportée pour l'étape HTTP.

> **Décision structurante tranchée et mise en œuvre : Roslyn (C# scripté).** Voir [Scénarios
> scriptés](docs/scenarios/scripte.md#scénarios-scriptés-roslyn) — `ScriptedWorkflowLoader`/`WorkflowFileLoader`,
> vérifiés par un vrai tir (`scenarios/scripted-checkout.csx`). Limite restante : mode distribué
> non pris en charge pour ce format, `WorkerCoordinator` reste câblé sur le déclaratif seul.
>
> Un scénario scripté devient un
> `IWorkflow` compilé à la volée (`Microsoft.CodeAnalysis.CSharp.Scripting`) : zéro couche
> d'interop à concevoir, réutilisation directe de `Sirocco.Domain`/`Application`/`Infrastructure`,
> déjà publiés en paquets NuGet (voir [Paquets NuGet](docs/demarrer/installation.md#paquets-nuget)) — un script
> scénario et une bibliothèque scénario deviennent le même code, à la compilation près. L'AOT
> était déjà hors de portée pour d'autres raisons (`YamlDotNet`/JSON par réflexion dans
> `ScenarioDefinitionLoader`, voir [Binaires autonomes](docs/demarrer/installation.md#binaires-autonomes)) : Roslyn
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

Sirocco ne sait piloter qu'un débit cible. C'est le bon modèle par défaut, mais beaucoup de besoins
réels s'expriment autrement — « exactement 50 utilisateurs simultanés », « 1 000 itérations
réparties », « ce scénario à 10 RPS pendant que celui-là monte en charge ». Refuser le modèle fermé
par purisme coûterait des utilisateurs.

- ~~**Modèle fermé**~~ — fait, voir [Modèle fermé](docs/charge/modeles.md#modèle-fermé) : `--vus <n>
  --duration <d>` fait tourner exactement N utilisateurs virtuels sans aucune pause imposée,
  `ClosedModelScheduler` (`ILoadScheduler`) prenant la place de `CoordinatedRateLimiter` derrière
  le même moteur. Mise en garde explicite dans le rapport (`LoadTestReport.ClosedModel`) : sans
  échéancier théorique, il n'y a pas de correction du *coordinated omission* en modèle fermé — les
  chiffres ne sont jamais comparables à un tir en modèle ouvert. Limite : mode distribué non pris
  en charge pour ce modèle. Vérifié par un vrai tir.
- ~~**Exécuteurs multiples**~~ — fait. Utilisateurs constants (voir ci-dessus) ; montée
  d'utilisateurs, voir [Montée d'utilisateurs](docs/charge/modeles.md#montée-dutilisateurs) (`--vus-from`/
  `--vus-to`, `RampingVirtualUserPool`) ; itérations partagées et itérations par utilisateur, voir
  [Itérations partagées et itérations par
  utilisateur](docs/charge/modeles.md#itérations-partagées-et-itérations-par-utilisateur) (`--iterations`,
  `--vus <n> --iterations-per-vu <k>`, `IterationCountScheduler`). Les quatre partagent la même
  mise en garde de rapport, faute d'échéancier théorique à comparer.
- ~~**Scénarios concurrents**~~ — fait, voir [Scénarios concurrents](docs/charge/scenarios-concurrents.md#scénarios-concurrents) :
  `Sirocco:Scenarios` dans un `appsettings.json` (comme `Sirocco:RampVus`, pas d'équivalent CLI
  plat), chaque scénario avec son propre profil/modèle de charge, ses étiquettes et ses seuils.
  Chaque scénario construit sa propre chaîne de mesure à la main (pas via le conteneur
  d'injection de dépendances, qui ne loge qu'un singleton par type) : c'est cet isolement complet
  qui garantit qu'aucune mesure ne se mélange entre deux scénarios, même s'ils partagent un nom
  d'étape. Limites : mode distribué non pris en charge, `/report/live` et `/metrics` non
  alimentés — seuls `/report`, `/report.html` et `/thresholds` le sont, une fois le tir terminé.
  Vérifié par un vrai tir à deux scénarios concurrents.
- ~~**Bridage**~~ — fait, voir [Bridage](docs/charge/modeles.md#bridage) : `RateCappedScheduler`
  (`Sirocco.Application.Execution`), décorateur d'`ILoadScheduler` qui retarde la transmission des
  jetons plutôt que de réécrire leur échéance planifiée — le retard qu'il impose se mesure donc
  comme une dette d'ordonnancement ordinaire, pas comme un cas particulier masqué. `--max-rps`
  n'est un cinquième modèle mutuellement exclusif avec rien : il compose avec tous les modèles
  précédents et avec `Sirocco:Scenarios` (`ScenarioOptions.MaxRequestsPerSecond`, avec repli sur le
  plafond global). **Clôt entièrement la phase 3.** Vérifié par de vrais tirs : modèle ouvert à
  100 RPS ramené à 20, modèle fermé à 176 RPS naturels ramené à 15, plafond par scénario et repli
  global tous deux respectés dans un tir à deux scénarios concurrents.

### Phase 4 — Un rapport au niveau de Gatling

*Effort moyen · impact fort*

Le rapport de Gatling est la raison pour laquelle beaucoup d'équipes le choisissent. Celui de
Sirocco est un tableau statique : il donne l'état final, jamais la trajectoire. Or c'est la
trajectoire — le moment où les centiles décrochent — qui explique une dégradation.

- ~~**Séries temporelles**~~ — fait, voir [Série temporelle](docs/rapports/mesure.md#série-temporelle) :
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
- ~~**Interface web temps réel**~~ — fait : `/report/live.html` (`SiroccoHostOptions.LiveDashboardRefreshSeconds`,
  3 s par défaut) rend le même rapport HTML sur la fenêtre glissante, avec une balise
  `<meta http-equiv="refresh">` qui le recharge seul pendant le tir — au-delà du JSON brut de
  `/report/live`. **Clôt entièrement la phase 4.**

### Phase 5 — Réduire le coût du premier scénario

*Effort moyen · impact sur l'adoption*

Écrire un premier scénario à la main est le moment où l'on abandonne un outil. Les convertisseurs
sont le levier d'adoption le moins cher du marché : on part d'un trafic déjà capturé plutôt que
d'une page blanche.

- ~~**HAR vers scénario**~~ — fait, voir [Convertisseur HAR](docs/convertisseurs/index.md#convertisseur-har) :
  `tools/Sirocco.HarConvert` traduit un export du navigateur en scénario **scripté** C# (`.csx`),
  conformément à la décision structurante ci-dessus — jouable sans aucun câblage supplémentaire.
  Actifs statiques ignorés par extension, hôte cible retenu par fréquence (pas par ordre
  d'apparition — un bug réel de la première version, trouvé et corrigé en vérifiant un vrai HAR
  où un appel tiers sans extension reconnue precédait le premier appel à la cible). Limite
  documentée : authentification/cookies capturés à revoir manuellement (valeurs de session
  probablement expirées), corps multipart non pris en charge. Vérifié par un vrai tir : HAR
  reconstitué d'un aller-retour réel login/catalogue/checkout contre `Sirocco.SampleTarget`,
  mêlé à un actif statique et un hôte secondaire, converti puis exécuté par `sirocco run`.
- ~~**OpenAPI vers scénario**~~ — fait, voir [Convertisseur OpenAPI](docs/convertisseurs/index.md#convertisseur-openapi) :
  `tools/Sirocco.OpenApiConvert` traduit une spécification OpenAPI 3.x (JSON) en **squelette**
  scripté C# (`.csx`) — un step par opération, corps JSON d'exemple dérivé du schéma (`$ref`
  résolues contre `components/schemas`, garde anti-cycle). Différence assumée avec le
  convertisseur HAR : une spécification ne décrit que la forme d'une API, jamais de données
  réelles, donc la sortie n'est jamais directement jouable — contrairement au HAR, qui rejoue du
  trafic capturé. Limites documentées : JSON seul (pas de YAML dans cette première version),
  `application/json` seul comme type de corps, paramètres de requête optionnels omis, aucun
  schéma d'authentification traduit (même raison que pour le HAR : une vraie valeur ne peut venir
  que d'un humain). Vérifié par deux vrais tirs contre `Sirocco.SampleTarget` à partir d'une
  spécification décrivant fidèlement ses trois routes réelles : le squelette brut échoue le
  checkout (placeholder d'authentification, comme documenté) ; complété à la main avec le jeton
  et l'identifiant de produit lus dans les réponses précédentes, les 3 étapes réussissent.
- ~~**Collection Postman vers scénario**~~ — fait, voir [Convertisseur
  Postman](docs/convertisseurs/index.md#convertisseur-postman) : `tools/Sirocco.PostmanConvert` traduit une
  collection Postman (v2.1) en squelette scripté C# (`.csx`), même nature que le convertisseur
  OpenAPI — une collection décrit des requêtes construites à la main, pas des données réelles.
  Dossiers imbriqués parcourus récursivement, variables de collection (`{{nom}}`) substituées
  dans l'URL/en-têtes/corps. Limites documentées : pas d'environnement Postman séparé lu, corps
  `formdata` non pris en charge, aucun schéma d'authentification traduit. Trouvaille réelle en
  vérifiant une vraie conversion : un placeholder substitué sans guillemets dans un corps JSON
  (convention Postman pour injecter un nombre) casse la syntaxe — documenté, pas corrigé en
  silence, une variable Postman n'ayant pas de schéma pour deviner son type. **Clôt entièrement
  la phase 5**, hors proxy enregistreur (bullet suivant, conditionné à un vrai public). Vérifié
  par deux vrais tirs contre `Sirocco.SampleTarget` : squelette brut en échec sur le checkout
  (placeholders), squelette complété à la main aux 3 étapes réussies.
- ~~**Proxy enregistreur**~~ — fait, voir [Proxy enregistreur](docs/convertisseurs/index.md#proxy-enregistreur) :
  `tools/Sirocco.RecorderProxy` capture du trafic HTTP en direct, sans export manuel — seul
  convertisseur/outil de la phase à dépendre d'un autre (`ProjectReference` vers
  `Sirocco.HarConvert`, dont il reutilise `HarConverter.Convert` tel quel : une capture en direct
  alimente la meme forme `HarEntry` qu'un export HAR de navigateur). Scope volontairement réduit
  face au *recorder* de Gatling : reverse proxy à cible unique, HTTP seul, pas d'interception TLS
  (MITM) — cohérent avec le modèle `--target-url` unique de `sirocco run`, évite le chantier
  certificat/confiance d'un vrai proxy HTTPS pour une fonctionnalité qui restait conditionnée à un
  vrai public. **Clôt entièrement la phase 5.** Vérifié par un vrai tir de bout en bout : session
  réelle login/catalogue/checkout enregistrée à travers le proxy contre `Sirocco.SampleTarget`,
  scénario généré puis rejoué immédiatement — les 4 étapes à 0 % d'échec, le jeton capturé
  encore valide (contrairement au HAR, où l'export manuel laisse le temps à un jeton d'expirer).

### Phase 6 — Élargir sans tout porter soi-même

*Effort élevé · impact structurel*

k6 n'a pas écrit ses dizaines de protocoles : il a ouvert `xk6` et la communauté l'a fait. Porter
seul SQL, Kafka, MQTT, AMQP et le reste est un puits sans fond. Le modèle d'extension doit venir
**avant** les protocoles, pas après — chaque protocole écrit dans le cœur est une dette permanente.

- ~~**Contrat de plugin** stable~~ — fait, voir [Contrat de plugin](docs/extensions/contrat.md#contrat-de-plugin) :
  `PluginWorkflowLoader` (`Sirocco.Scenarios`) charge un `IWorkflow` depuis une assembly `.dll`
  compilée independamment de ce depot (`Assembly.LoadFrom`, type resolu par `--plugin-type` ou
  candidat unique, constructeur public sans parametre). Le contrat lui-meme (`IWorkflow`/
  `IVirtualUserContext`/`StepScope`) existait deja, agnostique du protocole — ce qui manquait
  etait un moyen de le charger sans `ProjectReference` vers ce depot. Limites documentees :
  aucune configuration injectee dans le type instancie (un plugin gere la sienne), pas de
  resolution NuGet dans cette version (bullet suivant), mode distribue non pris en charge (meme
  limite que le scripte). `samples/Sirocco.SamplePlugin` est la preuve reelle du decouplage :
  compile separement, jamais reference par `Sirocco.Host`/`Sirocco.Cli`/`Sirocco.Scenarios`.
  Verifie par deux vrais tirs contre `Sirocco.SampleTarget` : selection automatique du seul type
  disponible, puis selection explicite via `--plugin-type` — les deux a 0 % d'echec.
- ~~**Chargement dynamique** d'extensions et résolution depuis NuGet~~ — fait, voir [Résolution
  NuGet](docs/extensions/contrat.md#résolution-nuget) : `NuGetPluginResolver` (`Sirocco.Scenarios`,
  `NuGet.Protocol`) resout un plugin par identifiant de paquet (`--plugin-package`/
  `--plugin-package-version`/`--plugin-source`) plutot qu'un chemin de fichier deja present sur le
  disque — telecharge le `.nupkg` depuis la premiere source qui le connait, extrait le groupe
  `lib/<tfm>` le plus proche de `net10.0` (`FrameworkReducer`), cache local persistant entre les
  tirs (une version explicite deja en cache ne redeclenche aucun trafic reseau). Verifie par un vrai
  tir : `Sirocco.SamplePlugin` empaquete via `dotnet pack` dans un dossier local (flux NuGet a part
  entiere), resolu puis execute contre `Sirocco.SampleTarget` — 0 % d'echec.
  **Dependances transitives restaurees depuis le 26 août 2026** (la limite documentee ici jusque-la) :
  le graphe declare par le paquet est parcouru en largeur et les assemblies de chaque paquet atteint
  sont extraites a cote de celle du plugin. C'est le premier des prerequis de la ligne « Écosysteme
  d'extensions communautaire » du tableau concurrentiel : sans lui, seules des extensions **sans
  aucune dependance** pouvaient etre distribuees par paquet, ce qui exclut de fait la plupart des
  protocoles reels. Limites residuelles documentees : actifs `runtimes/<rid>/native` non servis,
  arbitrage de version sommaire (voir [Résolution NuGet](docs/extensions/contrat.md#résolution-nuget)).
  Verifie par un vrai tir : plugin empaquete dependant de `Polly` (donc transitivement de
  `Polly.Core`, aucun des deux present dans l'hote), resolu depuis un flux local + nuget.org, execute
  contre `Sirocco.SampleTarget` — 200 iterations, 0 % d'echec ; puis contre-epreuve en supprimant
  `Polly.Core.dll` du cache, qui echoue bien faute de cette dependance. Deux defauts reels trouves
  en verifiant : la regle d'exclusion du contrat partage etait un prefixe `Sirocco.*` (elle avalait
  toute extension tierce ainsi nommee), et une dependance manquante remontait en
  `TargetInvocationException` non traduite — trace de pile brute et code de sortie 127 au lieu d'un
  message.
- ~~**Protocoles de référence**~~ écrits comme extensions pour valider le contrat : SQL, SSE, MQTT,
  GraphQL. **Les quatre sont faits.** Et **publiables comme paquets depuis le 26 août 2026** (étiquette
  `sirocco-extension`, README par paquet) : ce sont les premiers exemples consommables de la
  convention d'écriture d'extension, voir [Extensions publiées](docs/extensions/contrat.md#extensions-publiées-et-convention-de-découverte).
  Vérifié par de vrais tirs depuis les paquets réellement produits par `dotnet pack Sirocco.sln` :
  SSE, GraphQL et MQTT se chargent par `--plugin-package` (0 % d'échec chacun, `MQTTnet` restauré
  transitivement) ; **SQL non** — sa bibliothèque native `e_sqlite3` vit dans `runtimes/<rid>/native`,
  que la restauration transitive ne sert pas, d'où un `DllNotFoundException`. Il se consomme par
  `PackageReference` + `dotnet publish`, voie vérifiée elle aussi par un vrai tir (SELECT et INSERT
  réels, 0 % d'échec). Publié quand même : cette voie-là fonctionne, et le taire serait pire que le
  documenter.
  - ~~**SQL**~~ — fait, voir [Protocoles de référence — SQL](docs/extensions/contrat.md#sql) :
    `extensions/Sirocco.Extensions.Sql` interroge une vraie base SQLite (deux etapes reelles par
    iteration, SELECT parametre et INSERT) plutot que le client HTTP partage — referme le SQL
    explicitement ecarte des jeux de donnees en phase 2, sous un angle different. Trouvaille
    reelle documentee plutot que corrigee dans le coeur : un plugin charge par `Assembly.LoadFrom`
    doit etre publie (`dotnet publish`), pas seulement compile, et sa bibliotheque *native* doit
    en plus etre cherchee par le plugin lui-meme (`NativeLibrary.SetDllImportResolver`), le
    resolveur par defaut de `SQLitePCLRaw` cherchant a cote de l'hote plutot qu'a cote du plugin.
  - ~~**SSE**~~ — fait, voir [Protocoles de référence — SSE](docs/extensions/contrat.md#sse) :
    `extensions/Sirocco.Extensions.Sse` valide le contrat sous un angle different de SQL — pas un
    protocole different de HTTP, mais un usage different d'`IVirtualUserContext.HttpClient` : une
    reponse en flux continu (`text/event-stream`) lue evenement par evenement, plutot que
    l'aller-retour requete/reponse unique du reste du depot. Contrairement a SQL, utilise
    `--target-url` normalement et ne depend d'aucun paquet NuGet au-dela de `Sirocco.Domain` — un
    simple `dotnet build` suffit, confirmant par contraste que l'exigence de publication de SQL
    tenait a sa dependance externe, pas au contrat lui-meme. Nouveau point d'ecoute
    `GET /api/events/stream` sur `Sirocco.SampleTarget`.
  - ~~**MQTT**~~ — fait, voir [Protocoles de référence — MQTT](docs/extensions/contrat.md#mqtt) :
    `extensions/Sirocco.Extensions.Mqtt` revient a un protocole reellement different de HTTP comme
    SQL, mais oriente publication/abonnement : chaque iteration s'abonne a un sujet qui lui est
    propre, y publie un message, puis attend sa propre reception (round-trip complet, pas un
    simple accuse de publication). `Sirocco.SampleTarget` heberge desormais son propre courtier
    MQTT embarque (`MQTTnet.Server`) pour rester sans infrastructure externe, meme logique que
    SQLite pour SQL. Confirmation reelle plutot que nouvelle trouvaille : ce plugin doit lui aussi
    etre publie (`dotnet publish`), pas seulement compile, meme si sa seule dependance ajoutee
    (`MQTTnet`) est entierement geree, sans composant natif — la limite tient au chargement
    dynamique d'un plugin avec dependance externe, pas a un alea propre a SQLite.
  - ~~**GraphQL**~~ — fait, voir [Protocoles de référence — GraphQL](docs/extensions/contrat.md#graphql) : comme
    SSE, reste au-dessus de HTTP mais valide un autre aspect du contrat — succes/echec se lit dans
    le corps JSON (`errors`), jamais dans le code de statut qui reste 200 meme pour une mutation en
    echec metier. Deux etapes reelles, memes natures d'operation que SQL (lecture/ecriture) sous
    revetement HTTP. `Sirocco.SampleTarget` heberge un vrai schema GraphQL (`GraphQL`, moteur
    GraphQL.NET, pas une simulation par correspondance de chaine). Aucune dependance NuGet au-dela
    de `Sirocco.Domain` cote plugin : un simple `dotnet build` suffit, comme SSE. **Clot
    entierement le bullet des quatre protocoles de reference.**
- ~~**Guide d'écriture d'extension**~~ — fait, voir [Guide d'écriture d'extension](docs/extensions/guide.md#guide-décriture-dextension) :
  sans documentation, un modèle de plugin restait théorique. Le contrat lui-même n'a pas changé —
  ce chantier est purement documentaire, contrairement à tous les précédents de la phase. Contenu :
  quand écrire une extension plutôt qu'un scénario scripté, le contrat minimal (`IWorkflow` en
  détail, ordre d'appel des cinq méthodes, discipline du chemin chaud), un premier plugin pas à pas
  (même forme que `samples/Sirocco.SamplePlugin`), le choix `dotnet build`/`dotnet publish` selon la
  nature de la dépendance ajoutée, la distribution par paquet NuGet, la discipline de test (vrai
  double, jamais un mock) et un tableau récapitulatif des quatre protocoles de référence comme
  exemples travaillés. **Clôt entièrement la phase 6.** Vérifié en suivant le guide à la lettre
  depuis un dossier vide, sans rien copier depuis ce dépôt : un plugin minimal construit par un
  simple `dotnet build` puis chargé par `sirocco run` contre `Sirocco.SampleTarget` réellement
  démarré — sélection automatique du seul type disponible, puis `--plugin-type` explicite — les
  deux à 0 % d'échec. Dossier jetable, jamais commité.

### Phase 7 — Échelle cloud-native

*Effort moyen · impact conditionnel*

Le mode distribué existe et fonctionne, mais il se déploie à la main via `docker-compose`. À partir
de quelques dizaines de workers, ça ne tient plus. À ne lancer que lorsque des utilisateurs réels
atteignent ce plafond.

- ~~**Opérateur Kubernetes**~~ — fait, voir [Opérateur Kubernetes](docs/distribue/kubernetes.md#opérateur-kubernetes) :
  une ressource `TestRun` (`sirocco.dev/v1alpha1`, construite avec KubeOps), workers en
  `StatefulSet` + service headless, maître en `Job` — créés à l'application de la ressource,
  workers détruits automatiquement (réplicas ramenés à 0) une fois le tir terminé.
- ~~**TLS sur le control plane**~~ — fait, voir [TLS sur le control plane](docs/distribue/mode-distribue.md#tls-sur-le-control-plane) :
  un seul certificat auto-signé partagé par le maître et les workers, épinglé par empreinte
  (`Sirocco.ClusterCertificateThumbprint`) côté client — le serveur (Kestrel) sert du HTTPS par
  pure configuration, sans code supplémentaire. Simplification assumée face à une PKI par nœud,
  renvoyée au chantier Kubernetes suivant (`cert-manager` y trouvera naturellement sa place).

  Vérifié par un vrai tir distribué (1 maître, 2 workers, certificat auto-signé partagé) : 224
  itérations fusionnées, 0 % d'échec, code de sortie 0. Contre-épreuve avec une empreinte
  volontairement fausse : la poignée de main échoue dès l'enregistrement (`AuthenticationException`),
  donc l'épinglage n'est pas un no-op. **Tir témoin en HTTP** rejoué depuis, cette fois chiffré au
  lieu d'être affirmé : même topologie et même profil sans aucun certificat ni empreinte, 224
  itérations elles aussi, 0 % d'échec sur les quatre étapes, p95 87,04 ms, seuils respectés, code
  0 — le chemin historique est intact et l'option à `null` n'a réellement aucun effet. Le chantier
  avait aussi mis au jour un vrai bug : `WorkerCoordinator.SubmitReportAsync` utilisait un client
  HTTP différent des deux autres points d'appel, si bien que le rapport final échouait en
  `RemoteCertificateNameMismatch` alors que le reste du control plane passait — trouvé en observant
  un tir qui ne se terminait jamais, pas en relisant le code.
- ~~**Autoscaling**~~ — fait, voir [Autoscaling](docs/distribue/kubernetes.md#autoscaling) : `spec.autoscaling`
  calcule le nombre de workers requis palier par palier à partir du débit cible du profil (déjà
  connu à l'avance), pas d'un HPA/KEDA réactif à des métriques observées. Live, pas seulement un
  dimensionnement statique au démarrage : `MasterOrchestrationHostedService.ExecuteAdaptiveAsync`
  suit le plan de paliers posé par l'opérateur (`Master__StagePlannedWorkers`), dispatche un
  nouveau worker qui rejoint en cours de route avec les paliers restants, et un worker retiré par
  le contrôleur (`StatefulSet` réduit) reçoit désormais SIGTERM proprement
  (`WorkerCoordinator.Stop()` sur `ApplicationStopping`) et soumet un rapport partiel plutôt que
  de disparaître en silence — tombant sinon dans le filet de sécurité déjà existant
  (`MarkDeadIfStale`/`LostWorkers`). Chemin figé (`spec.autoscaling` absent) inchangé.
- ~~**Reprise sur perte d'un worker**~~ — fait, voir [Reprise sur perte d'un
  worker](docs/distribue/mode-distribue.md#reprise-sur-perte-dun-worker) : un worker dispatché signale désormais qu'il
  est vivant en continu (`POST /master/heartbeat`, `WorkerLivenessHostedService`), pas seulement
  une fois à l'enregistrement. Passé `Master.WorkerDeadAfterSeconds` (20 s par défaut) sans
  heartbeat, `MasterCoordinator.MarkDeadIfStale` déclare le worker perdu et le rapport final se
  fusionne avec les survivants (`LoadTestReport.LostWorkers` documente honnêtement ce qui manque),
  plutôt que d'attendre indéfiniment un rapport qui ne viendra jamais. Filet de sécurité optionnel
  (`Master.ReportTimeoutSeconds`, `null` par défaut) pour le cas non couvert par le heartbeat : un
  worker dont le *process* reste vivant mais dont le *tir local* est bloqué. Vérifié par un vrai
  tir distribué à deux workers (maître + deux process réels, pas de simulation) : un worker tué en
  cours de tir (`kill -9`) est déclaré perdu ~6 s après son dernier heartbeat, le rapport final
  contient `lostWorkers: ["http://localhost:5301"]` et les statistiques réelles du seul worker
  survivant (60 itérations, percentiles réels) — le maître ne reste jamais bloqué. Un second tir
  témoin, sans tuer aucun worker, confirme l'absence de faux positif.

### Phase 8 — Prouver publiquement le différenciateur

*Effort faible · impact sur la notoriété*

Un outil technique inconnu ne se diffuse pas par ses fonctionnalités mais par une démonstration
qu'on ne peut pas ignorer. Sirocco en a une à disposition, et elle est reproductible.

- ~~**Benchmark comparatif publié**~~ — fait : [benchmark/README.md](benchmark/README.md) et
  [benchmark/results/RESULTS.md](benchmark/results/RESULTS.md). Même cible saturée
  (`Sirocco.SampleTarget`, `ConcurrencyGate` réglé), même scénario et même profil de charge
  (rampe 20→150 req/s) pour Sirocco, k6, Gatling et NBomber. Reproductible en une commande
  (`benchmark/run.sh`), méthode et limites documentées en toute honnêteté (y compris la variance
  observée d'un tir à l'autre sur une machine partagée).
- ~~**Article de fond**~~ — fait, en deux versions complètes :
  [Zéro erreur, et pourtant inutilisable](docs/articles/dette-ordonnancement.md) et
  [Zero errors, and still unusable](docs/articles/scheduling-debt.md), adossées à une expérience
  reproductible en une commande ([`benchmark/saturation.sh`](benchmark/saturation.sh)).

  **Le chantier n'était pas d'écrire, c'était d'avoir quelque chose à montrer.** Le benchmark
  publié affiche 19,1 ms de dette sur un p99 de 337,9 ms : une thèse forte étayée par une preuve
  faible. La cause, trouvée en lisant le code plutôt qu'en la supposant, est que la cible du
  benchmark **déleste** — `ConcurrencyGate` attend 50 ms puis rend un 503, ce qui libère les
  utilisateurs virtuels et empêche tout retard d'injecteur. Le benchmark mesurait un système sain.

  L'expérience change **une seule variable** (`QUEUE_WAIT_MS`, de 50 ms à 120 s : la cible met en
  file au lieu de refuser), à débit constant de 100 req/s pendant 60 s, plafond de 50 utilisateurs
  virtuels partout où il existe, plus une passe témoin sur la cible délesteuse. Résultat réel :
  **Response p99 28 311 ms contre Service p99 819 ms, 0 % d'échec, dette maximale 27 796 ms**, sur
  un profil de 60 s qui a demandé 88,6 s de temps réel. Le témoin, lui, retombe à 36,9 ms d'écart
  au p99 — deux à trois ordres de grandeur en dessous, ce qui confirme que la faible dette du
  benchmark publié venait de la cible et pas de la mesure. (Sa valeur exacte varie d'un tir à
  l'autre sur une machine partagée : 19,1 ms puis 21,1 ms puis 188,4 ms de dette maximale selon la
  charge de fond ; c'est l'ordre de grandeur qui porte la conclusion, pas le chiffre.)

  **Ce que le tir a démenti, et qui a rendu l'article meilleur** : l'hypothèse de départ était que
  Sirocco serait le seul à voir la vérité. Faux, deux fois.

  D'abord, NBomber (27 672 ms) ne borne pas sa concurrence, mesure donc cette attente
  *directement* — sans aucune notion de dette d'ordonnancement — et retombe à 2 % du `Response` de
  Sirocco : c'est une **validation croisée** du chiffre par un mécanisme indépendant. Le vrai
  clivage n'est pas Sirocco contre les autres, c'est **borné contre non borné**.

  Ensuite, la vraie leçon du tir est ailleurs, dans une colonne que personne ne lit : **un seul des
  quatre outils a délivré les 6000 itérations demandées.** k6 en a livré 4092 (1907
  `dropped_iterations`), Gatling 4827, NBomber 5958. Et le p99 rapporté suit exactement ce
  classement à l'envers — 28 311 / 27 672 / 14 573 / 1 163 ms : **plus un outil a délivré peu, plus
  son percentile est flatteur.** Deux causes distinctes derrière ce manque : k6 **abandonne**
  délibérément (choix défendable, compteur explicite) ; Gatling, non borné, a épuisé les sockets de
  la machine (1173 × `j.n.NoRouteToHostException`) — l'angle mort du modèle non borné, où
  l'injecteur devient le goulot sans que la latence le dise. Aucun outil ne dissimule : chacun rend
  compte de son manque, mais dans une monnaie que ni un SLO de latence ni un seuil de CI ne
  regardent. L'article est écrit en créditant chaque outil, pas en les chargeant, et cette colonne
  « charge délivrée » est devenue son diagnostic central — générée depuis les sorties réelles, y
  compris les libellés d'erreur de Gatling, plutôt qu'affirmée en prose.

  **Trois angles morts de Sirocco mis au jour par cette expérience, et écrits dans l'article** :
  `InjectorFellBehind` est resté faux (les 6000 jetons ont été émis, seulement en retard — ce
  drapeau détecte un injecteur qui renonce, pas un qui traîne) ; `isTrustworthy` est resté vrai
  (il ne regarde que les mesures perdues) ; et la dette n'est portée que par le **premier pas** de
  l'itération, si bien que l'étape `checkout` lue seule affiche Response = Service et ne montre
  rien.

  **Un vrai bug corrigé**, trouvé en exécutant et pas en relisant : `benchmark/normalize` plantait
  sur une sortie Gatling sans aucun échec, Gatling écrivant `-` et non `0` dans la colonne KO — cas
  jamais atteint par le benchmark publié, qui sature toujours en 503.

  Dispositif anti-dérive du choix « deux versions complètes » : aucun des deux articles n'écrit un
  chiffre à la main. Les tableaux sont générés par `benchmark/normalize --saturation` en deux
  fragments (`docs/articles/_mesures-{fr,en}.md`) issus d'**une seule structure de données**, et
  inclus par les deux pages. Vérifié en comparant les 176 nombres des deux pages rendues : aucune
  divergence.

  Limites résiduelles assumées : une seule machine sans isolation — c'est elle qui a fait
  épuiser les sockets à Gatling et tronqué son tir, donc les percentiles inter-outils démontrent un
  mécanisme, pas un classement de vitesse ; harnais hors CI (comme le benchmark publié — Docker,
  image k6, bundle Gatling, plusieurs minutes) ; pas de contre-épreuve « injecteur délibérément
  goulot » ; Gatling et NBomber non bornables en modèle ouvert, donc la parité de plafond de VUs ne
  vaut qu'entre Sirocco et k6 ; sorties brutes non versionnées, seul
  [SATURATION.md](benchmark/results-saturation/SATURATION.md) l'est.
- ~~**Site de documentation**~~ — fait, publié sur <https://coulibalyousmane.github.io/Sirocco/>
  (DocFX, déployé par [`.github/workflows/docs.yml`](.github/workflows/docs.yml)). Le README, qui
  faisait 2666 lignes et servait à la fois de page d'accueil, de manuel, de journal de
  vérification et de doc d'architecture, est réduit à une page d'accueil de ~115 lignes ; son
  contenu est réparti **verbatim** en 20 pages sous [`docs/`](docs/), sans réécriture de prose —
  une seule source de vérité, pas un site en doublon du README.

  **« Exemples exécutables » est pris au mot** : aucune page ne recopie un exemple dans son
  markdown, chacune **transclut** un vrai fichier du dépôt (`[!code-yaml[](...)]`), et la CI
  **exécute** chacun d'eux contre un `Sirocco.SampleTarget` réellement démarré, avec un seuil sur
  le taux d'erreur — sans seuil, `sirocco run` sortirait 0 même à 100 % d'échec et la CI validerait
  un exemple cassé. Un exemple qui cesse de fonctionner casse donc le build, et ne peut plus
  dériver en silence de ce que le moteur fait réellement.

  Ce chantier a surtout révélé combien la doc avait déjà dérivé : **aucun** fichier du dépôt ne
  contenait `datasets:`, `checks:`, `group:`, `tags:`, `metrics:`, `thinkTime:`,
  `Sirocco:Scenarios`, `Sirocco:Workflow` ni la section `GrpcEcho`, alors que chaque section
  correspondante affirmait avoir été vérifiée par un vrai tir ; l'exemple `Sirocco:Scenarios`
  référençait `checkout.yaml` et `browse.yaml`, qui n'existaient nulle part ; et l'exemple de
  métrique personnalisée mesurait une jauge sur `$.cartSize`, un champ absent de la réponse réelle
  de la cible. D'où 12 nouveaux fichiers d'exemple dans [`docs/examples/`](docs/examples), tous
  exécutés pour de bon.

  Piège d'usage rencontré en écrivant les pages, documenté plutôt que contourné : `Sirocco.Host`
  n'a pas d'option pour désigner un fichier de configuration (il lit `appsettings.<env>.json` de sa
  racine de contenu), et le chemin passé à `--contentRoot` doit être **absolu** — un chemin relatif
  est résolu depuis le dossier du binaire, pas depuis le répertoire courant.

  Limites résiduelles assumées : pas de référence d'API générée depuis les commentaires XML (DocFX
  sait le faire, mais c'est une immense surface générée pour un bullet qui demandait un site), pas
  de versionnement de la doc, pas de traduction, et les exemples Kubernetes (`deploy/samples/`)
  comme les extensions SQL/MQTT ne sont pas exécutés en CI — ils demandent respectivement un
  cluster et un `dotnet publish` du plugin.
- ~~**Décision sur l'offre managée**~~ — tranchée le 22 août 2026 : **non**, et la question ne se
  rouvrira que sur les conditions nommées plus bas. Ce bullet était conditionné (« seulement si
  l'adoption open source la justifie ») : le trancher demandait donc de **mesurer** cette adoption
  avant de conclure, pas de la supposer.

  **Ce que la mesure donne**, dépôt public depuis le 5 août 2026, soit 17 jours : 0 étoile, 0 fork,
  0 abonné, 0 issue ; **0 release** — aucun tag, ni local ni distant, et
  [`release.yml`](.github/workflows/release.yml) ne se déclenchant que sur un tag `v*`, il n'a
  jamais tourné ; **0 paquet sur nuget.org** — les cinq identifiants `Sirocco.*` y renvoient 404 ;
  site de documentation en ligne depuis la veille.

  **Ce que ces zéros ne disent pas.** La condition n'est pas *non remplie*, elle est
  **inévaluable** : les trois canaux par lesquels une adoption pourrait arriver sont fermés — rien
  à télécharger, rien à installer, et jusqu'à la veille rien à lire. Zéro étoile en 17 jours dans
  ces conditions n'est pas un signal sur la demande, c'est l'absence de mesure. La réponse reste
  donc « non », mais le motif honnête n'est pas « personne n'en veut » : c'est « rien n'a encore
  été distribué ». Conclure l'inverse aurait donné une décision juste appuyée sur un motif faux —
  et un motif faux se retourne, le jour où quelqu'un demande sur quoi la décision reposait.

  **Conditions de réouverture**, dans cet ordre, la première rendant les deux autres mesurables :

  1. Les deux gestes de distribution encore ouverts de la phase 1 sont faits — paquets publiés sur
     nuget.org, et une première release `vX.Y.Z` réellement poussée. Sans eux, il n'y a rien à
     observer. C'est cette mesure qui a déclenché le renommage du projet, alors appelé Tempest :
     l'identifiant `Tempest` était déjà occupé sur nuget.org par un générateur de templates sans
     rapport (dernier envoi en février 2017, jamais sorti de preview), ce qui rendait improbable
     la réservation du préfixe `Tempest.*`. `Sirocco` et ses cinq `Sirocco.*` sont libres, préfixe
     réservable compris — publier, c'est désormais aussi revendiquer l'identité.
  2. L'usage se **répète** au lieu d'être un pic : des installations de `Sirocco.Cli` qui suivent
     les versions successives, et des issues ouvertes par des inconnus décrivant un usage réel.
     C'est le signal le plus honnête disponible : une étoile coûte un clic, une issue coûte du
     temps.
  3. La demande porte sur l'**hébergement**, pas sur la fonctionnalité — flotte d'injecteurs sans
     cluster à opérer, historique retenu entre les tirs, tableau de bord d'équipe. Tant qu'elle
     porte sur ce que le binaire ne *fait* pas, la bonne réponse reste un ajout au produit open
     source, jamais un service.

  Pas de date de réexamen : une décision conditionnée à des signaux se relit quand les signaux
  arrivent, pas au calendrier.

  **Risque accepté plutôt que passé sous silence** : si une offre hébergée devait un jour être la
  seule source de financement viable, décider tard laisse le terrain à qui décide tôt — et NBomber
  Studio occupe déjà cette place sur .NET, comme le note « Le paysage concurrentiel » en tête de ce
  document. C'est assumé : l'alternative — la construire maintenant — est exactement le
  « construire le SaaS trop tôt » que « Trois façons de perdre du temps » liste comme deuxième
  façon d'en perdre, et elle coûterait précisément le temps qui manque encore à la distribution.

  **Ce que cette décision n'engage pas**, délibérément : aucune promesse publique n'est faite aux
  adoptants sur la licence, ni sur un éventuel modèle open-core — ni dans le README, ni sur le
  site. La décision est stratégique et vit dans cette roadmap seule ; toutes les options
  commerciales restent ouvertes. C'est un choix explicite, pas un oubli.

## Trois façons de perdre du temps

**Courir après la parité avec k6.** Grafana finance une équipe entière. Rattraper l'étendue
fonctionnelle de k6 fonctionnalité par fonctionnalité est perdu d'avance. La seule stratégie viable
est d'être meilleur sur un axe étroit et défendable, puis d'élargir.

**Construire le SaaS trop tôt.** C'est le réflexe qui tue les outils open source naissants :
monétiser avant d'avoir des utilisateurs. Tant que personne n'utilise Sirocco gratuitement,
personne ne le paiera.

**Ajouter des protocoles avant le modèle d'extension.** La phase 6 place volontairement le contrat
de plugin avant les protocoles qu'il doit accueillir.

## Recommandation

**La phase 1 est entièrement faite** — CLI, packaging `dotnet tool`, binaires autonomes, paquets
NuGet bibliothèque, licence, démarrage rapide, et le dépôt est maintenant public sur GitHub. Ce
qui a été construit — la fusion d'histogrammes exacte, les quatre modes gRPC, le mode distribué
sécurisé, l'installation en une commande — a désormais de la valeur pour quelqu'un d'autre que
son auteur. Deux gestes restent ouverts, non bloquants pour la suite : publier les paquets NuGet
sur nuget.org (actuellement installables depuis une source locale ou un flux privé seulement) et
déclencher le workflow de release GitHub pour les binaires autonomes (`vX.Y.Z`, prêt mais jamais
poussé).

**Le projet s'appelait Tempest jusqu'au 23 août 2026**, et le renommage en **Sirocco** est sorti
de la mesure du premier de ces deux gestes. L'identifiant `Tempest` était occupé sur nuget.org, ce
qui ne bloquait rien — les cinq `Tempest.*` étaient libres, et le paquet occupant n'était même pas
un `dotnet tool` — mais interdisait en pratique la réservation du préfixe. La vraie collision était
ailleurs et plus sérieuse : `tempest` sur PyPI est **OpenStack Integration Testing** (v46,
activement maintenu), un outil de test dans l'infrastructure cloud, c'est-à-dire exactement le
public visé ici ; et TEMPEST, nom de code des émanations compromettantes, domine le sens du mot
dans une recherche technique — trois des six dépôts GitHub « tempest » les plus étoilés en
relèvent. Aucun outil de **test de charge** ne portait ce nom, donc rien n'était cassé : c'est le
coût du renommage, pas l'urgence, qui commandait la date. Fait à 0 étoile, 0 fork, 0 release et
0 paquet publié, il n'a invalidé aucune installation existante ; après le premier `nuget push` et
le premier tag, il serait devenu un changement cassant à traîner avec des identifiants dépréciés.
`Sirocco` est libre partout — nom nu et cinq `Sirocco.*` sur nuget.org, préfixe donc réservable.

**La décision de la phase 2 est tranchée et le moteur de script existe** : Roslyn (C# scripté),
voir [Scénarios scriptés](docs/scenarios/scripte.md#scénarios-scriptés-roslyn). C'était le seul choix de cette
roadmap difficile à défaire une fois pris ; il conditionne les phases 5 et 6, toujours valables
telles que décrites dans la note de la phase 2. **Le contenu de la phase 2 est maintenant
entièrement traité** : jeux de données, checks, groupes/étiquettes, métriques personnalisées et
temps de réflexion sont faits, voir [Jeux de données](docs/scenarios/donnees-assertions.md#jeux-de-données),
[Checks](docs/scenarios/donnees-assertions.md#checks), [Groupes et étiquettes](docs/scenarios/donnees-assertions.md#groupes-et-étiquettes),
[Métriques personnalisées](docs/scenarios/donnees-assertions.md#métriques-personnalisées) et
[Temps de réflexion et rythme](docs/scenarios/donnees-assertions.md#temps-de-réflexion-et-rythme).

**La phase 3 est entièrement traitée.** Effectif fixe, montée d'utilisateurs et les deux exécuteurs
par itérations sont faits, voir [Modèle fermé](docs/charge/modeles.md#modèle-fermé) (`--vus <n> --duration <d>`),
[Montée d'utilisateurs](docs/charge/modeles.md#montée-dutilisateurs) (`--vus-from <n> --vus-to <n> --duration <d>`)
et [Itérations partagées et itérations par
utilisateur](docs/charge/modeles.md#itérations-partagées-et-itérations-par-utilisateur) (`--iterations <n>`,
`--vus <n> --iterations-per-vu <k>`) — mise en garde explicite dans le rapport pour les quatre.
[Scénarios concurrents](docs/charge/scenarios-concurrents.md#scénarios-concurrents) (`Sirocco:Scenarios`) fait tourner
plusieurs de ces modèles en parallèle dans le même tir, chacun isolé jusque dans sa propre chaîne
de mesure. [Bridage](docs/charge/modeles.md#bridage) (`--max-rps`) plafonne le débit réel par-dessus n'importe
lequel de ces modèles, y compris par scénario dans un tir à scénarios concurrents.

**La phase 4 est entièrement traitée.** Voir [Série temporelle](docs/rapports/mesure.md#série-temporelle),
[Distribution des temps de réponse](docs/rapports/mesure.md#distribution-des-temps-de-réponse) et [Tableau de
bord temps réel](docs/rapports/mesure.md#tableau-de-bord-temps-réel) : `TimeSeriesRecorder` relève
périodiquement la trajectoire du tir, gardée dans `LoadTestReport.TimeSeries` ; `ToHtml` la rend
désormais en graphe (débit et dette d'ordonnancement superposés) et ajoute un histogramme par
étape à partir des paniers déjà détenus par `LatencyHistogram` ; `/report/live.html` transforme la
fenêtre glissante déjà servie en JSON par `/report/live` en tableau de bord HTML qui se recharge
seul pendant le tir.

**La phase 5 est maintenant entièrement traitée, y compris le proxy enregistreur.** Voir
[Convertisseur HAR](docs/convertisseurs/index.md#convertisseur-har), [Convertisseur
OpenAPI](docs/convertisseurs/index.md#convertisseur-openapi), [Convertisseur
Postman](docs/convertisseurs/index.md#convertisseur-postman) et [Proxy enregistreur](docs/convertisseurs/index.md#proxy-enregistreur) :
`tools/Sirocco.HarConvert`, `tools/Sirocco.OpenApiConvert`, `tools/Sirocco.PostmanConvert` et
`tools/Sirocco.RecorderProxy` traduisent respectivement un export du navigateur, une
spécification d'API, une collection Postman et une capture de trafic en direct en scénario
scripté C# (`.csx`), conformément à la décision structurante prise en phase 2 — pas de YAML/JSON
généré. Différence de nature assumée dans la doc : HAR et proxy enregistreur rejouent du trafic
réel capturé (le second réutilisant directement `HarConverter.Convert`, sans dupliquer sa
logique), OpenAPI et Postman ne produisent qu'un squelette (ni une spécification ni une
collection ne décrivent de données réelles). Le proxy enregistreur reste volontairement réduit
face à celui de Gatling : reverse proxy à cible unique, HTTP seul, pas d'interception TLS.
Comme les phases 6 à 8, tout ce qui reste au-delà peut attendre des retours d'utilisateurs réels.

**La phase 6 est entièrement faite** : voir [Contrat de plugin](docs/extensions/contrat.md#contrat-de-plugin),
[Résolution NuGet](docs/extensions/contrat.md#résolution-nuget), [Protocoles de référence —
SQL](docs/extensions/contrat.md#sql), [— SSE](docs/extensions/contrat.md#sse), [— MQTT](docs/extensions/contrat.md#mqtt), [—
GraphQL](docs/extensions/contrat.md#graphql) et [Guide d'écriture d'extension](docs/extensions/guide.md#guide-décriture-dextension).
`PluginWorkflowLoader` charge un `IWorkflow` compilé indépendamment de ce dépôt depuis un chemin de
fichier, `NuGetPluginResolver` fait de même depuis un identifiant de paquet NuGet, et
`samples/Sirocco.SamplePlugin` prouve les deux par une assembly qui n'est justement référencée par
aucun projet du cœur. Les quatre protocoles de référence valident chacun une facette différente du
contrat : `extensions/Sirocco.Extensions.Sql` contre un protocole réellement différent de HTTP
(trouvaille réelle sur le chargement de dépendances natives, documentée, pas corrigée dans le
cœur) ; `extensions/Sirocco.Extensions.Sse` sous l'angle d'un usage différent du client HTTP
partagé (flux continu plutôt qu'aller-retour unique), confirmant par contraste que l'exigence de
publication de SQL tenait à sa dépendance externe, pas au contrat lui-même ;
`extensions/Sirocco.Extensions.Mqtt` en revenant à un protocole réellement différent comme SQL,
mais orienté publication/abonnement (round-trip complet sujet→courtier→sujet), confirmant que
l'exigence de publication s'applique à toute dépendance externe, gérée ou native ;
`extensions/Sirocco.Extensions.GraphQl` enfin, sous un autre usage HTTP comme SSE — succès/échec
porté par le corps JSON, jamais par le code de statut. Dernier bullet, purement documentaire : le
guide d'écriture d'extension rassemble cette recette pour la cinquième extension, pas encore
écrite par ce dépôt — vérifié en le suivant à la lettre depuis un dossier vide, jusqu'à un vrai tir
à 0 % d'échec contre `Sirocco.SampleTarget`.

**La phase 7 est maintenant entièrement traitée**, malgré le principe énoncé plus haut ("ne
lancer que lorsque des utilisateurs réels atteignent ce plafond") — choix explicite de
l'utilisateur plutôt qu'attente passive. Quatre bullets, dans l'ordre choisi : [Reprise sur perte
d'un worker](docs/distribue/mode-distribue.md#reprise-sur-perte-dun-worker) d'abord, qui fermait un vrai trou de
robustesse (un maître qui restait bloqué indéfiniment sur un worker mort) ; puis [TLS sur le
control plane](docs/distribue/mode-distribue.md#tls-sur-le-control-plane), qui protège désormais la confidentialité du
control plane, pas seulement son authentification ; puis l'[opérateur
Kubernetes](docs/distribue/kubernetes.md#opérateur-kubernetes), qui remplace le déploiement manuel via
`docker-compose` par une ressource `TestRun` déclarative ; enfin l'[autoscaling](docs/distribue/kubernetes.md#autoscaling),
qui rouvre le protocole maître/worker pour que le nombre de workers suive vraiment le débit cible
en direct, pas seulement au démarrage — a mis au jour et corrigé au passage deux vrais bugs
(exception non gérée masquant un échec en faux `Succeeded`, validation de configuration
incompatible avec le nouveau chemin adaptatif), trouvés en vérifiant sur un vrai cluster plutôt
qu'en supposant que ça marchait.

**La phase 8 est maintenant entièrement traitée.** Le [benchmark comparatif](benchmark/README.md)
mesure le différenciateur ; le **site de documentation** le rend consultable
(<https://coulibalyousmane.github.io/Sirocco/>), avec des exemples qui ne peuvent pas mentir parce
que la CI les exécute ; l'**article de fond** ([FR](docs/articles/dette-ordonnancement.md) ·
[EN](docs/articles/scheduling-debt.md)) le démontre enfin sur un régime où il est massif —
28 311 ms de `Response` contre 819 ms de `Service`, à 0 % d'échec — après avoir établi que la dette
de 19,1 ms du benchmark publié venait de la cible, qui déleste, et pas de la mesure.

Le dernier bullet n'était pas un chantier technique et n'a pas été traité comme tel : la
**décision sur l'offre managée** est tranchée, et c'est **non**. Le motif compte autant que la
réponse. L'adoption a été mesurée avant d'être conclue, et elle est nulle sur tous les compteurs —
mais elle l'est parce que **rien n'a encore été distribué**, pas parce que rien n'intéresse
personne : ni release, ni paquet sur nuget.org, et un site en ligne depuis la veille. Le bullet
détaille les trois conditions qui rouvriraient la question, ainsi que le risque accepté en
décidant ainsi.

**Les huit phases de cette roadmap sont donc traitées, à deux gestes près** — publier les paquets
sur nuget.org et pousser une première release `vX.Y.Z` — tous deux dans la phase 1, tous deux de
distribution, et jusqu'ici qualifiés de « non bloquants pour la suite ». Ils ne le sont plus : ce
sont eux qui rendraient mesurable la seule décision encore devant, celle que ce document vient
justement de refuser de prendre à l'aveugle. Le préambule ouvrait sur *« l'écart avec les leaders
est à 70 % un problème de distribution, pas de moteur »* ; après huit phases, c'est encore la
phrase la plus juste du document.

## Sources

Consultées le 6 août 2026 :

- [k6 2.0 release — Grafana Labs](https://grafana.com/blog/k6-2-0-release/)
- [k6 Extensions xk6 Complete Reference 2026](https://qaskills.sh/blog/k6-extensions-xk6-complete-reference)
- [JMeter vs k6 vs Gatling 2026 — modèles ouvert et fermé](https://qaskills.sh/blog/jmeter-vs-k6-vs-gatling-2026)
- [Gatling — rapports statiques HTML](https://docs.gatling.io/reference/stats/reports/oss/)
- [NBomber — framework distribué .NET](https://nbomber.com/)
- [Coordinated Omission — Red Hat Performance](https://redhatperf.github.io/post/coordinated-omission/)

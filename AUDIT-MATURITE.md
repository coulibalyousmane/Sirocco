# Audit de maturité — avant la première publication

Audit du dépôt au commit `1ee092d` (branche `main`), à la demande explicite : **Sirocco est-il assez
mûr pour qu'un inconnu l'installe et le mette dans sa CI, et pour qu'un tiers écrive une extension
qui en dépende ?**

Ce document ne cherche pas les mêmes choses que son prédécesseur. [AUDIT.md](AUDIT.md) est un audit
transversal (`SEC`/`FONC`/`QUAL`/`DEP`/`ARCH`/`GOUV`, 21 constats) mené le 23 août à `ff324e8`,
cadré « juste avant la première publication publique ». Ses cinq priorités pré-tag sont traitées, et
tous ses constats de sécurité aussi, sauf `SEC-5` (frontière de confiance assumée). **Mais la
publication qu'il anticipait n'a pas eu lieu**, et quatorze commits ont passé depuis. Son horizon est
périmé, pas ses conclusions.

La différence de nature compte : `AUDIT.md` demandait « ce code est-il correct ? ». Celui-ci demande
« ce projet est-il **dépendable** ? » — ce qui porte moins sur le code que sur ce qui est **promis**,
**versionné**, **documenté** et **vérifié** autour de lui. Les chantiers sont numérotés `M1..M10`
pour ne pas entrer en collision avec les précédents.

**Le bloc pré-tag est traité le 27 août 2026** : `M1`, `M2`, `M5`, `M6`, `M9` et `M10`, c'est-à-dire
tout ce qui devient irréversible au premier tag ou faux dès la première minute. Restent ouverts
`M3`, `M4`, `M7` et `M8` — voir les priorités en fin de document.

## Méthode

Chaque constat est adossé à une commande ou à une lecture de code, référencée. Rien n'est déduit d'un
nom de fichier, et rien n'est supposé d'après la documentation — celle-ci est au contraire une des
sources auditées.

Outillé, réellement exécuté :

- `dotnet pack Sirocco.sln -c Release` — les dix `.nupkg` produits et listés ;
- `dotnet tool install -g Sirocco.Cli --add-source <source locale>` puis **un vrai tir depuis un
  répertoire hors du dépôt**, contre un `Sirocco.SampleTarget` réellement démarré : 188 itérations,
  0 échec, code de sortie 0 ; puis un second tir de 20 s pour trancher une hypothèse (voir `M7`) ;
- relevé des licences des **122 paquets** du graphe (directs et transitifs) en lisant les `.nuspec`
  et, quand la licence y est un fichier, le fichier lui-même dans le cache global ;
- inventaire des `TargetFramework`, des `IsPackable`, des `InternalsVisibleTo` et des types publics
  des quatre bibliothèques publiées ;
- lecture intégrale de `release.yml` et des quatre jobs de `ci.yml` ;
- recherche des mentions d'état du dépôt dans tout le markdown, les `.csproj` et les workflows.

**Hors périmètre, à énoncer plutôt qu'à laisser croire** : aucune vérification sur un vrai macOS
(machine indisponible — c'est précisément l'objet de `M4`) ; aucune mesure de performance de
l'injecteur lui-même, qui reste le second audit manquant du projet et n'est pas mené ici ; aucun
fuzzing des convertisseurs ; aucune installation depuis un vrai flux nuget.org, impossible avant le
tag — l'installation est donc prouvée depuis une source locale, ce qui exerce le même chemin de code
mais pas le même chemin réseau.

---

## Ce qui est déjà mûr

À dire d'abord, sans quoi l'audit ne renseigne pas sur l'état réel — et parce que deux de ces points
retirent des inquiétudes que je portais avant de mesurer.

- **Le chemin d'installation fonctionne déjà de bout en bout.** `dotnet tool install -g Sirocco.Cli`
  depuis une source locale installe la commande `sirocco` ; lancée depuis un répertoire quelconque,
  hors du dépôt, elle a exécuté un tir complet contre une vraie cible : `188 iterations en 3,13s,
  echecs 0, abandons 0`, rapport complet, code de sortie 0. Il n'y a **rien à réparer** dans ce
  chemin : il manque le tag, pas du code.
- **Zéro dépendance copyleft, sur les 122 paquets du graphe.** MIT, Apache-2.0 et BSD-3-Clause
  uniquement ; les deux paquets dont le `.nuspec` déclare une licence-fichier (`Bogus`, `MQTTnet`)
  sont MIT, lu dans le fichier ; `Fractions` est en clauses BSD de redistribution. Aucun conflit avec
  l'Apache-2.0 du dépôt. C'était l'inquiétude la plus asymétrique — publier une transitive copyleft
  est irréversible — et la mesure la lève.
- **`release.yml` est le fichier le plus mûr du dépôt.** Trois garde-fous sur un geste irréversible,
  chacun adossé à une erreur réellement possible : cohérence tag ↔ `<Version>`, suite de tests, et
  **compte exact des dix paquets attendus**, nommés un par un. Ce dernier a déjà servi : il a attrapé
  l'absence de `Sirocco.Extensions.Browser` dans la solution le 27 août. La clé d'API nuget.org ne
  quitte jamais les secrets du dépôt.
- **`IsPackable=false` est le défaut, explicitement.** `Directory.Build.props` inverse le défaut du
  SDK avec le bon raisonnement écrit à côté : un envoi sur nuget.org est définitif, donc le mode de
  défaillance doit être « un paquet manque » et jamais « un identifiant est squatté pour toujours ».
- **Le contrat d'extension est cohérent avec ce qu'il prescrit.** Les cinq extensions de référence ne
  référencent que `Sirocco.Domain` — exactement ce que [docs/extensions/guide.md:81](docs/extensions/guide.md)
  demande à un tiers. La convention n'est pas seulement écrite, elle est suivie par ses propres
  exemples.
- **Métadonnées de paquet complètes sur les dix** : `PackageLicenseExpression` Apache-2.0,
  `RepositoryUrl`, `PackageProjectUrl`, une `Description` propre à chaque paquet et un `README.md`
  embarqué par paquet.
- **Les erreurs de première minute sont actionnables.** `sirocco run` sans cible sort en code 1 avec
  `--target-url est requis (ou une valeur Sirocco:TargetBaseUrl dans un appsettings.json du
  repertoire courant)` — le message dit à la fois quoi faire et où le mettre.

---

## M1 — Faux motif : la documentation dit à un inconnu que le dépôt est privé ✅ traité

**Élevé** — c'est la première chose que lit un visiteur, et c'est faux.

Le dépôt est **public depuis le 5 août 2026** ([ROADMAP.md:617](ROADMAP.md)). Or **neuf passages, dans
six fichiers**, affirment ou impliquent le contraire, et s'en servent pour expliquer pourquoi rien
n'est installable :

| Fichier | Ce qui est écrit |
|---|---|
| [docs/index.md:17](docs/index.md) | « ne fonctionne aujourd'hui que depuis une source locale ou un flux privé » |
| [docs/demarrer/installation.md:53](docs/demarrer/installation.md) | « Pas encore publié sur nuget.org — **le dépôt reste privé** » |
| [docs/demarrer/installation.md:139](docs/demarrer/installation.md) | « pas encore publiés sur nuget.org (le dépôt reste privé) » |
| [docs/extensions/contrat.md:44](docs/extensions/contrat.md) | « uniquement parce que le dépôt reste privé » |
| [docs/extensions/guide.md:82](docs/extensions/guide.md) | « `ProjectReference` tant que ce dépôt reste privé » |
| [ROADMAP.md:121](ROADMAP.md), [:135](ROADMAP.md), [:684](ROADMAP.md) | idem, trois fois |
| [src/Sirocco.Cli/Sirocco.Cli.csproj:41](src/Sirocco.Cli/Sirocco.Cli.csproj) | « le depot reste prive (bullet suivant de la meme phase) » |

(`SECURITY.md:8` est exclu : « le rapport reste privé jusqu'à publication d'un correctif » est exact
et parle d'autre chose.)

**Pourquoi c'est plus grave qu'une coquille.** Ce n'est pas seulement périmé, c'est un **motif
faux** : la raison pour laquelle un inconnu ne peut pas installer Sirocco n'est pas que le dépôt
serait fermé — il peut le cloner à l'instant — c'est qu'**aucun tag n'a jamais été poussé**. Un
lecteur à qui l'on donne une mauvaise cause en tire une mauvaise conclusion : que le projet n'est pas
ouvert, alors qu'il l'est. Sur `docs/extensions/guide.md`, l'effet est direct : on lui dit d'utiliser
un `ProjectReference` « tant que le dépôt reste privé », donc de faire exactement ce que la convention
d'extension cherche à éviter.

**Coût** : une passe de rédaction, aucun code. C'est le constat au meilleur rapport de tout ce
document.

**Traité le 27 août 2026.** Chaque passage énonce désormais le motif réel — *la publication est
déclenchée par un tag `vX.Y.Z`, et aucun n'a été poussé* — au lieu d'une fermeture du dépôt qui
n'existe pas. Une incohérence interne au passage : `ROADMAP.md` affirmait le dépôt privé ligne 136 et
public ligne 139, à trois lignes d'écart.

**Correction de ce document lui-même** : il y avait **dix** passages, pas neuf. Le dixième
(`ROADMAP.md:111`, « Tant qu'il faut cloner un dépôt privé ») a échappé à la recherche de l'audit et
n'a été trouvé qu'en traitant le constat. Il est traité différemment des autres : c'est l'exposé du
problème que la phase 1 devait résoudre, donc il passe au passé plutôt que d'être réécrit. Un audit
qui recompte son propre décompte doit le dire.

**Contrôle** : la recherche qui avait produit le constat ne rend plus aucune affirmation au présent ;
`docfx build --warningsAsErrors` passe à 0 avertissement, ce qui vérifie au passage tous les liens
ajoutés.

---

## M2 — 121 types publics, aucune frontière de contrat déclarée ✅ traité

**Élevé** — et c'est le seul constat de cet audit qui devient **irréversible** au tag.

Les quatre bibliothèques publiées exposent :

| Paquet | Types publics | Fichiers |
|---|---:|---:|
| `Sirocco.Domain` | 52 | 50 |
| `Sirocco.Application` | 20 | 27 |
| `Sirocco.Infrastructure` | 3 | 5 |
| `Sirocco.Scenarios` | 46 | 36 |
| **Total** | **121** | 118 |

Aucun analyseur d'API publique n'est référencé (`Microsoft.CodeAnalysis.PublicApiAnalyzers` absent),
aucune baseline `PublicAPI.Shipped.txt`, et **aucune politique de versionnement n'est écrite nulle
part** — ni SemVer, ni règle de rupture, ni distinction entre « contrat que je tiens » et « détail
d'implémentation qui se trouve être public ».

**Ce que le tag fige.** À la seconde où `0.1.0` part sur nuget.org, ces 121 types deviennent tous
quelque chose contre quoi un tiers peut compiler — et sous SemVer, en casser un seul impose un
majeur. Or le guide d'extension ne demande à un tiers que **`Sirocco.Domain`**, soit 52 de ces types.
Les 69 autres sont publiés avec une promesse de stabilité que personne n'a formulée : ni identique à
celle de `Domain`, ni explicitement moindre.

Le cas de `Sirocco.Scenarios` est le plus net : 46 types publics, dont le chargeur déclaratif et le
résolveur de plugins NuGet, c'est-à-dire de la mécanique interne au moteur. Sa `Description` la
présente comme « à réutiliser ou copier », ce qui est une intention légitime — mais une intention
n'est pas un contrat.

**Ce que je ne prétends pas.** Je n'affirme pas que ces 69 types devraient être internes : composer
son propre hôte à partir de `Application`/`Infrastructure` est un usage annoncé. Le défaut n'est pas
la surface, c'est **l'absence de frontière déclarée** sur cette surface. Un signe concret : j'ai
ouvert `InternalsVisibleTo` sur `Sirocco.Host` le 27 août pour un test unitaire — geste anodin, sauf
que rien dans le dépôt ne dit ce qu'un tel geste engage, ni ce qu'il n'engage pas.

**Coût** : une page de politique de version (une demi-heure) ; et si l'on veut la faire mordre,
l'analyseur d'API publique avec sa baseline (un après-midi, et il transforme toute rupture en erreur
de compilation).

**Traité le 27 août 2026** par [docs/projet/versionnement.md](docs/projet/versionnement.md), câblée
dans la table des matières du site et dans le tableau `## Documentation` du README. Elle déclare deux
niveaux :

- **niveau 1, engagement de compatibilité** : l'API publique de `Sirocco.Domain`, **les options et
  les codes de sortie de la CLI**, le schéma des scénarios déclaratifs, et le JSON de `/report` en
  additif seulement ;
- **niveau 2, sans garantie avant la 1.0** : `Application`, `Infrastructure`, `Scenarios` et les cinq
  extensions de référence.

**Décisions prises dans ce chantier**, à contester si elles ne conviennent pas : mettre la CLI au
niveau 1 (c'est d'elle que dépend une CI, pas des types) ; poser que corriger un biais de mesure
n'est **pas** une rupture même si les chiffres publiés changent — point propre à un outil de mesure,
et directement pertinent après la correction de la file de jetons ; `[Obsolete]` pendant au moins une
mineure avant toute suppression.

**Résidu assumé, écrit dans la page elle-même** : rien n'outille cette politique. L'analyseur d'API
publique n'est pas posé — il demande de figer une baseline des 52 types de `Sirocco.Domain`, ce qui
est un chantier à part. Une rupture involontaire reste donc possible ; seul le `CHANGELOG` la
rattrapera, après coup.

---

## M3 — Aucun moyen de savoir quelle version tourne

**Moyen** — vérifié sur l'outil réellement installé, pas sur le code.

```text
$ sirocco --version
Commande inconnue : '--version'. Seule 'run' existe pour l'instant.
-> code de sortie 1
```

Sirocco a **deux canaux de distribution** : l'outil `dotnet tool` et les quatre binaires autonomes de
`release.yml`. Pour le premier, `dotnet tool list -g` donne la version du paquet. Pour le second — un
`sirocco.exe` téléchargé depuis une release GitHub, sans SDK ni gestionnaire de paquets autour — il
n'existe **aucun** moyen d'établir ce qu'on exécute.

**Pourquoi ça compte pour la maturité, pas pour le confort.** La condition de réouverture n°2 de la
roadmap attend « des issues ouvertes par des inconnus décrivant un usage réel ». La première question
sur une issue est toujours la même : quelle version ? Aujourd'hui, ni le rapporteur ni le mainteneur
ne peuvent y répondre.

Illustration involontaire trouvée pendant l'audit : la machine de développement porte encore un outil
global `tempest.cli 0.1.0`, vestige d'avant le renommage — **même numéro de version** que celui que
Sirocco va publier. Deux binaires différents, un même `0.1.0`, et rien pour les distinguer.

**Coût** : une branche dans le parseur, lisant l'`InformationalVersion` de l'assemblage. Une heure.

---

## M4 — Deux des quatre binaires publiés n'ont jamais été exécutés par personne

**Moyen**.

`release.yml` publie quatre archives : `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`. Les quatre
jobs de `ci.yml` (`build-and-test`, `format-benchmark`, `smoke-e2e`, `docs`) tournent tous sur
`ubuntu-latest`, sans matrice.

| Plateforme publiée | Couverte par |
|---|---|
| `linux-x64` | la CI, y compris le `smoke-e2e` qui exécute les exemples |
| `win-x64` | les tirs manuels du mainteneur, dont ceux de cet audit |
| `osx-x64` | **rien** |
| `osx-arm64` | **rien** |

Compiler pour macOS et l'expédier sans qu'aucune exécution n'ait jamais eu lieu, c'est offrir à un
inconnu sur Mac une chance non nulle d'un premier contact cassé — et le premier contact ne se rejoue
pas. Le risque n'est pas théorique : le dépôt a déjà connu un bug de packaging par RID qui a cassé la
CI ([Sirocco.Cli.csproj:28](src/Sirocco.Cli/Sirocco.Cli.csproj)), exactement dans cette zone.

**Coût** : une matrice `runs-on: [ubuntu-latest, windows-latest, macos-latest]` sur `build-and-test`
seul — les runners macOS sont plus chers, mais c'est le job le plus court. Une demi-journée avec les
ajustements de chemins.

---

## M5 — Aucun symbole, aucun SourceLink, aucune construction reproductible ✅ traité

**Moyen** — coût quasi nul, bénéfice permanent.

Recherche sur tous les `.csproj`, `.props` et workflows : **aucune** occurrence de `SourceLink`,
`IncludeSymbols`, `SymbolPackageFormat`, `ContinuousIntegrationBuild`, `EmbedUntrackedSources` ni
`PublishRepositoryUrl`. `Deterministic=true` est bien posé dans `Directory.Build.props`, mais c'est le
défaut du SDK et il ne suffit pas : sans `ContinuousIntegrationBuild=true` au moment du pack, les
chemins absolus du runner sont embarqués et la construction n'est pas vérifiable.

Conséquence pour un consommateur : il référence `Sirocco.Domain`, pose un point d'arrêt, et ne peut
pas entrer dans le code. Pour une bibliothèque dont tout l'argument est la justesse de la mesure,
c'est le mauvais endroit pour être opaque.

**Coût** : quatre propriétés MSBuild et `--include-symbols` au pack. Une heure — et bien plus cher à
rattraper après, parce que les paquets déjà publiés resteront sans symboles pour toujours.

**Traité le 27 août 2026** dans `Directory.Build.props` : `IncludeSymbols`, `SymbolPackageFormat`
(`snupkg`), `PublishRepositoryUrl`, `EmbedUntrackedSources`, et `ContinuousIntegrationBuild`
**conditionné à `GITHUB_ACTIONS`** — le poser en local normaliserait les chemins de source et
empêcherait le débogueur de retrouver les fichiers sur le disque. Aucune référence
`Microsoft.SourceLink.*` n'a été nécessaire : depuis .NET 8, le SDK l'embarque.

**Vérifié sur les artefacts, pas sur l'intention** : `dotnet pack Sirocco.sln -c Release` produit
**10 `.nupkg` et 10 `.snupkg`** ; le symbole de `Sirocco.Domain` contient bien
`lib/net10.0/Sirocco.Domain.pdb` ; et son `.nuspec` porte désormais
`<repository … commit="1ee092db1b8b57de0b78ff30a9529e657447d6a8" />`, donc un paquet publié est
traçable au commit exact — ce qui n'était pas le cas avant, `RepositoryUrl` seul n'embarquant pas le
SHA.

**Effet de bord contrôlé** : le garde-fou des dix paquets de `release.yml` teste `ls *.nupkg`, glob
qui **ne capte pas** les `.snupkg` (`foo.snupkg` ne se termine pas par `.nupkg`). Vérifié en
exécutant la comparaison exacte du workflow contre la vraie sortie de `pack` : les dix noms attendus,
ni un de plus.

---

## M6 — `net10.0` seul, sans politique de support énoncée ✅ traité

**Faible**, mais à écrire avant le tag.

Les dix projets publiables ciblent `net10.0` et rien d'autre. Un consommateur sur `net8.0` — LTS,
supporté jusqu'en novembre 2026 — ne peut pas référencer `Sirocco.Domain`, donc ne peut pas écrire
d'extension.

C'est un choix **défendable** : .NET 10 est LTS, et le moteur emploie des constructions récentes. Le
défaut n'est pas le choix, c'est qu'il n'est **écrit nulle part** — donc indistinguable d'un oubli, et
impossible à opposer à quelqu'un qui ouvrira une issue pour demander `net8.0`. Une ligne dans la
politique de version (`M2`) répond aux deux.

**Coût** : deux phrases. Multi-cibler, si la demande existe un jour, est un autre chantier et ne doit
pas être décidé ici.

**Traité le 27 août 2026**, dans la section « Support et compatibilité » de la politique de version.
Le choix y est énoncé avec sa conséquence — un projet sur `net8.0` ne peut pas écrire d'extension —
plutôt que laissé à deviner, et il gagne un **engagement** qui n'existait pas : la cible ne sera pas
relevée au sein d'un même majeur, parce que passer à `net11.0` casserait les consommateurs restés sur
`net10.0`. Le multi-ciblage reste hors périmètre tant que personne ne le demande, et la page invite à
ouvrir une issue décrivant la contrainte.

---

## M7 — La colonne du différenciateur n'est pas lisible par un inconnu

**Moyen** — et c'est le constat le plus intéressant de l'audit, parce qu'il ne porte pas sur un défaut
mais sur une **mesure exacte et pourtant trompeuse**.

Deux tirs réels, même cible, même effectif, seule la durée change :

| Tir | Itérations | Durée réelle | `__iteration` p99 | **dette max** |
|---|---:|---:|---:|---:|
| `--vus 4 --duration 3s` | 188 | 3,13 s | 598 ms | **586,2 ms** |
| `--vus 4 --duration 20s` | 1 510 | 20,14 s | 163 ms | **390,9 ms** |

La dette max **baisse** quand le tir s'allonge. Elle n'est donc pas un signal de saturation : c'est un
**transitoire de démarrage**. La cause est visible dans le rapport lui-même — l'étape `login` a
`n = 4`, une fois par utilisateur virtuel, à 274-387 ms de p50 : pendant que les quatre VUs font leur
première itération anormalement longue, la file (désormais plafonnée à l'effectif, soit 4 jetons) se
remplit, et ces jetons affichent l'attente correspondante.

Rien n'était saturé : le tir de 20 s a tenu sa durée **à 0,14 s près**, et son p99 est de 163 ms.

**Pourquoi c'est un défaut de maturité et pas un bug.** La mesure est juste — ces jetons ont réellement
attendu. Mais `dette max` est un maximum sans horodatage, sans fenêtre d'échauffement et sans
percentile, présenté au même rang que des p50/p95/p99. Le rapport avertit déjà, en modèle fermé, que
les chiffres ne sont pas comparables à un modèle ouvert ; il ne dit rien de ce transitoire. Un inconnu
lit `dette max 586 ms` sur un p99 de 598 ms et conclut que sa cible sature. C'est **exactement la
colonne** que ni k6, ni Gatling, ni NBomber ne publient — le seul `●` isolé du tableau concurrentiel.
Elle devrait être la plus interprétable du rapport ; elle est la moins.

**Note de méthode** : ce constat n'existe que parce que la file de jetons a été corrigée le 27 août.
Avant, le transitoire était noyé dans un artefact bien plus gros — 63 s de dette fantôme sur un tir de
10 s. Corriger un défaut rend le suivant visible.

**Coût** : rapporter l'instant du maximum, ou un percentile de dette à côté du maximum. Ce n'est pas
une correction mais une décision de produit — et elle gagne à être prise avant que le chiffre ne soit
lu par des tiers.

---

## M8 — Rien pour accueillir la contribution que la roadmap attend

**Faible**.

Absents du dépôt, vérifié par `ls` : `CHANGELOG.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`,
`.github/ISSUE_TEMPLATE/`, `.github/PULL_REQUEST_TEMPLATE.md`, `.github/dependabot.yml`. Présents :
`LICENSE`, `SECURITY.md`.

`AUDIT.md` porte déjà `GOUV-2` (pas de `CHANGELOG`) et `DEP-2` (pas de `dependabot`) en « Faible ».
L'angle de maturité les recadre : la condition de réouverture n°2 de la roadmap fait des **issues
ouvertes par des inconnus** le signal d'adoption le plus honnête disponible. Le dépôt n'offre à ces
inconnus aucun gabarit, aucune indication de ce qu'il attend d'un rapport, et aucun `CHANGELOG` pour
qu'ils sachent ce qui a changé entre deux versions. On mesure une adoption dont on n'a pas préparé
l'arrivée.

**Coût** : une demi-journée pour les trois fichiers utiles — `CHANGELOG`, `CONTRIBUTING`, et un
gabarit d'issue de bug demandant la version, qui n'existe pas encore (`M3`).

---

## M9 — Le document qui explique la politique d'empaquetage ne la décrit plus ✅ traité

**Info**.

`Directory.Build.props` commente ses métadonnées comme « communes à tous les paquets publiables du
dépôt (`Sirocco.Domain`, `Sirocco.Application`, `Sirocco.Infrastructure`, `Sirocco.Scenarios`,
`Sirocco.Cli`) », puis parle des « cinq projets réellement publiés ». Il y en a **dix** depuis le
26 août : les cinq extensions de référence sont devenues publiables.

Le garde-fou de `release.yml` a bien été porté de cinq à neuf puis à dix paquets ; ce commentaire ne
l'a pas suivi. C'est mineur — sauf que c'est précisément le texte qui explique *pourquoi* le défaut est
`IsPackable=false`, c'est-à-dire la barrière qui empêche de squatter un identifiant nuget.org par
inadvertance. Un raisonnement qu'on garde doit rester exact, sinon il finit par sauter.

**Coût** : deux lignes.

**Traité le 27 août 2026** : le commentaire énumère les dix paquets en deux groupes (cinq
bibliothèques, cinq extensions), « les cinq projets réellement publiés » devient « les dix », et il
renvoie désormais à la politique de version pour ce que chaque groupe promet — le lecteur qui vient
comprendre pourquoi `IsPackable=false` est le défaut trouve du même coup à quel niveau chaque paquet
s'engage.

---

## M10 — La release n'est pas atomique ✅ traité

**Faible**.

Dans `release.yml`, les jobs `binaries` et `nuget` n'ont aucune relation `needs` : ils tournent en
parallèle sur le même tag. Le job `nuget` place bien ses trois garde-fous **avant** le `dotnet nuget
push`, ce qui rend le mode de défaillance probable rassurant — « rien n'est publié » plutôt que
« moitié publié ». Mais rien n'empêche l'issue suivante : `binaries` réussit et crée la release
GitHub, `nuget` échoue sur le contrôle tag ↔ version, et il reste un tag `v0.2.0` avec une release
publique portant quatre binaires, zéro paquet, et un `Directory.Build.props` qui dit encore `0.1.0`.

**Coût** : un `needs:` sur le job `binaries`, ou un job de contrôle préalable dont les deux dépendent.
Dix minutes.

**Traité le 27 août 2026** par la seconde forme, plus sûre que le simple `needs:` : les deux contrôles
(tag ↔ version, suite de tests) quittent le job `nuget` pour un job **`verifications`** dont les deux
autres dépendent, et `binaries` dépend en plus de `nuget`. L'ordre devient donc
`verifications → nuget → binaries` : le geste vraiment irréversible passe en premier, et aucun effet
de bord n'a lieu si un contrôle échoue. Un quatrième garde-fou est ajouté au passage — le compte des
dix `.snupkg`, qui attrape une régression silencieuse sur `IncludeSymbols` que le compte des `.nupkg`
ne verrait pas.

**Modes de défaillance après correction**, énoncés parce que le dernier subsiste :

| Ce qui échoue | Conséquence |
|---|---|
| tag ↔ version, ou les tests | rien n'est publié, ni release ni paquets |
| l'envoi nuget.org | pas de release GitHub non plus |
| la création de la release GitHub | **paquets publiés, pas de release** — rattrapable à la main (relancer le job, ou `gh release create`), contrairement à son inverse |

**Vérification, et sa limite** : `release.yml` ne se déclenche que sur un tag, donc ce changement
**n'a pas pu être exercé de bout en bout**. Ce qui l'a été : le fichier est parsé par YamlDotNet via
le SDK du dépôt, et le graphe rendu est bien `verifications` (sans dépendance, 4 étapes), `nuget`
(dépend de `verifications`, 6 étapes), `binaries` (dépend des deux, 4 étapes) ; et les deux
garde-fous de comptage ont été exécutés tels quels contre la vraie sortie de `dotnet pack`. La
première vraie exécution restera le premier tag poussé.

---

## La réponse en une ligne

**Le produit était mûr, le contrat ne l'était pas.** Tout ce qui se mesure fonctionnait déjà — le
moteur, les tests, la CI, le packaging, l'installation, jusqu'au tir réel depuis un outil global
installé. Presque rien de ce qui se **promet** n'était écrit : ni ce qui est stable, ni ce qui est
supporté, ni ce qui a changé, ni ce que le chiffre phare veut dire.

**Après le bloc pré-tag** : ce qui est stable et ce qui est supporté sont désormais écrits, et le
premier tag ne peut plus produire une release incohérente ni des paquets sans symboles. Restent hors
de l'écrit : **ce qui a changé** (`M8`, faute de `CHANGELOG`) et **ce que la dette max veut dire**
(`M7`). Un inconnu peut utiliser Sirocco et sait maintenant sur quoi il s'engage en en dépendant ; il
ne sait toujours pas quelle version il exécute (`M3`).

## Priorités

**Avant le tag `v0.1.0`** — ce qui devient irréversible, ou faux dès la première minute. **Les six
sont traités le 27 août 2026.**

| # | Pourquoi maintenant | État |
|---|---|---|
| `M1` | Un motif faux sur la page d'accueil, et il décourage exactement l'usage visé | ✅ dix passages réécrits, dont un que l'audit avait manqué |
| `M2` | 121 types deviennent un contrat au tag ; après, chaque rupture coûte un majeur | ✅ politique de version en deux niveaux, non outillée (résidu énoncé) |
| `M5` | Les paquets publiés sans symboles resteront sans symboles pour toujours | ✅ 10 `.snupkg` et le commit dans le nuspec, prouvés sur les artefacts |
| `M6` | Deux phrases, et elles ferment une issue qui viendra | ✅ `net10.0` assumé, plus l'engagement de ne pas relever la cible dans un majeur |
| `M9` | Le raisonnement qui protège des identifiants squattés doit rester exact | ✅ cinq → dix paquets |
| `M10` | Évite une release publique incohérente sur le premier tag | ✅ `verifications → nuget → binaries`, plus un garde-fou sur les symboles |

Barrière repassée après correction : build Release **0 avertissement / 0 erreur**, **801 tests**
verts, `dotnet format --verify-no-changes --severity info` à 0 violation sur la solution **et** sur
les deux projets du harnais de benchmark, `docfx --warningsAsErrors` à 0 avertissement, et
`dotnet pack` produisant exactement 10 `.nupkg` + 10 `.snupkg`.

**Ensuite, avant que le premier inconnu n'arrive** : `M3` (version, sans quoi aucune issue n'est
exploitable), `M8` (de quoi accueillir cette issue), `M4` (ne plus expédier deux binaires jamais
exécutés).

**À décider, pas à corriger** : `M7`. Ce n'est pas un défaut à réparer mais un arbitrage de produit
sur la présentation du différenciateur — et il gagne à être tranché avant que le chiffre ne soit lu
par des tiers, pas après.

## Limites de cet audit

Trois, énoncées plutôt que laissées à découvrir :

1. **Aucune vérification sur macOS**, faute de machine — c'est l'objet de `M4`, pas sa résolution.
2. **La performance de l'injecteur lui-même n'est pas auditée.** C'est le second audit manquant du
   projet, identifié et volontairement non mené ici : il demande de vrais tirs de saturation, pas des
   lectures de code. La contre-épreuve « à partir de quand l'injecteur est-il lui-même le goulot ? »
   reste ouverte, comme l'énonce déjà l'article de fond.
3. **Un audit de maturité ne trouve pas les défauts de comportement.** Le bug de la file de jetons,
   corrigé le 27 août, n'aurait été trouvé ni par `AUDIT.md` ni par celui-ci : il a fallu un vrai tir
   navigateur avec des itérations de l'ordre de la seconde. `M7` n'existe d'ailleurs que grâce à cette
   correction. Ce document dit ce qui est promis et ce qui manque autour du code — pas ce que le code
   fait sous charge.

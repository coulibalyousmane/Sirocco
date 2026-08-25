# Pipeline CI

Tout l'outillage orienté CI (seuils, `ExitAfterRun`, `Sirocco.Compare`) restait, jusqu'ici,
jamais exercé automatiquement. `.github/workflows/ci.yml` ferme cette boucle sur chaque push et
pull request vers `main`, en deux temps :

- **`build-and-test`** — restauration, compilation en `Release`, suite de tests complète **avec
  mesure de couverture** (voir ci-dessous), `dotnet format --verify-no-changes`.
- **`smoke-e2e`** — un **vrai tir**, pas seulement des tests unitaires : démarre
  `Sirocco.SampleTarget`, attend qu'il réponde, puis lance `Sirocco.Host` en mode autonome
  avec des seuils configurés et `ExitAfterRun=true` contre lui. C'est ce genre de vérification
  qui a déjà révélé des bugs réels dans ce projet (limite Kestrel HTTP/1.1+2, `UriFormatException`
  sur l'hôte `+`, `TargetUri` jamais propagé aux workers) — aucun test isolé ne les aurait
  trouvés. Seuils volontairement larges (P95 < 1 000 ms) : ce job vérifie que la chaîne
  seuils → code de sortie fonctionne en CI, pas la performance elle-même, qui varie trop d'un
  runner partagé à l'autre pour un seuil serré.

Vérifié en local, commande par commande, avant de pousser le workflow (pas d'accès direct aux
runs GitHub Actions depuis cet environnement — `gh` n'est pas installé, cf. plus haut) : restore,
build, 299 tests, format, puis un tir de fumée réel avec les mêmes seuils — code de sortie 0.

## Couverture de code

`coverlet.collector` était référencé par `Sirocco.UnitTests.csproj` depuis toujours **sans qu'aucune
étape ne s'en serve** : des tests verts, sans savoir quelles branches étaient exercées. Le job
`build-and-test` collecte désormais la couverture à chaque run, la rend lisible et la fait mordre.

La chaîne est en trois temps :

1. `dotnet test --collect:"XPlat Code Coverage" --results-directory coverage` produit un rapport
   Cobertura.
2. **ReportGenerator** (déclaré dans `.config/dotnet-tools.json`, donc reproductible en local par
   `dotnet tool restore`) en tire trois sorties : un tableau markdown poussé dans le **résumé du
   run** — le chiffre se lit sans ouvrir d'artefact —, un `Summary.json` qui sert de verdict, et un
   **rapport HTML publié en artefact** (`couverture-html`, 14 jours). Le tableau dit *combien*, le
   rapport HTML dit *où* : c'est ce qui rend la mesure actionnable plutôt que décorative.
3. Un **plancher** fait échouer le job si la couverture de lignes passe sous 65 %.

Trois choix méritent d'être énoncés plutôt que subis :

**Pas de Codecov ni de Coveralls.** Ce n'est pas une préférence esthétique : `ci.yml` ne référence
aujourd'hui aucun secret, donc son déclencheur `pull_request` ne peut rien exfiltrer depuis un fork.
Ajouter un jeton d'envoi à un service tiers détruirait précisément cette propriété, sur un dépôt
public, pour afficher un chiffre que le résumé du run donne déjà.

**Un plancher, pas une cible.** Il existe pour empêcher le constat de retomber à l'état où l'audit
l'a trouvé — mesurable mais jamais mesuré — pas pour réclamer un chiffre plus flatteur. Il est
volontairement sous la mesure réelle : il doit attraper la disparition des tests d'un sous-système,
pas signaler la variation normale d'un runner partagé.

**Aucun filtre d'exclusion.** Le chiffre publié est brut. Écarter du code généré ou des chemins
« non testables » gonflerait la mesure sans rien changer à ce qui est réellement vérifié.

### Ce que la mesure dit du projet

Mesure en `Release`, la configuration de la CI : **70,8 % de lignes, 66,7 % de branches**
(5 449 lignes couvertes sur 7 693). Le détail par assembly confirme, chiffres à l'appui, ce que les
commentaires de `Sirocco.UnitTests.csproj` annonçaient déjà — les zones basses sont celles qui sont
**délibérément** vérifiées par de vrais tirs plutôt que par des tests unitaires :

| Assembly | Lignes | Pourquoi |
|---|---:|---|
| `Sirocco.Application` | 94,3 % | Cœur métier, testé directement |
| `Sirocco.Domain` | 92,9 % | Idem |
| `Sirocco.Infrastructure` | 91,4 % | Idem |
| `Sirocco.Extensions.*` | 71,6 → 87,3 % | Chaque protocole de référence a son vrai serveur/courtier en boucle locale |
| `Sirocco.*Convert` | 78,9 → 80 % | Convertisseurs (logique pure) |
| `Sirocco.Scenarios` | 70,5 % | Inclut du code généré (protobuf, générateur de regex) non exclu du calcul |
| `Sirocco.Operator` | 66,1 % | `TestRunResources` est testé ; `TestRunController` ne l'est pas — KubeOps n'offre pas de harnais sans cluster réel |
| `sirocco` (CLI) | 56,2 % | Le parseur d'arguments est testé, la commande elle-même est exercée par `smoke-e2e` |
| `Sirocco.RecorderProxy` | 51,3 % | Logique pure testée ; le forwarding HTTP est vérifié par un vrai tir |
| `Sirocco.Host` | 14 % | Seule `ClusterAuthentication` y est testée unitairement. Tout le câblage distribué (maître, workers, orchestration) est vérifié par de vrais tirs et par `smoke-e2e` |

Autrement dit : **le chiffre bas de `Sirocco.Host` n'est pas une lacune de tests, c'est la
contrepartie mesurée d'un choix de vérification.** Une couverture unitaire élevée y demanderait des
doubles de test du control plane — exactement ce que ce projet a refusé de faire, au profit de tirs
réels qui ont trouvé des bugs qu'aucun double n'aurait révélés.

Pour reproduire la mesure en local :

```bash
dotnet tool restore
dotnet test tests/Sirocco.UnitTests --configuration Release --collect:"XPlat Code Coverage" --results-directory coverage
dotnet reportgenerator -reports:"coverage/**/coverage.cobertura.xml" -targetdir:coverage/report -reporttypes:"Html;TextSummary"
```


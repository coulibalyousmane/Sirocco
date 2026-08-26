# Sirocco.Extensions.Browser

Extension [Sirocco](https://github.com/coulibalyousmane/Sirocco) pour les **tests navigateur** :
elle pilote un vrai Chromium via [Playwright](https://playwright.dev/dotnet/) et rapporte les
**Web Vitals mesurés par le navigateur** — LCP, FCP, TTFB et CLS.

C'est le seul protocole de référence qui ne parle aucun protocole réseau lui-même : le scénario
conduit un navigateur, et ce sont les mesures du navigateur qui remontent dans le rapport.

## ⚠️ Modèle fermé obligatoire

Un contexte de navigateur coûte des centaines de Mo et une navigation prend des secondes. Cette
extension tourne à **concurrence à un chiffre**, pas à 50 utilisateurs virtuels.

Pilotez-la en `--vus` (modèle fermé), **jamais** en `--rps`. Sous un profil en débit, le moteur
serait en dette d'ordonnancement permanente et rapporterait une dette catastrophique à chaque tir :
exact, et parfaitement inutile. Le partage habituel s'applique — le navigateur *mesure
l'expérience*, un tir protocolaire *génère la charge*, les deux côte à côte.

## ⚠️ À consommer par `PackageReference`, pas par `--plugin-package`

Playwright télécharge des binaires de navigateur, hors du `lib/<tfm>` d'un paquet. Comme
[`Sirocco.Extensions.Sql`](https://www.nuget.org/packages/Sirocco.Extensions.Sql), cette extension
passe donc par MSBuild :

```bash
dotnet new classlib -o mon-tir-navigateur && cd mon-tir-navigateur
dotnet add package Sirocco.Extensions.Browser
dotnet publish -c Release -o publish

# Une fois, pour installer le navigateur lui-meme :
pwsh -File publish/playwright.ps1 install chromium

sirocco run publish/Sirocco.Extensions.Browser.dll --target-url http://localhost:5281 --vus 2 --duration 30s
```

## Ce que vous obtenez

| Nom | Forme | Pourquoi |
|---|---|---|
| `navigation` | étape | Le chargement de la page, mesuré par le plugin |
| `LCP`, `FCP`, `TTFB` | étapes | Des durées en millisecondes : publiées comme étapes, elles héritent des **centiles et des seuils** |
| `web_vitals_cls` | métrique `trend` | CLS est un score sans unité, pas une durée — voir la limite ci-dessous |

Les Web Vitals sont définis au **p75**, et `ResponseP75Milliseconds` existe déjà comme cible de
seuil. La cible standard s'écrit donc directement :

```bash
--threshold "LCP:ResponseP75Milliseconds:LessThanOrEqual:2500"
```

**Limite connue** : CLS n'a **ni centile ni seuil**. Les métriques personnalisées de type `trend`
n'exposent que min / moyenne / max, et un histogramme de millisecondes ne représente pas un score
fractionnaire de 0 à 1. On lit donc CLS en moyenne et en max, pas au p75.

**À lire correctement** : sur l'étape `navigation`, la colonne `Response` inclut l'attente en file
du modèle fermé (bornée à environ une itération) ; c'est `p99 brut` qui donne le temps de
chargement lui-même. Les trois vitals, eux, portent exactement la valeur du navigateur — ils sont
publiés sans dette d'ordonnancement, par construction.

## Configuration

| Variable | Défaut | Rôle |
|---|---|---|
| `SIROCCO_BROWSER_PLUGIN_PATH` | `/demo` | Chemin relatif à `--target-url` |
| `SIROCCO_BROWSER_PLUGIN_SETTLE_MILLISECONDS` | `600` | Attente avant relevé, pour laisser LCP et CLS se stabiliser |
| `SIROCCO_BROWSER_PLUGIN_TIMEOUT_SECONDS` | `30` | Délai maximal de navigation |
| `SIROCCO_BROWSER_PLUGIN_HEADED` | *(non défini)* | `1`/`true` pour voir le navigateur, utile au diagnostic |

Un **contexte de navigateur neuf par itération** (cache, cookies et stockage vides) : les Web Vitals
décrivent une *première visite*, qu'un cache déjà chaud fausserait. Le navigateur lui-même, en
revanche, est lancé une seule fois pour tout le tir.

## Écrire la vôtre

Voir le [guide d'écriture d'extension](https://github.com/coulibalyousmane/Sirocco/blob/main/docs/extensions/guide.md).

Publiée sous la licence Apache 2.0.

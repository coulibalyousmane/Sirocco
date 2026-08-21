# Tempest

Moteur de test de charge haute performance, asynchrone et *cloud-native*, écrit en C# / .NET 10.
Il tient **100 000 requêtes par seconde depuis une seule machine**, avec une mesure de latence
plus honnête que la plupart de ses concurrents.

```bash
git clone https://github.com/coulibalyousmane/Tempest.git
cd Tempest
dotnet run --project src/Tempest.Cli -- run --target-url https://votre-cible --rps 50 --duration 30s
```

Pas de scénario à écrire, pas de configuration : ces trois commandes suffisent pour un premier tir
contre n'importe quelle URL.

> Les paquets ne sont **pas encore publiés sur nuget.org** : `dotnet tool install -g Tempest.Cli`
> ne fonctionne aujourd'hui que depuis une source locale ou un flux privé. Un binaire autonome, qui
> ne demande ni `git clone` ni le SDK .NET, reste la voie la plus courte — voir
> [Installation](demarrer/installation.md).

## Le seul outil qui vous dit quand ses propres chiffres deviennent faux

Un modèle de charge ouvert (*arrival rate*) évite le biais du *coordinated omission* — mais
seulement **tant que l'injecteur tient la cadence**. Dès qu'il sature, l'écart réapparaît. k6,
Gatling et JMeter proposent tous un modèle ouvert ; aucun des trois ne montre le moment où il
décroche.

Tempest publie les deux mesures côte à côte, plus la dette d'ordonnancement maximale :

| Mesure | Ce qu'elle dit |
|---|---|
| **Response** | Ce que l'appelant attend réellement, file d'attente de l'injecteur incluse |
| **Service** | Le temps de traitement pur, une fois la requête prise en charge |
| **Dette d'ordonnancement** | Le retard accumulé par l'injecteur — l'écart entre les deux |

**L'écart entre Response et Service est la mesure du problème résiduel.** C'est vérifiable : le
[benchmark comparatif](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/results/RESULTS.md)
rejoue le même scénario contre la même cible saturée avec Tempest, k6, Gatling et NBomber, en une
commande.

## Par où commencer

| Vous voulez… | Allez voir |
|---|---|
| lancer un tir tout de suite | [Interface de ligne de commande](demarrer/cli.md) |
| décrire un parcours en YAML | [Format déclaratif](scenarios/declaratif.md) |
| des données variables, des assertions, des pauses | [Données, assertions et rythme](scenarios/donnees-assertions.md) |
| du branchement ou des boucles | [Scénarios scriptés (Roslyn)](scenarios/scripte.md) |
| partir d'un trafic déjà capturé | [Partir d'un trafic existant](convertisseurs/index.md) |
| autre chose qu'un débit cible | [Modèles de charge](charge/modeles.md) |
| lire le rapport | [Mesure et rapport](rapports/mesure.md) |
| échouer un build sur un seuil | [Seuils et comparaison](rapports/seuils-et-comparaison.md) |
| un protocole que Tempest ne connaît pas | [Guide d'écriture d'extension](extensions/guide.md) |
| dépasser une machine | [Mode distribué](distribue/mode-distribue.md), [Kubernetes](distribue/kubernetes.md) |

## Les exemples de cette documentation sont exécutables

Aucun exemple de ce site n'est recopié à la main dans une page : chacun est un **vrai fichier du
dépôt**, transclus ici et **exécuté par la CI** contre une vraie cible à chaque commit. Un exemple
qui cesserait de fonctionner casserait le build — il ne peut donc pas dériver silencieusement de ce
que le moteur fait réellement.

Ils vivent dans
[`docs/examples/`](https://github.com/coulibalyousmane/Tempest/tree/main/docs/examples) et se
lancent tels quels contre `Tempest.SampleTarget`, la cible de démonstration du dépôt.

## Ce que Tempest ne fait pas

Dit franchement, pour éviter une mauvaise surprise après installation :

- **Pas de test navigateur** ni de Web Vitals — c'est le terrain de k6 browser.
- **Pas d'écosystème d'extensions communautaire** : le [contrat de plugin](extensions/contrat.md)
  existe et quatre protocoles de référence le valident, mais il n'y a pas d'équivalent de `xk6`.
- **Pas de rapport au niveau de Gatling** sur l'analyse exploratoire : séries temporelles,
  histogrammes et courbe de dette sont là, mais en HTML statique et tableau de bord auto-rafraîchi,
  pas en interface d'exploration.
- **Le mode distribué ne prend pas tous les formats de scénario** : le déclaratif seul, pas le
  scripté ni les plugins.

La [roadmap concurrentielle](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md)
détaille cet écart phase par phase, sans le minimiser.

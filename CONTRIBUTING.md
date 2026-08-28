# Contribuer à Sirocco

Merci de vouloir aider. Ce document dit ce que le projet attend d'une contribution — pas des
politesses, les quatre ou cinq exigences concrètes qui font la différence entre une PR fusionnable et
une PR qui traîne.

## Signaler un problème

Ouvrez une issue avec la sortie de **`sirocco --version`** : elle donne la version, le commit dont
elle est issue, le runtime et le système. Sans elle, ni vous ni personne ne peut établir quel binaire
a produit le comportement décrit. Les gabarits d'issue la demandent en premier champ.

Pour une **faille de sécurité**, n'ouvrez pas d'issue : suivez [SECURITY.md](SECURITY.md).

## Ce qu'il faut pour compiler

Le SDK **.NET 10** et rien d'autre. Toute la chaîne du dépôt est `dotnet` — pas de Node, pas de
Python, pas de Make.

```bash
dotnet build Sirocco.sln
dotnet test tests/Sirocco.UnitTests
```

## La barrière avant de proposer une PR

Ces quatre commandes doivent passer. Elles sont exactement ce que la CI vérifie, donc les lancer en
local évite un aller-retour :

```bash
dotnet build Sirocco.sln --configuration Release
dotnet test tests/Sirocco.UnitTests --configuration Release
dotnet format Sirocco.sln --verify-no-changes --severity info
docfx build docs/docfx.json --warningsAsErrors
```

Deux pièges qui font échouer la CI sans échouer un `dotnet build` :

- **Le formatage n'est pas négociable.** `dotnet format` est l'arbitre, y compris sur les fins de
  ligne (LF) et l'encodage. Un fichier neuf déclenche presque toujours `FINALNEWLINE` et `CHARSET` :
  lancez `dotnet format Sirocco.sln --severity info` une fois avant de vérifier.
- **Les deux projets du harnais de benchmark sont hors de la solution** (NBomber est sous licence
  commerciale et n'a rien à faire dans le build du dépôt). Leur formatage se vérifie à part :
  `dotnet format benchmark/normalize --verify-no-changes --severity info`, idem pour
  `benchmark/nbomber`.

## Ce que le projet attend du code

- **La couverture a un plancher, vérifié en CI.** Une PR qui la fait descendre est refusée par la CI,
  pas par une opinion.
- **Les commentaires expliquent le pourquoi, jamais le quoi.** Le dépôt est commenté en français, et
  un commentaire qui paraphrase la ligne suivante sera relu comme du bruit. Un commentaire qui dit
  pourquoi une approche évidente a été écartée vaut de l'or — il y en a beaucoup dans le code, prenez
  modèle sur eux.
- **Pas de `--` dans un commentaire XML de `.csproj`** : MSBuild refuse de charger le fichier
  (`MSB4025`). Le piège a déjà coûté trois builds.
- Conventions de nommage : `SCREAMING_CASE` pour les `const`, `_camelCase` pour les champs statiques
  en lecture seule, `PascalCase` pour les propriétés. `dotnet format` les fait respecter.

## Ce que le projet attend d'une vérification

C'est le point où Sirocco est plus exigeant que la moyenne, et il vaut d'être dit explicitement :
**une fonctionnalité est vérifiée par un vrai tir, pas par un test qui simule le monde.**

Le dépôt teste ses protocoles contre de vrais serveurs en boucle locale (un vrai courtier MQTT, un
vrai serveur GraphQL, un vrai Kestrel pour SSE, une vraie base SQLite), jamais contre un double du
protocole. Si votre changement touche un comportement observable, montrez le tir dans la description
de la PR : la commande, et les chiffres qu'elle a produits.

Corollaire honnête : quand une vérification est **impossible** (pas de cluster, pas de machine macOS,
pas de flux NuGet), écrivez-le au lieu de laisser croire. Le dépôt fait ça partout, y compris dans ses
propres audits.

## Les exemples de documentation ne se recopient pas

Chaque exemple du site est un **vrai fichier** de [`docs/examples/`](docs/examples), transclus dans la
page et **exécuté par la CI** contre une vraie cible. N'écrivez pas un bloc de code dans une page de
doc : ajoutez le fichier, faites-le exécuter, et transcluez-le. Un exemple qui cesserait de
fonctionner casse le build — c'est voulu.

## Écrire une extension

Vous n'avez pas besoin de contribuer au dépôt pour ça, et c'est le but : une extension est une
bibliothèque .NET ordinaire dont la seule dépendance est `Sirocco.Domain`. Voir le
[guide d'écriture d'extension](docs/extensions/guide.md), et étiquetez votre paquet
`sirocco-extension` pour qu'il apparaisse dans l'index communautaire.

## Journal des modifications

Une PR qui change un comportement observable ajoute son entrée à [CHANGELOG.md](CHANGELOG.md). Si
elle corrige un biais de mesure, l'entrée dit **l'effet attendu sur les chiffres** : c'est la seule
façon pour un utilisateur de comprendre pourquoi son p99 a bougé sans que sa cible ait changé.

## Portée et compatibilité

Avant de proposer un changement d'API publique, lisez
[Versionnement, compatibilité et support](docs/projet/versionnement.md) : tout n'est pas au même
niveau d'engagement, et une rupture sur `Sirocco.Domain` ou sur les options de la CLI coûte un
majeur.

# Extensions publiées

Une extension Sirocco est un paquet NuGet ordinaire : rien ne la distingue techniquement d'une
autre bibliothèque. Pour qu'on puisse malgré tout la **trouver**, ce projet retient une convention
d'étiquette.

## La convention

Un paquet qui expose un `IWorkflow` chargeable par Sirocco porte l'étiquette **`sirocco-extension`**
dans ses `PackageTags` :

```xml
<PackageTags>load-testing;sirocco-extension;mon-protocole</PackageTags>
```

C'est tout. Il n'y a ni inscription, ni dépôt à ouvrir, ni validation à demander.

## Ce que cette étiquette n'est pas

**Ce n'est pas un adoubement.** N'importe qui peut la poser sur n'importe quel paquet, et la poser
ne fait pas d'un paquet quelque chose que ce dépôt aurait examiné, testé ou approuvé. NuGet ne
réserve pas de préfixe d'identifiant pour une convention communautaire : la découverte est ouverte,
l'autorité n'existe pas.

En pratique, cela veut dire que les précautions habituelles restent entièrement à votre charge :

- Un plugin s'exécute **dans le même processus** que le moteur, avec les mêmes droits. Il n'y a pas
  de bac à sable.
- Sirocco vérifie qu'un paquet de plugin est **signé** et que son contenu correspond à sa signature,
  jamais que le signataire est digne de confiance — un paquet valablement signé par son auteur sous
  un nom proche d'un autre (typosquatting) n'est pas détecté. Voir
  [Isolation et signature](contrat.md#résolution-nuget) et `SECURITY.md`.
- Épinglez une version explicite (`--plugin-package-version`) pour une exécution reproductible, et
  lisez ce que fait le code avant de le lancer contre autre chose qu'un bac à sable.

## L'index

La liste ci-dessous est **générée depuis une vraie requête nuget.org**, jamais tenue à la main :

```bash
dotnet run --project tools/Sirocco.ExtensionIndex -- docs/extensions/_index-communaute.md
```

L'outil interroge le service de recherche officiel, puis **revalide l'étiquette sur les métadonnées
de chaque paquet** plutôt que de faire confiance au moteur de recherche, dont la correspondance est
approximative — cet index nomme des paquets tiers, en lister un qui ne revendique pas l'étiquette
serait une erreur de fond.

**Limite assumée : c'est un instantané.** Rien ne le rafraîchit tout seul ; il vaut pour le jour où
il a été régénéré, et il est donc à régénérer avant chaque version. Un rafraîchissement périodique
demanderait un workflow planifié qui committe — machinerie disproportionnée tant que l'étiquette ne
désigne que les extensions de ce dépôt.

[!include[](_index-communaute.md)]

## Les cinq extensions de référence

Elles vivent dans ce dépôt et servent d'exemples travaillés du contrat, chacune validant une facette
différente — voir [Extensions publiées et convention de découverte](contrat.md#extensions-publiées-et-convention-de-découverte)
pour le détail, y compris **laquelle des deux voies de consommation fonctionne pour chacune** (les
paquets SQL et navigateur, à dépendances hors `lib/<tfm>`, ne se chargent pas par
`--plugin-package`).

Pour écrire la vôtre : [Guide d'écriture d'extension](guide.md).

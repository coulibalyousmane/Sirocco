# Versionnement, compatibilité et support

Cette page dit **sur quoi vous pouvez vous appuyer**, et sur quoi non. Elle existe parce qu'un
paquet publié sur nuget.org est définitif : la surface publique de Sirocco compte 121 types, et sans
frontière déclarée, chacun serait un engagement implicite.

## Schéma de version

Sirocco suit [SemVer 2.0.0](https://semver.org/lang/fr/). Une conséquence à lire avant tout le
reste : **tant que le numéro majeur vaut 0, une version mineure peut casser la compatibilité.** La
`0.1.0` est la première version publiée ; épinglez-la (`[0.1.0]`) si vous ne voulez aucune surprise.

Le tag Git `vX.Y.Z` et la version des dix paquets sont la même valeur, vérifiée par la CI avant tout
envoi : `release.yml` refuse de publier si le tag et `Directory.Build.props` divergent.

## Ce qui est un contrat

**Niveau 1 — engagement de compatibilité.** Une rupture ici impose un majeur (ou, en `0.x`, un
mineur clairement annoncé dans le `CHANGELOG`) :

| Surface | Pourquoi elle est au niveau 1 |
|---|---|
| L'API publique de **`Sirocco.Domain`** | c'est la seule référence dont une extension a besoin ([guide d'extension](../extensions/guide.md)) |
| Les **options de la ligne de commande** et les **codes de sortie** de `sirocco` | c'est ce dont dépend une CI, bien plus que n'importe quel type |
| Le **schéma des fichiers de scénario déclaratifs** (YAML) | un scénario écrit une fois doit continuer à tourner |
| Le **JSON de `/report`** | additif seulement : des champs peuvent apparaître, aucun ne disparaît ni ne change de type |

**Niveau 2 — publié, mais sans garantie de stabilité avant la `1.0`.** Ces paquets existent pour
qui veut composer son propre hôte ou partir d'un exemple. Ils peuvent changer dans une version
mineure :

- `Sirocco.Application`, `Sirocco.Infrastructure`, `Sirocco.Scenarios` ;
- les cinq extensions de référence (`Sirocco.Extensions.Sql`, `.Sse`, `.Mqtt`, `.GraphQl`,
  `.Browser`) — ce sont des exemples consommables, pas un socle.

**Hors contrat, à tout moment** : tout ce qui est `internal`. Le dépôt utilise
`InternalsVisibleTo` pour ses propres tests ; cela n'accorde rien à un tiers et ne rend rien
public.

## Ce qui n'est pas une rupture

Un point propre à un outil de mesure, et qui mérite d'être dit explicitement : **corriger un défaut
de mesure n'est pas une rupture, même si vos chiffres changent.** Si une version corrige un biais
dans la dette d'ordonnancement ou dans un percentile, la valeur rapportée pour la même cible peut
bouger — c'est la correction d'un résultat faux, pas un changement de contrat. Ces corrections sont
signalées dans le `CHANGELOG` avec leur effet attendu sur les chiffres.

Ne sont pas non plus des ruptures : la mise en page du rapport HTML et du tableau texte, les
messages de journal, et l'ordre des lignes d'un rapport.

## Dépréciation

Rien n'est retiré sans préavis :

- un membre public part avec `[Obsolete]` pendant **au moins une version mineure** avant sa
  suppression ;
- une option de ligne de commande continue de fonctionner, avec un avertissement, pendant **au
  moins une version mineure** après son remplacement.

## Support et compatibilité

**Sirocco cible `net10.0`, et rien d'autre.** C'est un choix, pas un oubli : .NET 10 est une version
LTS, et le moteur s'appuie sur des constructions récentes du langage et du runtime. La conséquence
est nette et assumée — **un projet sur `net8.0` ne peut pas référencer `Sirocco.Domain`**, donc ne
peut pas écrire d'extension.

Deux engagements qui en découlent :

- la cible ne sera **pas relevée** au sein d'un même majeur : passer à `net11.0` casserait les
  consommateurs restés sur `net10.0`, ce que SemVer interdit sans majeur ;
- le multi-ciblage n'est pas prévu tant que le besoin n'est pas exprimé. Si vous en avez besoin,
  ouvrez une issue en décrivant votre contrainte : c'est le genre de demande qui fait bouger cette
  page.

Les binaires autonomes publiés à chaque tag (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`)
n'exigent aucun SDK ni runtime .NET sur la machine qui les exécute — voir
[Installation](../demarrer/installation.md).

## Ce que rien ne fait respecter, à ce jour

À énoncer plutôt qu'à laisser croire : **cette politique n'est pas outillée.** Aucun analyseur d'API
publique, aucune baseline `PublicAPI.Shipped.txt` ne fait échouer la compilation quand la surface de
`Sirocco.Domain` change. Une rupture involontaire reste donc possible, et seul le `CHANGELOG` la
rattrapera — après coup.

C'est une limite connue, pas un oubli : la poser demande de figer une baseline des 52 types publics
de `Sirocco.Domain`, ce qui est un chantier à part. Voir `M2` dans
[AUDIT-MATURITE.md](https://github.com/coulibalyousmane/Sirocco/blob/main/AUDIT-MATURITE.md).

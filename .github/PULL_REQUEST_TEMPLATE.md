<!--
Merci pour la PR. Ce gabarit reprend les exigences de CONTRIBUTING.md : il est court parce qu'il ne
demande que ce que la CI ne peut pas deviner.
-->

## Ce que change cette PR, et pourquoi

<!-- Le pourquoi surtout : la CI dira si ça compile, elle ne dira pas si c'était la bonne idée. -->

## Comment c'est vérifié

<!--
Le point où Sirocco est plus exigeant que la moyenne. Si le changement touche un comportement
observable, donnez le vrai tir : la commande, et les chiffres qu'elle a produits. Un test qui simule
le protocole plutôt que de parler à un vrai serveur ne compte pas comme vérification.

Si une vérification est impossible dans votre environnement (pas de cluster, pas de machine macOS,
pas de flux NuGet), écrivez-le. Une limite énoncée vaut mieux qu'une preuve supposée.
-->

## Barrière

- [ ] `dotnet build Sirocco.sln --configuration Release` — 0 avertissement, 0 erreur
- [ ] `dotnet test tests/Sirocco.UnitTests --configuration Release`
- [ ] `dotnet format Sirocco.sln --verify-no-changes --severity info`
- [ ] `docfx build docs/docfx.json --warningsAsErrors` — si la documentation est touchée
- [ ] `CHANGELOG.md` mis à jour — si le comportement observable change ; et l'effet attendu sur les
      chiffres est écrit, s'il s'agit d'un biais de mesure corrigé
- [ ] Un exemple de documentation ajouté est un **vrai fichier** de `docs/examples/`, transclus et
      exécuté par la CI — pas un bloc de code recopié dans une page

## Compatibilité

<!--
À remplir uniquement si l'API publique de Sirocco.Domain, les options de la CLI, les codes de sortie,
le schéma déclaratif ou le JSON de /report changent : ce sont les surfaces de niveau 1 de
docs/projet/versionnement.md, et une rupture y coûte un majeur.
-->

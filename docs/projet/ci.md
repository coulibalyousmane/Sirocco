# Pipeline CI

Tout l'outillage orienté CI (seuils, `ExitAfterRun`, `Sirocco.Compare`) restait, jusqu'ici,
jamais exercé automatiquement. `.github/workflows/ci.yml` ferme cette boucle sur chaque push et
pull request vers `main`, en deux temps :

- **`build-and-test`** — restauration, compilation en `Release`, suite de tests complète,
  `dotnet format --verify-no-changes`.
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


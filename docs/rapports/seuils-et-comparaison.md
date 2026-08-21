# Seuils et comparaison

## Seuils CI/CD

Un `ThresholdRule` transforme un rapport en verdict binaire : grandeur, comparaison, limite.
Volontairement absent de la liste des grandeurs disponibles : toute variante `Service*` (le
temps brut, non corrigé). Gater un pipeline sur le temps de service reviendrait à faire
confiance à la mesure même que Tempest existe pour corriger — un seuil ne peut porter que sur
la latence de réponse déjà corrigée, le taux d'erreur, la dette d'ordonnancement ou le nombre
de mesures.

[!code-json[](../examples/config/appsettings.seuils-ci.json)]
*Fichier exécuté par la CI : `docs/examples/config/appsettings.seuils-ci.json`*

`ExitAfterRun` est **faux par défaut** : sans lui, l'hôte reste actif pour continuer à servir
`/metrics` comme avant, même si des seuils sont configurés. C'est le scénario CI (un script qui
attend un code de sortie) qui doit l'activer explicitement — jamais un changement de
comportement silencieux pour du code déjà en production. Une étape introuvable (nom mal
orthographié) est un **échec**, pas un succès par défaut : une règle mal configurée doit se voir
dans le verdict, pas disparaître dans un pipeline qui continue de passer au vert.

Vérifié par un vrai tir : seuil trop strict → code de sortie 1, seuil desserré → code de sortie
0, `ExitAfterRun` absent → l'hôte reste actif comme à l'étape précédente. `/thresholds` expose
le même verdict en JSON, à tout moment du tir.

L'exemple ci-dessus, rejoué tel quel, rend le verdict explicite en fin de tir :

```text
Seuils : tous respectes.
  [OK] __iteration: ResponseP95Milliseconds < 1000 (observe : 84,99)
  [OK] __iteration: ErrorRate <= 0,01 (observe : 0,00)
```

Il se lance comme les autres fragments de configuration — voir
[Scénarios concurrents](../charge/scenarios-concurrents.md#scénarios-concurrents) pour la commande
et le piège du chemin absolu :

```bash
dotnet src/Tempest.Host/bin/Release/net10.0/Tempest.Host.dll \
  --contentRoot "$(pwd)/docs/examples/config" --environment seuils-ci
```


## Comparaison entre tirs

Un `ThresholdRule` gate sur une limite **absolue**, à redéfinir manuellement à chaque évolution
légitime de la cible. `tools/Tempest.Compare` répond à une question différente — "a-t-on
régressé *depuis le dernier tir de référence*, indépendamment de la limite absolue ?" — à
partir de deux rapports `/report` exportés en JSON, sans qu'aucun autre seuil n'ait besoin
d'être redéfini :

```bash
dotnet run --project tools/Tempest.Compare -- reference.json actuel.json \
  --html comparaison.html --max-regression-percent 20
```

Trois usages du même calcul (`LoadTestReportComparison.Compare`), pas trois outils : une table
console (usage manuel ou log CI), `--html` pour un rapport comparatif ouvrable dans un
navigateur (régressions en rouge, améliorations en vert), `--max-regression-percent` pour un
code de sortie 1 si une étape régresse au-delà de ce pourcentage de p95 par rapport à la
référence. Les étapes sont appariées par nom : une étape apparue ou disparue entre les deux
tirs est signalée comme telle, jamais ignorée en silence.

`Tempest.Compare` ne déserialise pas directement vers `LoadTestReport` : comme
`ScenarioDefinitionDto` côté scénarios déclaratifs, `System.Text.Json` ne sait pas construire un
`IReadOnlyList<T>` par réflexion — un DTO à types concrets fait la frontière avant de mapper
vers le type Domain réel.

Vérifié par deux vrais tirs contre des cibles de latence différente (5–15 ms puis 40–80 ms) :
la comparaison détecte correctement une régression de p95 de +205,6 % sur `__iteration`,
`--max-regression-percent 10` échoue (code de sortie 1), `--max-regression-percent 1000`
passe (code de sortie 0) — et le rapport HTML colore chaque étape régressée en rouge.


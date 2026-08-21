# Scénarios concurrents

Tous les modèles ci-dessus pilotent un seul scénario à la fois. Un tir réaliste veut souvent en
faire tourner plusieurs *en même temps* dans le même processus — « la navigation à 20 RPS pendant
que le paiement monte en charge » — chacun avec son propre profil, ses propres étiquettes et ses
propres seuils, sans que les mesures de l'un se mélangent à celles de l'autre.

Reste, comme un profil de charge à plusieurs paliers (`Tempest:RampVus`), l'affaire d'un
`appsettings.json` du répertoire courant plutôt que d'une syntaxe `--scenario` à inventer sur la
ligne de commande — un tableau de scénarios n'a pas d'équivalent plat raisonnable :

[!code-json[](../examples/config/appsettings.scenarios-concurrents.json)]
*Fichier exécuté par la CI : `docs/examples/config/appsettings.scenarios-concurrents.json`*

Les deux scénarios qu'il référence portent volontairement tous les deux une étape nommée
`browse`, pour rendre l'isolement observable :

[!code-yaml[](../examples/scenarios/checkout.yaml)]
*`docs/examples/scenarios/checkout.yaml`*

[!code-yaml[](../examples/scenarios/browse.yaml)]
*`docs/examples/scenarios/browse.yaml`*

`Tempest.Host` n'a pas d'option pour désigner un fichier de configuration : il lit
`appsettings.json` et `appsettings.<environnement>.json` de sa **racine de contenu**. Pour jouer
l'exemple ci-dessus sans copier de fichier, viser le dossier qui le contient et nommer
l'environnement d'après son suffixe :

```bash
dotnet src/Tempest.Host/bin/Release/net10.0/Tempest.Host.dll \
  --contentRoot "$(pwd)/docs/examples/config" --environment scenarios-concurrents
```

Le chemin passé à `--contentRoot` doit être **absolu** : un chemin relatif est résolu depuis le
dossier du binaire, pas depuis le répertoire courant — piège rencontré pour de bon en écrivant
cette page. Le répertoire courant, lui, reste la racine du dépôt, ce qui permet aux `ScenarioFile`
de l'exemple d'être écrits relativement à elle.

Chaque entrée de `Tempest:Scenarios` accepte le même vocabulaire que `TempestHostOptions` lui-même
(`Profile`, `ClosedModelDuration`, `RampVus`, `SharedIterations`, `IterationsPerVirtualUser`,
`MaxRequestsPerSecond`, `ScenarioFile`/`Workflow`, `Thresholds`) — chaque scénario choisit son
modèle de charge indépendamment des autres. `TargetBaseUrl` reste optionnel par scénario : omis, il
retombe sur celui du tir entier, ce qui couvre le cas courant où tous les scénarios visent la même
cible — `MaxRequestsPerSecond` suit la même convention de repli (voir [Bridage](modeles.md#bridage)).

Techniquement, `MultiScenarioRunner` (`Tempest.Host.Execution`) construit à la main, pour chaque
scénario, sa propre chaîne complète — `IWorkflow`, `ILoadScheduler`, `HttpClient`,
`StepRegistry`/`MetricsAggregator` — plutôt que de passer par le conteneur d'injection de
dépendances, qui ne sait enregistrer qu'un singleton de chaque type. C'est cet isolement complet,
pas un simple préfixe de nom, qui garantit que deux scénarios déclarant tous les deux une étape
`browse` produisent deux lignes indépendantes dans le rapport combiné (`MultiScenarioReport`),
jamais une seule fusionnée. Les scénarios tournent en parallèle (`Task.WhenAll`) : un scénario plus
long n'est jamais tronqué par la fin anticipée d'un autre.

Limites de cette première version : mode distribué non pris en charge, `/report/live` et
`/metrics` (Prometheus) non alimentés — seuls `/report`, `/report.html` et `/thresholds` le sont,
une fois le tir entièrement terminé. Vérifié par un vrai tir à deux scénarios (l'un en modèle
ouvert, l'autre en modèle fermé, tous deux avec une étape `browse` de même nom) contre
`Tempest.SampleTarget` : deux entrées indépendantes dans le rapport (100 puis 943 itérations,
jamais 1043 fusionnées), étiquettes et seuils propres à chacune, avertissement modèle fermé présent
sur la seule entrée concernée, code de sortie reflétant le verdict combiné.


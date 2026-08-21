# Données, assertions et rythme

## Jeux de données

Premier bullet de la [roadmap phase 2](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
sans jeu de données, tous les utilisateurs virtuels envoient les mêmes identifiants — le pool
généré par Bogus dans `DynamicCheckoutWorkflow` ne couvrait ce besoin que pour ce seul scénario,
codé en dur. Un scénario déclaratif peut désormais charger un fichier CSV ou JSON et piocher une
ligne à chaque itération, exposée à n'importe quelle étape via `{{jeu.colonne}}` — même mécanisme
de substitution que les variables extraites, juste préfixé par le nom du jeu :

[!code-yaml[](../examples/scenarios/jeux-de-donnees.yaml)]
*Fichier exécuté par la CI : `docs/examples/scenarios/jeux-de-donnees.yaml`*

Les deux sources qu'il consomme — un CSV et un JSON :

[!code-csv[](../../scenarios/users.csv)]
*`scenarios/users.csv`*

[!code-json[](../examples/data/paniers.json)]
*`docs/examples/data/paniers.json`*

Un JSON de jeu de données est un **tableau d'objets plats**. Une valeur numérique y est reprise
telle quelle : `{{panier.quantity}}` donne `3`, pas `"3"` — donc utilisable sans guillemets dans un
corps JSON, comme ci-dessus.

Trois stratégies de choix d'une ligne, portées par `DataSet` (`Tempest.Domain.Data`) :

- **`circular`** (défaut) — parcourt les lignes dans l'ordre, en boucle, un curseur **partagé**
  par tous les utilisateurs virtuels (`Interlocked.Increment`, sans verrou).
- **`random`** — une ligne tirée uniformément au hasard à chaque lecture.
- **`uniquePerVirtualUser`** — une ligne fixe par utilisateur virtuel
  (`VirtualUserId % nombre de lignes`), la même à chaque itération de cet utilisateur —
  exactement le principe déjà à l'œuvre dans `DynamicCheckoutWorkflow.ExecuteAsync`, généralisé
  à n'importe quelle source de données.

Une ligne est choisie **une fois par itération**, pas une fois par étape : toutes les étapes
d'une même itération voient la même ligne, comme les variables extraites. Le fichier est chargé
une seule fois dans `IWorkflow.SetUpAsync`, jamais sur le chemin critique — un jeu de données
volumineux ne coûte rien pendant le tir.

Vérifié par de vrais tirs contre `Tempest.SampleTarget` : un scénario déclaratif dont `checkout`
substitue `productId`/`quantity` depuis un CSV avec `uniquePerVirtualUser` passe à 0 % d'échec
sur 3 utilisateurs virtuels, chacun recevant sa propre ligne à chaque itération (confirmé par
instrumentation temporaire) ; `scenarios/scripted-checkout.csx` (voir plus bas) recevant de même
un identifiant distinct par utilisateur virtuel depuis `scenarios/users.csv`.

## Checks

Deuxième bullet de la [roadmap phase 2](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
une assertion logique sur la réponse d'une étape, qui enregistre un échec **sans jamais faire
échouer la requête HTTP** dont elle dérive — `checkout` reste un 200 même si un check sur son
corps échoue. Chaque check devient sa **propre étape** dans le rapport (même table, mêmes
`/metrics`, même seuil possible via `--threshold`), avec son propre compte de succès/échec :

[!code-yaml[](../examples/scenarios/checks.yaml)]
*Fichier exécuté par la CI : `docs/examples/scenarios/checks.yaml`*

Même vocabulaire d'expression que la [corrélation dynamique](declaratif.md#corrélation-dynamique-regexxpathjsonpath)
— `regex`, `xpath` ou `jsonPath`, exactement une des trois — plutôt qu'un second langage
d'assertion : un check est une extraction dont on ne garde que le résultat booléen. Sans
`expected`, le check réussit dès que l'expression trouve quelque chose ; avec, il ne réussit que
si la valeur trouvée lui est identique (comparaison de texte exacte).

Le nom d'un check partage l'espace de noms des noms d'étape — les deux deviennent chacun leur
propre ligne du même rapport — un check ne peut donc pas porter le nom d'une étape existante, ni
d'un autre check, dans tout le scénario ; une collision est rejetée au chargement.

Un check qui échoue compte comme n'importe quelle étape pour l'issue de l'itération dans son
ensemble (`__iteration`) — seule la requête HTTP dont il dérive reste inchangée. C'est cohérent
avec l'extraction manquée (étape 9) : un problème logique reste visible dans le signal global,
sans être imputé à tort au transport.

Vérifié par de vrais tirs contre `Tempest.SampleTarget`. D'abord le cas qui échoue, celui qui
importe : `login` avec un check qui trouve toujours son jeton (`has-token`, 0 % d'échec) et un
second qui ne trouve jamais le champ qu'il cherche (`status-ok`, absent de la réponse réelle,
100 % d'échec) — `login` lui-même reste à 0 % d'échec sur les 15 itérations, confirmant qu'un
check qui échoue ne rejaillit jamais sur la requête HTTP dont il dérive. Puis l'exemple
ci-dessus, rejoué tel quel : 50 itérations, et les trois checks apparaissent bien comme leurs
propres lignes du rapport (`jeton-present`, `catalogue-commence-a-1`, `commande-confirmee`), à
côté des trois étapes HTTP.

`catalogue-commence-a-1` illustre le seul piège d'un `expected` : il faut viser une valeur
réellement déterministe. Les identifiants du catalogue de démonstration le sont (1..n) ; ses noms
et ses prix, tirés au hasard par Bogus, ne le seraient pas.

Scénario **scripté** : rien de nouveau n'est nécessaire — un script a déjà accès à
`System.Text.RegularExpressions`/`System.Xml`/`System.Text.Json` et peut publier n'importe quelle
assertion comme sa propre étape (`registry.Register(...)` puis `context.BeginStep(...)` /
`.Complete(...)`), exactement le mécanisme que `CheckRule` automatise pour le format déclaratif.

## Groupes et étiquettes

Troisième bullet de la [roadmap phase 2](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
une hiérarchie d'étapes (`endpoint`) et des métadonnées de tir (`région`, `version`) dans le
rapport. Les deux couvrent des besoins distincts et n'ont volontairement pas la même portée.

**Groupe** — une étape peut porter un `group`, préfixé à son nom pour former le nom
effectivement enregistré (`QualifiedName`) :

[!code-yaml[](../examples/scenarios/groupes-etiquettes.yaml)]
*Fichier exécuté par la CI : `docs/examples/scenarios/groupes-etiquettes.yaml` — il porte les
deux dimensions à la fois, groupes et étiquettes.*

Ce scénario produit deux lignes `checkout/login` et `checkout/pay` dans le rapport — la même
`StepId`/`StepScope` que n'importe quelle étape, donc les mêmes `/metrics` Prometheus et les
mêmes seuils via `--threshold`, sans aucun changement dans `Tempest.Application`/
`Tempest.Infrastructure`. Deux étapes de même nom dans deux groupes différents (`checkout/pay`,
`refund/pay`) restent deux lignes distinctes : la collision est vérifiée sur le nom qualifié, pas
sur le nom seul.

Le rapport affiche ce nom qualifié tel quel, sans tenter d'en déduire une arborescence visuelle
(indentation, sous-total par groupe) : un nom d'étape reste une chaîne libre, et interpréter un
`/` comme séparateur de groupe à l'affichage romprait pour toute étape dont le nom en contient un
sans intention de groupe — un cas réel, pas hypothétique, rencontré pendant le développement de
cette fonctionnalité (le test d'échappement HTML utilise justement un nom malicieux contenant
`</script>`, lui-même porteur d'un `/`). Le regroupement reste donc une convention de nommage
visible dans la colonne existante, pas une syntaxe interprétée.

**Étiquettes** — une métadonnée de tir dans son ensemble, pas d'une étape précise, portée par
`ScenarioDefinition.Tags` : c'est le bloc `tags:` de l'exemple ci-dessus (`region`, `version`),
déclaré à la racine du scénario et non dans une étape.

Reportées telles quelles dans l'en-tête du rapport (texte et HTML), jamais dans l'agrégation des
métriques : une étiquette classe un rapport, elle ne découpe pas ses lignes. C'est une différence
assumée avec un système de tags par requête façon k6, qui exigerait de faire de la valeur d'une
étiquette une clé d'agrégation à part entière — un changement bien plus profond (jusque dans
`StepAccumulator`/`MetricResult`, aujourd'hui une structure non managée volontairement sans
référence, voir son commentaire) pour un besoin réel mais différent : classer deux tirs du même
scénario contre deux cibles, pas ventiler un seul tir par région choisie par requête.

Scénario **scripté** : `IWorkflow.Tags` est une propriété d'interface à défaut vide — un script
qui en a besoin la surcharge directement dans sa classe, sans mécanisme supplémentaire.

Limite assumée : en mode distribué (maître/workers), les étiquettes ne sont pas encore
propagées jusqu'au rapport fusionné — seul le mode autonome (`tempest run` sans rôle) les affiche
aujourd'hui. À traiter si le besoin se présente.

Vérifié par un vrai tir contre `Tempest.SampleTarget` : un scénario avec `login`/`pay` groupés
sous `checkout` et une étape `browse` sans groupe affiche `checkout/login`, `checkout/pay` et
`browse` dans la même table, et l'en-tête du rapport (texte et HTML) affiche
`etiquettes : region=eu-west, version=v2`.

## Métriques personnalisées

Dernier bullet réellement nouveau de la [roadmap phase 2](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
un compteur, une jauge, un taux ou une tendance métier, alimentés depuis une réponse de scénario
et agrégés comme les métriques natives — même vocabulaire que les `Counter`/`Gauge`/`Rate`/`Trend`
de k6. Contrairement aux checks et aux groupes/étiquettes, cette fonctionnalité ne pouvait pas se
contenter de réutiliser `StepId`/`StepAccumulator` tels quels : une métrique personnalisée porte
une valeur métier arbitraire (un montant, une taille de panier), pas une durée de requête, donc
une seconde chaîne d'agrégation parallèle était réellement nécessaire — canal borné dédié
(`ChannelCustomMetricSink`), accumulateur (`CustomMetricAccumulator`) et agrégateur
(`CustomMetricsAggregator`), sur le même principe qu'un seul consommateur en arrière-plan que la
chaîne native.

[!code-yaml[](../examples/scenarios/metriques-personnalisees.yaml)]
*Fichier exécuté par la CI : `docs/examples/scenarios/metriques-personnalisees.yaml`*

Même vocabulaire d'expression que la [corrélation dynamique](declaratif.md#corrélation-dynamique-regexxpathjsonpath)
et les [checks](#checks) — `regex`, `xpath` ou `jsonPath` — avec une exception : un `counter` sans
expression compte simplement les passages sur l'étape (valeur implicite 1 à chaque exécution), le
cas le plus courant ne devrait pas exiger d'extraire quoi que ce soit d'une réponse. `gauge` et
`trend` exigent une expression numérique. `rate` évalue une condition comme un check (trouvé, ou
identique à `expected` si fourni) et enregistre 1 ou 0. Une expression manquée ou non numérique
n'enregistre simplement rien cette fois-ci — ce n'est jamais un échec de la requête HTTP dont la
métrique dérive, exactement comme un check.

Le nom d'une métrique vit dans son propre espace, distinct des étapes et des checks : une même
métrique peut légitimement apparaître dans plusieurs étapes (un compteur métier alimenté à deux
endroits différents), à condition de garder le même type partout où elle apparaît — un type
incohérent est rejeté au chargement.

Rendue dans le rapport (texte et HTML) sous une section dédiée, et dans Prometheus sous quatre
instruments (`tempest.custom.counter`, `.gauge`, `.rate`, `.trend`), étiquetés par `metric` (et
par `stat` pour la tendance : `min`/`mean`/`max`). Limites assumées pour ce premier tour, dans le
même esprit que les précédentes : pas de centiles pour la tendance (`LatencyHistogram` est bâti
pour une durée non négative bornée, pas pour une valeur métier de plage arbitraire — voir son
commentaire), pas de fenêtre glissante (une seule photographie cumulée), et pas de fusion
inter-workers en mode distribué.

Scénario **scripté** : aucun changement nécessaire — `CustomMetricRegistry`/`CustomMetricId` sont
déjà dans les imports par défaut (`Tempest.Domain.Metrics`), et `IWorkflow.RegisterMetrics` a un
défaut vide qu'un script surcharge s'il en a besoin, exactement comme il enregistre déjà ses
propres étapes.

Vérifié par de vrais tirs contre `Tempest.SampleTarget` — confirmés à la fois dans le rapport
texte et dans `/metrics`. L'exemple ci-dessus, rejoué tel quel sur 50 itérations, donne les
quatre types côte à côte :

```text
prix-premier-produit     gauge          50   72677,00
commandes                counter        50   50,00
montant-commande         trend          50   min 8262,00 / moy 57596,00 / max 88947,00
commandes-confirmees     rate           50   100,0 %
```

Le compteur vaut exactement le nombre d'itérations, le taux 100 %, et la tendance couvre une vraie
plage de valeurs — c'est pour ça que l'exemple fait varier son panier par un jeu de données : avec
un panier fixe, min, moyenne et max coïncideraient et n'illustreraient aucune distribution.

## Temps de réflexion et rythme

Dernier bullet de la [roadmap phase 2](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
une pause après une étape, avant la suivante — le `sleep()` de k6 ou le `pause()` de Gatling, sans
lequel un parcours utilisateur simulé enchaîne ses requêtes plus vite qu'aucun humain ne le ferait
jamais.

[!code-yaml[](../examples/scenarios/temps-de-reflexion.yaml)]
*Fichier exécuté par la CI : `docs/examples/scenarios/temps-de-reflexion.yaml`*

`thinkTime` seul fixe une durée exacte ; ajouter `thinkTimeMax` en fait une plage, tirée uniformément
à chaque itération (`ThinkTimeDefinition.Sample`) — un parcours réel ne s'arrête jamais identique
deux fois de suite. Les deux acceptent le même format que `--duration` en ligne de commande
(`500ms`, `1s`, `2m`, `1h`, ou un nombre nu interprété en secondes).

La pause n'est **jamais** mesurée comme latence de requête : elle a lieu après que l'étape a
publié sa propre mesure (`scope.Complete()`), donc en dehors de tout ce que `LoadTestReport`
rapporte pour cette étape. Aucun changement dans le moteur n'était nécessaire — un utilisateur
virtuel qui dort dans `Task.Delay` ne fait que retarder le prochain jeton qu'il prendra dans le
canal, exactement comme le ferait une réponse HTTP lente : le modèle ouvert de
`TargetRpsLoadEngine` absorbe cela nativement en dette d'ordonnancement si le débit cible dépasse
ce que les utilisateurs virtuels configurés peuvent tenir compte tenu de leurs pauses, sans jamais
ralentir le rythme d'émission des jetons eux-mêmes.

Scénario **scripté** : sans effet et sans besoin d'API dédiée — une pause s'écrit directement via
`await Task.Delay(...)` dans le script, ce que Roslyn permettait déjà avant ce chantier.

Vérifié par de vrais tirs contre `Tempest.SampleTarget` : avec un seul utilisateur virtuel, une
pause fixe de 500 ms et un débit cible de 20 req/s (irréaliste pour un seul utilisateur virtuel
avec cette pause), le débit effectif tombe à ~2 itérations/s — exactement `1 / (pause + latence)`
— et la dette d'ordonnancement grimpe en conséquence, pendant que la latence brute de l'étape HTTP
elle-même reste inchangée (~100 ms de p99), confirmant que la pause n'est jamais comptée dans la
mesure de la requête. Le même scénario sans `thinkTime` tient les 20 req/s cible avec une dette
négligeable. Une pause en plage (100–300 ms, 4 utilisateurs virtuels) montre un p50/p95
d'itération cohérent avec la plage configurée, sans affecter la latence brute rapportée pour
l'étape HTTP.

L'exemple ci-dessus le montre d'un coup d'œil, rejoué tel quel : l'itération complète est à
549 ms de p50 (les deux pauses, ~300 à 600 ms, s'y ajoutent), alors que chacune des trois étapes
HTTP reste à ~31 ms. La pause est bien hors de la portée de mesure de l'étape.

Avec ce chantier, le contenu de la [roadmap phase 2](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire)
est entièrement traité.


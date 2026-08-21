# Historique du projet

## Ce que le premier tir réel a révélé

Trois défauts qu'aucun test unitaire — y compris les 128 déjà écrits à l'étape 3 — n'avait
pu voir, parce qu'aucun n'exerçait à la fois un vrai réseau et le câblage DI complet :

1. **JSON camelCase vs PascalCase.** ASP.NET Core sérialise en camelCase par défaut ; un
   `JsonSerializerContext` sans `[JsonSourceGenerationOptions]` explicite attend les noms de
   propriété exactement tels que déclarés (PascalCase). Sans accord explicite entre les deux
   contextes (client et serveur), un jeton de connexion serait arrivé au client sous un nom qui
   ne correspond à rien — `Token` resterait `null`, échec silencieux, sans exception. Corrigé en
   déclarant `PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase` explicitement des deux côtés.
2. **`MetricsAggregator` exigeait un `StepRegistry` déjà rempli à la construction.** Or le
   registre n'est peuplé qu'au démarrage du tir (`TargetRpsLoadEngine.RunAsync`), pas à la
   construction du moteur — et un conteneur DI ne garantit aucun ordre entre deux singletons
   indépendants. Corrigé en construisant les accumulateurs **paresseusement**, au premier
   enregistrement ou à la première lecture, plutôt qu'au constructeur.
3. **`TempestMeter` n'était résolu par personne.** Le `Meter` et ses instruments ne se créent
   qu'à la construction de `TempestMeter` — mais rien d'autre n'en dépend directement (il existe
   pour être observé de l'extérieur, pas pour être appelé). Résultat : Prometheus n'exposait que
   `target_info`, aucune métrique Tempest. Corrigé par un `IHostedService` dédié
   (`MeterActivationHostedService`) dont l'unique rôle est de forcer la résolution.

Les trois sont désormais couverts par des tests de régression qui reproduisent l'ordre exact
qui a échoué, sans dépendre de réseau.

Résultat du premier tir (100 utilisateurs virtuels, rampe 0→100→0 RPS sur 35 s, contre la
cible de démonstration) : 2 750 itérations, **0 échec, 0 abandon**, 100 connexions pour 100
utilisateurs virtuels (le jeton mis en cache fonctionne), dette d'ordonnancement maximale
14 ms. La queue de latence (P99 à 811 ms sur `__iteration`, contre P50 à 55 ms) vient du
démarrage à froid — JIT, première connexion TCP par utilisateur virtuel — pas d'une
saturation réelle ; c'est exactement le genre de détail qu'une moyenne aurait masqué.


## Roadmap initiale — close

Les trois priorités identifiées au départ sont faites, chacune dans un scope volontairement
minimal documenté à sa section :

| Priorité | Fonctionnalité |
|---|---|
| ~~P1~~ | ~~Protocoles avancés~~ : WebSockets et les quatre modes gRPC (unaire, streaming serveur/client/bidirectionnel) faits |
| ~~P2~~ | ~~Mode distribué Master/Workers~~ fait (étape 10), tableau de bord combiné en temps réel fait (étape 12) |
| ~~P3~~ | ~~Corrélation avancée : extraction par Regex / XPath / JsonPath~~ fait (étapes 9, 17) |

Trois chantiers de suivi, identifiés une fois les trois priorités closes, sont également faits :
sécurisation du control plane distribué (étape 15), propagation du scénario et des options aux
workers (étape 14), et rapports/observabilité — Prometheus distribué, rapport HTML, comparaison
entre tirs (étapes 18 à 20).


## Et ensuite

Cette roadmap initiale est close : elle traitait de ce que le moteur devait savoir faire. La
suite est un problème différent — Tempest n'est encore installable par personne, et ses scénarios
restent trop pauvres pour un test de charge réel.

**[ROADMAP.md](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md)** couvre ce qui manque pour exister face à k6, Gatling et NBomber :
matrice concurrentielle honnête, huit phases ordonnées par dépendance, et une correction
importante — l'argument « Tempest corrige le *coordinated omission*, contrairement aux autres »
est **faux** (les trois proposent un modèle ouvert) ; le différenciateur réel est ailleurs.

Ce différenciateur est maintenant démontré, pas seulement affirmé : **[benchmark/](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/README.md)**
fait tourner Tempest, k6, Gatling et NBomber contre la même cible saturée avec le même scénario, et
publie les résultats bruts dans [benchmark/results/RESULTS.md](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/results/RESULTS.md).


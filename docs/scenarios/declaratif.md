# Format déclaratif

## Scénario de référence

`DynamicCheckoutWorkflow` (login → browse → checkout) illustre trois capacités du moteur en
même temps :

- **jeton mis en cache par utilisateur virtuel** — `login` n'est rejoué qu'à la première
  itération, ou après un 401 ;
- **corrélation minimale** — le panier de `checkout` référence les identifiants réellement
  renvoyés par `browse`, jamais un pool pré-généré côté client ;
- **JSON sans réflexion** — sérialisation générée à la compilation
  (`System.Text.Json.Serialization.JsonSerializerContext`) des deux côtés du contrat HTTP,
  politique `camelCase` déclarée explicitement (voir plus bas pourquoi).

`Tempest.SampleTarget` simule une vraie capacité finie (`SemaphoreSlim` bornée, 503 au-delà)
et des jetons qui expirent, pour que le scénario ait vraiment quelque chose à saturer et à
rafraîchir plutôt qu'un simple écho instantané.


## Configuration déclarative

Un scénario HTTP peut se décrire en YAML ou JSON plutôt qu'en C#, sans recompiler :

[!code-yaml[](../../scenarios/smoke-test.yaml)]
*`scenarios/smoke-test.yaml` — le scénario de référence du dépôt, exécuté par la CI*

Deux façons de le jouer. En ligne de commande, le fichier est un argument positionnel :

```bash
tempest run scenarios/smoke-test.yaml --target-url http://localhost:5281 --rps 10 --duration 5s
```

Ou par configuration, pour l'hôte :

```json
"Tempest": { "ScenarioFile": "scenarios/smoke-test.yaml" }
```

`ScenarioFile` absent (par défaut) : l'hôte utilise `DynamicCheckoutWorkflow`, comme avant —
même logique de non-régression que pour `ExitAfterRun`. Renseigné : il construit un
`DeclarativeWorkflow` à partir du fichier, qui reçoit le **même** traitement que n'importe
quel scénario codé en dur — métriques, seuils, `/metrics`, sans aucun cas particulier.

Sans `expectedStatusCodes`, l'heuristique usuelle s'applique (2xx = succès). Avec, la
correspondance est exacte : un 2xx absent de la liste devient un `AssertionFailed`, pas un
succès — le scénario a dit ce qu'il attendait, et un 200 inattendu ne le satisfait pas
silencieusement.

> YamlDotNet et `System.Text.Json` ne savent construire, par réflexion, ni
> `IReadOnlyList<T>` ni `IReadOnlyDictionary<K,V>` — découvert en écrivant les tests, pas en
> le supposant. `ScenarioDefinitionDto` (types concrets, mutables) isole ce compromis à la
> frontière de désérialisation ; `ScenarioDefinition` (Domain) reste un objet-valeur immuable.

### Corrélation dynamique (Regex/XPath/JsonPath)

Une étape peut extraire une valeur de sa réponse et la rendre disponible aux étapes
suivantes via `{{nom}}` — c'est ce qui comblait la limite décrite plus haut dans les
versions précédentes de ce document (un jeton d'authentification ne pouvait pas se propager
d'une étape à l'autre) :

C'est exactement ce que fait `scenarios/smoke-test.yaml` ci-dessus : `login` extrait le jeton par
expression régulière, `checkout` le réinjecte dans son en-tête `Authorization`.

Exactement une expression par règle : `regex` (universelle, sur texte brut), `xpath` (pour un
corps XML) ou `jsonPath` (pour un corps JSON). La même corrélation, écrite en JsonPath :

[!code-yaml[](../examples/scenarios/correlation-jsonpath.yaml)]
*Fichier exécuté par la CI : `docs/examples/scenarios/correlation-jsonpath.yaml`*

`jsonPath` ne couvre volontairement qu'un sous-ensemble pratique — accès par propriété
(`.nom`) et par index (`[n]`), par ex. `$.data.items[0].id` — sans caractères génériques,
filtres, descente récursive (`..`) ni tranches : une extraction Regex suffisait jusqu'ici sur
un corps JSON, ce sous-ensemble couvre le reste des cas usuels sans réimplémenter la
spécification JSONPath entière. Implémenté avec `System.Text.Json.Nodes` (BCL) uniquement —
`Tempest.Domain` n'a aucune dépendance NuGet externe, pas de bibliothèque JSONPath dédiée.

Les trois syntaxes sont validées au chargement du scénario, pas au premier appel : une
expression mal formée échoue immédiatement, avant le premier tir.

Portée volontairement limitée à ce seul vocabulaire — pas de branchement, pas de boucle. Les
variables extraites sont **locales à une itération** : une étape qui référence `{{nom}}` sans
qu'aucune extraction précédente ne l'ait renseigné échoue en `AssertionFailed` *avant même
l'envoi de la requête* — une erreur de configuration du scénario, pas un échec de transport à
mesurer comme tel. De même, une extraction configurée mais manquée transforme un 2xx en
`AssertionFailed` : le scénario attendait une valeur que la réponse n'a pas fournie, ce n'est
pas un succès silencieux.

[`scenarios/smoke-test.yaml`](https://github.com/coulibalyousmane/Tempest/blob/main/scenarios/smoke-test.yaml) démontre le cas réel : `login`
extrait le jeton effectivement émis par `Tempest.SampleTarget`, `checkout` le réutilise dans
son en-tête `Authorization` — vérifié par un vrai tir, `checkout` passe désormais à 0 %
d'échec (il retournait systématiquement 401 dans les versions précédentes de ce fichier,
faute de pouvoir propager un jeton réel).

Vérifié à nouveau après l'ajout de JsonPath, avec ce même scénario ré-écrit pour extraire le
jeton via `$.token` plutôt qu'un motif Regex : 125 itérations, **0 échec** sur
`login`/`browse`/`checkout` — `checkout` reste à 0 % d'échec, confirmant que le jeton extrait
par JsonPath se propage correctement à l'en-tête `Authorization`, exactement comme avec Regex.


# Sirocco.Extensions.GraphQl

Extension [Sirocco](https://github.com/coulibalyousmane/Sirocco) pour **GraphQL** : un point d'entrée
HTTP unique (toujours `POST`, toujours le même chemin) où le succès ou l'échec se lit dans le champ
`errors` du corps JSON — jamais dans le code de statut, qui reste 200 même quand la requête échoue
côté métier.

Aucune dépendance au-delà de `Sirocco.Domain` — `System.Text.Json` et `HttpClient` suffisent.

## Utilisation

Résolue directement par identifiant de paquet, sans rien compiler :

```bash
sirocco run --plugin-package Sirocco.Extensions.GraphQl --target-url http://localhost:5281 --rps 20 --duration 30s
```

Deux étapes réelles par itération : `GraphQL query` (liste le catalogue, vérifie qu'elle n'est jamais
vide) et `GraphQL mutation` (passe une commande).

| Variable | Défaut | Rôle |
|---|---|---|
| `SIROCCO_GRAPHQL_PLUGIN_PATH` | `/graphql` | Chemin relatif du point d'entrée |
| `SIROCCO_GRAPHQL_PLUGIN_PRODUCT_ID_MAX` | `20` | Borne haute de l'identifiant tiré pour la mutation |

## Écrire la vôtre

Cette extension est un exemple travaillé du contrat de plugin : un `IWorkflow` ordinaire, compilé
indépendamment du dépôt. Voir le [guide d'écriture d'extension](https://github.com/coulibalyousmane/Sirocco/blob/main/docs/extensions/guide.md).

Publiée sous la licence Apache 2.0.

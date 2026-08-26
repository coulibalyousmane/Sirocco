# Sirocco.Extensions.Sse

Extension [Sirocco](https://github.com/coulibalyousmane/Sirocco) pour **Server-Sent Events** : lit
une réponse `text/event-stream` au fil de l'eau plutôt qu'en un aller-retour requête/réponse, et
chronomètre la réception des événements attendus.

Aucune dépendance au-delà de `Sirocco.Domain` — `HttpClient` et la lecture de flux viennent du BCL.

## Utilisation

Résolue directement par identifiant de paquet, sans rien compiler :

```bash
sirocco run --plugin-package Sirocco.Extensions.Sse --target-url http://localhost:5281 --rps 20 --duration 30s
```

Le client HTTP partagé pointe déjà vers `--target-url` ; seuls le chemin relatif et le nombre
d'événements attendu se règlent :

| Variable | Défaut | Rôle |
|---|---|---|
| `SIROCCO_SSE_PLUGIN_PATH` | `/api/events/stream` | Chemin relatif du flux |
| `SIROCCO_SSE_PLUGIN_EVENT_COUNT` | `3` | Événements attendus par itération |
| `SIROCCO_SSE_PLUGIN_TIMEOUT_SECONDS` | `10` | Délai au-delà duquel l'itération échoue plutôt que de bloquer |

## Écrire la vôtre

Cette extension est un exemple travaillé du contrat de plugin : un `IWorkflow` ordinaire, compilé
indépendamment du dépôt. Voir le [guide d'écriture d'extension](https://github.com/coulibalyousmane/Sirocco/blob/main/docs/extensions/guide.md).

Publiée sous la licence Apache 2.0.

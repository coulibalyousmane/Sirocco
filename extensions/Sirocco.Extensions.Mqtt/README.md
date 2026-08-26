# Sirocco.Extensions.Mqtt

Extension [Sirocco](https://github.com/coulibalyousmane/Sirocco) pour **MQTT** : un protocole
réellement différent de HTTP, orienté publication/abonnement. Chaque utilisateur virtuel publie sur
un sujet puis attend sa propre réception via un abonnement — le round-trip complet est chronométré.

Une seule dépendance au-delà de `Sirocco.Domain` : le client [MQTTnet](https://www.nuget.org/packages/MQTTnet),
managée, sans composant natif.

## Utilisation

Résolue directement par identifiant de paquet — sa dépendance MQTTnet est restaurée automatiquement
à côté d'elle :

```bash
SIROCCO_MQTT_PLUGIN_HOST=localhost SIROCCO_MQTT_PLUGIN_PORT=1883 sirocco run --plugin-package Sirocco.Extensions.Mqtt --target-url http://localhost:1 --rps 20 --duration 30s
```

`--target-url` reste exigé par la CLI mais n'est d'aucun usage ici : la cible vient de la
configuration ci-dessous, même convention que les workflows `grpc-echo`/`websocket-echo`.

| Variable | Défaut | Rôle |
|---|---|---|
| `SIROCCO_MQTT_PLUGIN_HOST` | `localhost` | Hôte du courtier |
| `SIROCCO_MQTT_PLUGIN_PORT` | `1883` | Port du courtier |
| `SIROCCO_MQTT_PLUGIN_TOPIC_PREFIX` | `sirocco/mqtt-plugin` | Préfixe des sujets utilisés |
| `SIROCCO_MQTT_PLUGIN_TIMEOUT_SECONDS` | `10` | Délai au-delà duquel l'itération échoue plutôt que de bloquer |

## Écrire la vôtre

Cette extension est un exemple travaillé du contrat de plugin : un `IWorkflow` ordinaire, compilé
indépendamment du dépôt. Voir le [guide d'écriture d'extension](https://github.com/coulibalyousmane/Sirocco/blob/main/docs/extensions/guide.md).

Publiée sous la licence Apache 2.0.

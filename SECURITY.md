# Politique de sécurité

## Signaler une vulnérabilité

**N'ouvrez pas d'issue publique pour une faille de sécurité.**

Utilisez [**Report a vulnerability**](https://github.com/coulibalyousmane/Sirocco/security/advisories/new)
dans l'onglet Security du dépôt. Le rapport reste privé jusqu'à publication d'un correctif.

Ce projet est développé par une seule personne, sur son temps libre. Attendez-vous à un accusé de
réception sous **7 jours** et à une première évaluation sous **30 jours**. Ce sont des objectifs
honnêtes, pas un engagement contractuel : mieux vaut le dire que promettre un délai d'entreprise.

Un rapport utile contient la version ou le commit concerné, la configuration employée, et les
étapes pour reproduire. Si vous préférez signaler de façon coordonnée, dites-le — nous conviendrons
d'une date de publication.

## Versions couvertes

Le projet est en `0.x`. Seule la dernière version publiée reçoit des correctifs ; il n'y a pas de
branche de maintenance.

## Ce que Sirocco fait par conception

Sirocco est un **générateur de charge**. Envoyer un grand volume de requêtes à un hôte est sa
fonction, pas un défaut. Cela déplace la frontière entre « comportement attendu » et
« vulnérabilité », et il vaut mieux l'écrire que de la laisser deviner.

**Sont des comportements attendus, non des failles :**

- Saturer une cible que vous lui désignez. N'utilisez Sirocco que contre des systèmes dont vous
  avez l'autorisation de mesurer la tenue en charge.
- Exécuter le code d'un scénario que vous fournissez. Un scénario scripté (`.csx`, compilé par
  Roslyn) ou un plugin (`--plugin-type`, `--plugin-package`) s'exécute avec les privilèges du
  processus, sans bac à sable. **Un scénario ou un plugin est du code : traitez-le comme tel.**
  `--plugin-package` interroge nuget.org par défaut et aucune signature n'est vérifiée — vérifiez
  l'identifiant que vous saisissez, le typosquatting existe sur tous les registres publics.
- Enregistrer ce que le proxy enregistreur voit passer. Le HAR produit contient le trafic capturé.

**Sont en revanche des vulnérabilités, à signaler :**

- Faire agir un composant de Sirocco au-delà de ce que sa configuration autorise.
- Divulguer un secret configuré (secret partagé de cluster, mot de passe de certificat, jeton) dans
  un journal, un rapport, un fichier généré ou une réponse HTTP.
- Contourner l'authentification ou l'épinglage de certificat du control plane distribué quand ils
  sont configurés.
- Une exécution de code non voulue déclenchée par une **donnée** plutôt que par du code fourni
  volontairement : un scénario déclaratif YAML/JSON, un HAR, une spécification OpenAPI, une
  collection Postman, ou une réponse de la cible.

## Deux points de configuration qui méritent votre attention

**Le mode distribué démarre sans authentification si vous ne la configurez pas.** Laissé à `null`,
`Sirocco__ClusterSharedSecret` ouvre `/worker/prepare` et `/worker/start`. Or la requête de
préparation porte l'URL de la cible : un worker joignable depuis un réseau non maîtrisé peut donc
être piloté pour marteler un tiers. **Configurez un secret partagé dès que le control plane n'est
pas confiné à un réseau de confiance**, et n'exposez jamais les ports d'un worker sur Internet.

**Le control plane est en clair par défaut.** `Sirocco__ClusterCertificateThumbprint` active
l'épinglage TLS (empreinte SHA-256), décrit dans
[le mode distribué](docs/distribue/mode-distribue.md). Sans lui, rapports et scénarios circulent
sans chiffrement.

Le mode autonome — `sirocco run` — n'expose aucun endpoint et n'est concerné par aucun des deux.

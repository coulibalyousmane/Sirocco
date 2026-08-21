# Conteneurisation

Une seule image sert les trois rôles (autonome/maître/worker) — c'est `Tempest:Role`, pas
l'image, qui les distingue. `docker-compose.yml` démontre le mode distribué en conteneurs
réels, joints par le DNS interne de Docker (nom de service, pas adresse IP) :

```bash
docker compose up --build --exit-code-from master
```

[!code-yaml[](../../docker-compose.yml)]
*`docker-compose.yml` — c'est aussi le seul endroit du dépôt où la configuration par variables
d'environnement (`Tempest__*`, `Master__*`, `Worker__*`) est écrite en entier*

Quatre services : `sampletarget` (la cible), `master` et deux `worker*`. Le maître s'arrête
seul une fois le tir terminé (`Tempest__ExitAfterRun=true`, code de sortie reflétant le
verdict des seuils — utilisable directement en CI) ; les workers restent actifs comme de
vrais services long-vivants, `docker compose down` pour tout arrêter.

**Un piège réel, pas supposé** : le premier démarrage conteneurisé a plané avec
`UriFormatException: Invalid URI: The hostname could not be parsed.` — `Tempest.SampleTarget`
extrayait son port principal via `new Uri(configuration["urls"])`, mais la convention
`ASPNETCORE_URLS=http://+:5281` (« toutes les interfaces », nécessaire en conteneur) utilise
`+` comme hôte, qui n'est pas une syntaxe d'hôte valide au sens strict de `System.Uri` — cette
même ligne fonctionnait en local uniquement parce que `--urls http://localhost:5281` n'a
jamais cette forme. Corrigé par une extraction manuelle du numéro de port, sans passer par
`Uri`.

Vérifié par un vrai `docker compose up` (4 conteneurs réels, résolution par nom de service
Docker) : même pipeline que la vérification manuelle — enregistrement, préparation, démarrage
synchronisé, tir local, remontée des rapports — fusionné en 224 itérations, **0 échec** sur
toutes les étapes, seuils respectés, code de sortie 0.


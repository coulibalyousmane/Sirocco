# Conteneurisation

Une seule image sert les trois rôles (autonome/maître/worker) — c'est `Sirocco:Role`, pas
l'image, qui les distingue. `docker-compose.yml` démontre le mode distribué en conteneurs
réels, joints par le DNS interne de Docker (nom de service, pas adresse IP) :

```bash
docker compose up --build --exit-code-from master
```

[!code-yaml[](../../docker-compose.yml)]
*`docker-compose.yml` — c'est aussi le seul endroit du dépôt où la configuration par variables
d'environnement (`Sirocco__*`, `Master__*`, `Worker__*`) est écrite en entier*

Quatre services : `sampletarget` (la cible), `master` et deux `worker*`. Le maître s'arrête
seul une fois le tir terminé (`Sirocco__ExitAfterRun=true`, code de sortie reflétant le
verdict des seuils — utilisable directement en CI) ; les workers restent actifs comme de
vrais services long-vivants, `docker compose down` pour tout arrêter.

**Un piège réel, pas supposé** : le premier démarrage conteneurisé a plané avec
`UriFormatException: Invalid URI: The hostname could not be parsed.` — `Sirocco.SampleTarget`
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

## Utilisateur non privilégié

Les images tournent sous un utilisateur sans privilèges, pas sous `root` : `USER $APP_UID`
(UID 1654, l'utilisateur `app` déjà présent dans les images .NET) dans l'étage runtime des trois
`Dockerfile`. L'UID est **numérique** et non le nom `app`, parce que Kubernetes évalue
`runAsNonRoot` avant de résoudre `/etc/passwd` et refuse un conteneur dont l'utilisateur est
désigné par un nom.

Deux conséquences à connaître :

- **`/app` reste possédé par `root`, donc en lecture seule pour le processus.** C'est voulu : rien
  n'écrit dans le répertoire de l'application. En revanche `Sirocco:ReportHtmlPath` et
  `Sirocco:ReportJsonPath` doivent désigner un volume monté ou `/tmp`, plus `/app`.
- **`docker-compose.yml` ne pose aucun `security_opt`** : ici la propriété repose entièrement sur le
  `USER` de l'image, sans le filet que Kubernetes apporte
  ([l'opérateur exige `runAsNonRoot`](kubernetes.md) sur les pods qu'il fabrique).


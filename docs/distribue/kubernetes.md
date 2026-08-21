# Kubernetes

## Opérateur Kubernetes

`docker-compose.yml` déploie le mode distribué à la main — au-delà de quelques dizaines de
workers, ça ne tient plus. L'opérateur Kubernetes introduit une ressource personnalisée
`TestRun` (`tempest.dev/v1alpha1`) : décrire un tir (cible, profil, nombre de workers) suffit,
l'opérateur crée les ressources Kubernetes qui le portent et les détruit une fois le tir
terminé.

**Construit avec [KubeOps](https://github.com/dotnet/dotnet-operator-sdk)** (SDK .NET dédié)
plutôt qu'une boucle watch/reconcile écrite à la main — CRD généré depuis des classes C#
annotées (`V1TestRun`), RBAC déclaratif via `[EntityRbac]`, boucle de réconciliation fournie par
le framework. Le contrôleur (`TestRunController`) reste volontairement fin : il délègue la
construction des objets désirés à `TestRunResources`, une classe statique pure et testable sans
cluster (même esprit que `ClusterCertificatePinning`).

**Les workers sont un `StatefulSet` derrière un service headless, pas un `Deployment`** : le
maître adresse chaque worker individuellement (`/worker/prepare`, `/worker/start`), exactement
comme chaque conteneur `worker1`/`worker2` a un nom DNS stable dans `docker-compose.yml`. Chaque
pod calcule sa propre `Worker__SelfUrl` à partir de son nom (Downward API + expansion native
`$(POD_NAME)` de Kubernetes) — aucun changement de code côté `Tempest.Host`.

**Le maître est un `Job` (`restartPolicy: Never`, `backoffLimit: 0`), pas un `Deployment`** :
`MasterOrchestrationHostedService` positionne déjà `Environment.ExitCode` selon le succès/échec
des seuils quand `Tempest__ExitAfterRun` est actif — la condition `Complete`/`Failed` du Job
reflète honnêtement ce résultat sans qu'il soit besoin de parser le rapport de tir. Une fois le
Job terminé, le contrôleur réduit le `StatefulSet` des workers à 0 réplique (patch, pas
suppression) — c'est le nettoyage automatique promis par la ressource `TestRun` ; le
`StatefulSet` reste inspectable mais ne consomme plus de pods.

**Aucune finalisation personnalisée** : chaque ressource fille porte une `OwnerReference` vers
la `TestRun` — la supprimer déclenche le garbage collection natif de Kubernetes (Job,
StatefulSet, Services), sans code de nettoyage à écrire.

Le secret partagé de cluster est référencé par nom (`clusterSharedSecretRef`, un `Secret`
existant + une clé), jamais recopié en clair dans la ressource `TestRun` :

[!code-yaml[](../../deploy/samples/testrun-demo.yaml)]
*`deploy/samples/testrun-demo.yaml` — prêt pour `kubectl apply -f`*

Essayer localement (Docker Desktop, Kubernetes activé dans ses réglages) :

```bash
docker build -f src/Tempest.Host/Dockerfile -t tempest-host:local .
docker build -f src/Tempest.Operator/Dockerfile -t tempest-operator:local .
kubectl apply -k deploy/operator
kubectl apply -f deploy/samples/testrun-demo.yaml
kubectl get testrun testrun-demo -w
```

Vérifié sur un vrai cluster (Kubernetes de Docker Desktop, pas de simulation) : l'opérateur
déployé (CRD + RBAC + `Deployment`, générés par `dotnet kubeops generate operator`) crée bien,
à l'application de la `TestRun`, le service headless, le `StatefulSet` des workers (2 pods,
`-0`/`-1`, adressables individuellement par leur nom DNS stable), le service et le `Job` du
maître — le maître enregistre les deux workers, les fait tourner, sonde leur rapport en direct
(`/worker/report/raw`, visible dans les logs), puis fusionne un rapport final de 224 itérations,
**0 % d'échec, tous les seuils respectés** ; `TestRun.status.phase` est passé par
`Pending → Running → Succeeded`, et le `StatefulSet` des workers est retombé à **0 réplique**
automatiquement une fois le `Job` `Complete`. Contre-épreuve obtenue en conditions réelles (une
première tentative où la cible n'écoutait pas encore sur le port attendu) : le statut a fini à
`Failed` proprement (`Job` en `BackoffLimitExceeded`, workers réduits à 0 réplique quand même),
sans jamais rester bloqué — la détection d'échec du chantier précédent (seuils non respectés)
se propage correctement jusqu'au statut de la ressource Kubernetes. `kubectl delete -f
deploy/samples/testrun-demo.yaml` sur les deux tirs (échoué et réussi) a bien fait disparaître
`Job`, `StatefulSet` et les deux `Service` via le garbage collection natif — aucune ressource
orpheline.

**Limites assumées, pas résolues ici** : les manifestes générés par l'outil CLI KubeOps
(`dotnet kubeops generate operator`) utilisent un `ClusterRole`/`ClusterRoleBinding` — l'opérateur
surveille les `TestRun` sur tout le cluster plutôt que dans un seul namespace ; restreindre la
portée demanderait de configurer explicitement le champ de surveillance côté runtime, non fait
ici. Pas de scénario personnalisé via `ConfigMap` (seuls les workflows déjà nommés dans
`TempestHostOptions` sont sélectionnables via `spec.workflow`). Pas d'automatisation
TLS/`cert-manager` : `spec` ne câble pas `Tempest__ClusterCertificateThumbprint` — HTTP en clair
à l'intérieur du cluster, même choix assumé que `docker-compose.yml` aujourd'hui. Pas de
publication d'image sur un registre (GHCR) : build et chargement locaux uniquement, comme pour
`docker-compose.yml`. `TestRun.status` ne porte pas le contenu du rapport de tir — accès par
`kubectl port-forward` sur le service du maître, comme aujourd'hui.

## Autoscaling

L'opérateur dimensionne `workerReplicas` une fois, à la création de la `TestRun` — pour un
profil qui varie fortement (une rampe de 10 à 500 req/s, par exemple), ça oblige soit à
sur-provisionner pour le pic dès le départ, soit à sous-dimensionner et à laisser la dette
d'ordonnancement grimper. `spec.autoscaling` calcule le nombre de workers requis **palier par
palier** à partir du débit cible du profil, déjà connu en entier à l'avance — pas d'un HPA/KEDA
réactif à des métriques observées en direct : le plan est **prévisionnel**, produit une seule
fois à partir de `spec.profile`, jamais mesuré.

**Renseigné, `spec.autoscaling` prime sur `workerReplicas` (ignoré).** Trois champs :
`maxRequestsPerSecondPerWorker` (capacité déclarée d'un seul worker — une hypothèse de
l'opérateur du cluster, jamais une mesure), `minWorkerReplicas`/`maxWorkerReplicas` (plancher et
garde-fou), `scaleAheadSeconds` (avance avec laquelle le `StatefulSet` est ajusté avant un palier
plus exigeant, pour laisser le temps au pod de démarrer et de s'auto-enregistrer — best-effort,
sans garantie si le pod met plus longtemps que prévu).

[!code-yaml[](../../deploy/samples/testrun-autoscaling-demo.yaml)]
*`deploy/samples/testrun-autoscaling-demo.yaml` — le nombre de workers attendu est annoté palier
par palier*

**Ce que ce chantier a dû rouvrir pour que "live" soit honnête, pas seulement un
dimensionnement statique au démarrage** : le mode distribué existant divise le profil **une
seule fois**, à `/worker/prepare`, par le nombre de workers alors enregistrés — figé pour tout
le tir. Autoscaling live demande deux choses que ce protocole ne permettait pas :

- **Un worker qui rejoint en cours de route** : `MasterOrchestrationHostedService.ExecuteAdaptiveAsync`
  suit le plan de paliers (`Master__StagePlannedWorkers`, posé par l'opérateur) plutôt que
  d'attendre un nombre fixe de workers une seule fois. Un nouveau worker enregistré entre deux
  paliers reçoit, au palier suivant, **une seule** préparation couvrant tous les paliers
  restants — jamais de re-préparation d'un worker déjà lancé (`WorkerCoordinator.Prepare` ne le
  permet pas). Chaque palier de cette préparation est divisé par le compte *prévu* pour ce
  palier (le plan), pas par le nombre de workers réellement actifs à cet instant — c'est ce qui
  permet à des workers dispatchés à des moments différents de contribuer le bon débit combiné une
  fois tous arrivés.
- **Un worker retiré proprement, pas juste tué** : quand le contrôleur réduit le `StatefulSet`
  entre deux paliers moins exigeants, Kubernetes envoie SIGTERM au pod retiré —
  `WorkerCoordinator` annule maintenant le jeton passé à `TargetRpsLoadEngine.RunAsync` sur
  `ApplicationStopping` plutôt que de laisser le process mourir en silence : le tir local
  s'arrête, un rapport **partiel mais réel** est quand même soumis (même chemin que
  `RunAndReportAsync` en fin normale). Un worker qui ne finit pas cet arrêt propre avant
  `terminationGracePeriodSeconds` tombe dans le filet de sécurité déjà existant
  (`MarkDeadIfStale`/`LostWorkers`, chantier [Reprise sur perte d'un
  worker](mode-distribue.md#reprise-sur-perte-dun-worker)) — réutilisé tel quel, pas réinventé.

Le chemin figé existant (`spec.autoscaling` absent) n'exécute **aucune** des lignes ci-dessus :
`MasterOptions.StagePlannedWorkers` reste `null`, `ExecuteAsync` continue exactement comme avant.

Vérifié sur le vrai cluster Docker Desktop, pas de simulation. **Non-régression** : la démo
existante sans `autoscaling` (`testrun-demo.yaml`) rejouée à l'identique — 2 workers fixes,
`Pending → Running → Succeeded`, 0 % d'échec, scale-to-zero final — comportement inchangé.
**Scale-up réel** avec `testrun-autoscaling-demo.yaml` (5 paliers, capacité 5 req/s/worker,
besoins 1 → 4 → 4 → 4 → 1 workers) : le `StatefulSet` démarre à 1 réplique, grossit à 4 avant le
palier exigeant, comme prévu — mais **pas toujours en un seul geste** : sur un tir, les trois
nouveaux workers ont tous rejoint avant le palier suivant ; sur un autre, un seul pod a mis plus
longtemps à démarrer et n'a rejoint qu'au palier d'après, confirmant en conditions réelles la
limite documentée (`scaleAheadSeconds` best-effort, pas une garantie) sans jamais faire échouer
le tir. **Scale-down réel** : le `StatefulSet` redescend de 4 à 1 *pendant* le tir (pas seulement
au `Complete` final) ; les pods retirés (ordinaux les plus hauts, comportement natif du
`StatefulSet`) reçoivent SIGTERM et soumettent bien un rapport partiel avant `SIGKILL`
(confirmé par l'absence de `lostWorkers` et un rapport final cohérent) — le nouveau hook
`ApplicationStopping` fonctionne. **Contre-épreuve** : un `kubectl delete pod --grace-period=0`
sur un worker actif a été absorbé de la même façon (rapport partiel soumis, rien perdu) — plus
robuste qu'attendu, la même mécanique de coupure propre s'applique à un retrait imprévu, pas
seulement à celui piloté par le contrôleur ; le mécanisme de perte réelle (`MarkDeadIfStale`)
reste couvert par un test dédié à son nouveau paramètre `candidates`, en plus de la preuve déjà
apportée par [Reprise sur perte d'un worker](mode-distribue.md#reprise-sur-perte-dun-worker) (processus nu tué
sans aucune grâce).

**Deux vrais bugs trouvés en vérifiant, pas en supposant que ça marchait.** (1) Une résolution
DNS transitoire d'un worker de `StatefulSet` tout juste créé (CoreDNS pas encore propagé)
provoquait une exception non gérée dans `MasterOrchestrationHostedService` — le
`BackgroundService` s'arrêtait alors *sans jamais positionner `Environment.ExitCode`*, si bien
que le `Job` rapportait `Complete` (sortie 0) et `TestRun.status.phase` passait à tort à
`Succeeded` sur un tir qui n'avait jamais eu lieu. Reproduit sur le chemin figé **et** le chemin
adaptatif (même fonction `PrepareAsync`, inchangée) — corrigé une fois pour les deux par une
mince enveloppe try/catch autour de `ExecuteAsync` qui positionne l'échec correctement plutôt que
de crasher en silence ; le correctif lui-même vérifié en laissant la même panne DNS se reproduire
organiquement une deuxième fois et observer le nouveau statut `Failed` correct. (2)
`MasterOptions.Validate()` exigeait `ExpectedWorkers ≥ 1` sans condition, alors que l'opérateur
ne l'émet jamais pour une `TestRun` avec `autoscaling` — le maître plantait au démarrage avant
même d'atteindre `ExecuteAdaptiveAsync`. Corrigé en ignorant cette exigence quand un plan de
paliers est présent.

**Limites assumées, pas résolues ici** : prévisionnel, pas réactif à une métrique observée en
direct (latence, taux d'erreur). `scaleAheadSeconds` est du best-effort — un pod plus lent que
prévu à démarrer laisse le palier tourner temporairement sous-dimensionné, non corrigé
rétroactivement. Le débit déjà figé chez un worker en cours de tir n'est jamais rééquilibré : un
palier futur dont le compte change ne modifie que le nombre de workers qui rejoignent ou
partent. Opérateur Kubernetes uniquement — `docker-compose`/mode autonome n'ont aucun moyen de
créer ou détruire des workers.


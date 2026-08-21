# Protocoles intégrés

## Protocole WebSocket

Un scénario peut ouvrir une connexion WebSocket exactement comme il ouvre une requête HTTP,
via le même `IVirtualUserContext` :

```csharp
WebSocketConnection connection = await context.ConnectWebSocketAsync(uri, configureOptions: null, cancellationToken);
await connection.SendTextAsync("ping", cancellationToken);
WebSocketMessage reply = await connection.ReceiveAsync(cancellationToken);
await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken);
```

Aucun ajout à `StepScope` : `Success()` / `Fail()` suffisaient déjà, le protocole n'a rien de
spécifique à mesurer que la mécanique existante ne couvre pas. Contrairement à `HttpClient`,
une `ClientWebSocket` ne se mutualise pas — `ConnectWebSocketAsync` en crée une nouvelle à
chaque appel ; un scénario qui veut garder une connexion ouverte entre deux itérations doit la
conserver lui-même dans `IVirtualUserContext.State`.

`WebSocketEchoWorkflow` (scénario de référence : connexion → aller-retour d'un message texte →
fermeture propre) s'active via :

```json
"Tempest": { "Workflow": "websocket-echo" }
```

`Workflow` est sans effet dès que `ScenarioFile` est renseigné, qui garde la priorité —
`DynamicCheckoutWorkflow` reste le comportement par défaut si ni l'un ni l'autre n'est précisé.
`Tempest.SampleTarget` expose la cible correspondante, un écho Kestrel pur sur `/ws/echo`.

**Poignée de main de fermeture : un piège vérifié en pratique avant d'écrire la moindre ligne
de production.** Une sonde jetable (`ClientWebSocket` + `HttpListener`, hors du dépôt) a permis
de confirmer l'interopérabilité *avant* de construire la fonctionnalité — et a aussi révélé le
piège à éviter : `WebSocket.CloseAsync` effectue une poignée de main **complète** (elle attend
la trame de fermeture du pair) ; si un seul côté ne participe pas à cet échange, l'appel reste
bloqué indéfiniment. `WebSocketConnection.CloseAsync` délègue tel quel à
`ClientWebSocket.CloseAsync`, mais `WebSocketEchoWorkflow` et `Tempest.SampleTarget` s'assurent
tous deux de répondre à une fermeture reçue — vérifié par un test qui échouerait par *timeout*,
pas par assertion, en cas de régression.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 10 s, contre
`Tempest.SampleTarget`) : 75 itérations, **0 échec, 0 abandon**, `ws-connect` et `ws-echo`
tous deux à 0 % d'échec, seuils respectés.

## Protocole gRPC — unaire

Portée délibérément minimale (choix explicite) : un appel unaire, un aller-retour.

```csharp
EchoService.EchoServiceClient client = new(channel);
PingResponse response = await client.PingAsync(new PingRequest { Message = "ping" }, cancellationToken: cancellationToken);
```

Aucune connexion à mesurer séparément, contrairement à WebSocket : l'établissement HTTP/2 est
transparent et mutualisé par le `GrpcChannel`, exactement comme le pool de `HttpClient`. Une
seule étape (`grpc-ping`) suffit donc. Le contrat (`protos/tempest_echo.proto`) est un fichier
unique référencé par `Tempest.Scenarios` (client), `Tempest.SampleTarget` (serveur) et
`Tempest.UnitTests` (serveur de test) : un désaccord de contrat échoue à la compilation, pas à
l'exécution — même discipline que pour la déclaration JSON `camelCase` de l'étape 4.

`GrpcEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-echo" }
```

Un fichier de configuration complet et exécutable, qui sert de base aux cinq workflows gRPC de
cette page (il suffit d'y changer `Workflow`) :

[!code-json[](../examples/config/appsettings.grpc.json)]
*Fichier exécuté par la CI : `docs/examples/config/appsettings.grpc.json`*

```bash
dotnet src/Tempest.Host/bin/Release/net10.0/Tempest.Host.dll \
  --contentRoot "$(pwd)/docs/examples/config" --environment grpc
```

**Un piège vérifié en pratique, pas supposé** : un vrai démarrage a révélé que Kestrel, en
clair (`http://`, sans TLS), ne multiplexe **pas** HTTP/1.1 et HTTP/2 sur un même port — sans
négociation ALPN (qui exige TLS), un point d'écoute mixte reste silencieusement en HTTP/1.1
seul, ce qui casserait gRPC sans le moindre message d'erreur explicite au niveau applicatif.
`Tempest.SampleTarget` expose donc gRPC sur un port dédié, HTTP/2 pur
(`SampleTargetOptions.GrpcPort`, 5287 par défaut), à côté du port REST/WebSocket habituel.
`GrpcEchoWorkflowOptions.TargetUri` renseigne cette adresse séparée ; omis, le canal est dérivé
de la `BaseAddress` du client HTTP — suffisant dès que la cible négocie les deux protocoles via
TLS, le cas courant en production.

Second piège, cette fois côté client : `SocketsHttpHandler` refuse par défaut de négocier
HTTP/2 en clair (h2c). `GrpcEchoWorkflow` active explicitement
`AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)`
avant d'ouvrir le canal — sans ce commutateur, l'appel échoue silencieusement plutôt que de
révéler la vraie cause.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 10 s, contre
`Tempest.SampleTarget`) : 75 itérations, **0 échec, 0 abandon**, `grpc-ping` à 0 % d'échec,
seuils respectés.

## Protocole gRPC — streaming serveur

Le premier des trois modes de streaming gRPC : un appel, un flux de messages reçus, dont le
nombre est décidé par le serveur.

```csharp
AsyncServerStreamingCall<StreamEchoMessage> call = client.StreamEcho(new StreamEchoRequest { Message = "ping" }, cancellationToken: cancellationToken);
while (await call.ResponseStream.MoveNext(cancellationToken))
{
    StreamEchoMessage current = call.ResponseStream.Current;
}
```

**Chaque message reçu est mesuré comme sa propre étape** (`grpc-stream-message`), pas l'appel
entier : `GrpcStreamEchoWorkflow` ouvre un `StepScope` frais juste avant chaque
`MoveNext`, donc la latence rapportée est celle de l'attente entre deux messages — la mesure
naturelle pour une API de flux, distincte du temps total de l'appel. Aucune étape "connexion"
séparée, même raisonnement que pour l'appel unaire : l'établissement HTTP/2 reste transparent.

Le nombre de messages envoyés est décidé par le **serveur**
(`SampleTargetOptions.StreamMessageCount`, 5 par défaut), jamais par le client : un client
réaliste ne dicte pas le comportement d'un flux auquel il s'abonne, il lit jusqu'à ce que le
serveur cesse d'émettre (`MoveNext` renvoie `false`) — ce dernier appel n'est pas mesuré comme
un message, puisqu'il n'en est pas un.

`GrpcStreamEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-stream-echo" },
"GrpcEcho": { "TargetUri": "http://localhost:5287" }
```

Réutilise la même section `GrpcEcho` (donc la même `TargetUri`) que le scénario unaire : même
besoin, mêmes réglages, pas de raison d'en dupliquer une deuxième.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 10 s, contre
`Tempest.SampleTarget`) : 75 itérations × 5 messages = 375 mesures sur `grpc-stream-message`,
**0 échec** — un nombre qui correspond exactement à la configuration serveur, confirmant que
chaque message du flux est bien compté une fois, ni plus ni moins.

## Protocole gRPC — streaming client

Le second mode, inverse du premier : c'est le **client** qui décide du nombre de messages
envoyés (`GrpcEchoWorkflowOptions.MessageCount`), le serveur se contente d'accumuler jusqu'à
la fermeture du flux montant puis répond une seule fois avec un récapitulatif.

```csharp
AsyncClientStreamingCall<ClientStreamMessage, ClientStreamSummary> call = client.ClientStreamEcho(cancellationToken: cancellationToken);
await call.RequestStream.WriteAsync(new ClientStreamMessage { Message = "ping", Sequence = 0 });
await call.RequestStream.CompleteAsync();
ClientStreamSummary summary = await call.ResponseAsync;
```

**Une seule étape mesure l'appel entier** (`grpc-client-stream-upload`), contrairement au
streaming serveur qui en mesure une par message reçu : un `WriteAsync` sur le flux montant ne
retourne qu'une fois le message mis en tampon, sans attendre de reconnaissance individuelle — il
n'existe donc aucune latence par message à mesurer avant la réponse récapitulative finale, seul
évènement réellement observable de ce côté.

`GrpcClientStreamEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-client-stream-echo" },
"GrpcEcho": { "TargetUri": "http://localhost:5287", "MessageCount": 5 }
```

Réutilise la même section `GrpcEcho` que les deux scénarios précédents (`TargetUri`), avec un
réglage supplémentaire (`MessageCount`) propre aux flux pilotés par le client.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 6+8+3 s, contre
`Tempest.SampleTarget`) : 125 itérations, **0 échec** sur `grpc-client-stream-upload` — le
récapitulatif renvoyé par le serveur (nombre de messages, octets totaux) correspond à chaque
fois exactement à ce qui a été envoyé.

## Protocole gRPC — streaming bidirectionnel

Le troisième et dernier mode : un flux ouvert une seule fois pour toute l'itération, sur lequel
client et serveur échangent en **ping-pong** — écrire un message, attendre son écho, mesurer,
recommencer — plutôt qu'en pipeline (écrire plusieurs messages d'avance sans attendre leurs
échos).

**Ce n'est pas une simplification arbitraire.** `IVirtualUserContext` et `StepScope` sont
documentés comme n'étant touchés que par leur propre travailleur, sans aucune synchronisation —
c'est ce qui permet au chemin de mesure de n'allouer et ne verrouiller rien, pour tous les
scénarios. Un pipeline exigerait une tâche d'écriture et une tâche de lecture tournant en
parallèle au sein d'une même itération, toutes deux ouvrant/clôturant des `StepScope` sur le
même contexte — cela violerait cette invariante et forcerait une synchronisation qui coûterait
à tous les scénarios, pas seulement celui-ci. Le ping-pong reste du vrai bidirectionnel au
niveau du protocole (un seul flux, deux sens, réutilisé pour toute l'itération) : c'est
seulement l'usage qu'en fait ce scénario qui reste séquentiel.

```csharp
AsyncDuplexStreamingCall<BidiStreamMessage, BidiStreamMessage> call = client.BidiStreamEcho(cancellationToken: cancellationToken);
await call.RequestStream.WriteAsync(new BidiStreamMessage { Message = "ping", Sequence = 0 });
await call.ResponseStream.MoveNext(cancellationToken);
BidiStreamMessage echo = call.ResponseStream.Current;
```

Comme le streaming serveur, **chaque message mesure sa propre étape**
(`grpc-bidi-stream-message`) : la latence rapportée est celle entre l'écriture d'un message et
la réception de son écho, message par message.

`GrpcBidiStreamEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-bidi-stream-echo" },
"GrpcEcho": { "TargetUri": "http://localhost:5287", "MessageCount": 5 }
```

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 6+8+3 s, contre
`Tempest.SampleTarget`) : 125 itérations × 5 messages = 625 mesures sur
`grpc-bidi-stream-message`, **0 échec**.


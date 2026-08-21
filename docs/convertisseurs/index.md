# Partir d'un trafic existant

## Convertisseur HAR

Écrire un premier scénario à la main est le moment où l'on abandonne un outil : `tools/Tempest.HarConvert`
part d'un export « Enregistrer tout en HAR » des outils de développement d'un navigateur (Chrome,
Firefox) plutôt que d'une page blanche.

```bash
dotnet run --project tools/Tempest.HarConvert -- session.har scenario.csx --name mon-scenario
```

Conformément à la [décision structurante de la roadmap](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md) — les convertisseurs
génèrent du C#, pas du YAML/JSON — la sortie est un scénario **scripté** (`.csx`), directement
jouable via `--scenario`/`Tempest:ScenarioFile` sans aucun câblage supplémentaire, exactement
comme [`scenarios/scripted-checkout.csx`](https://github.com/coulibalyousmane/Tempest/blob/main/scenarios/scripted-checkout.csx). Chaque requête HAR
devient une étape qui rejoue sa méthode, son chemin, son corps (avec le bon `Content-Type`) et
ses en-têtes via `context.HttpClient.SendAsync`.

Deux filtrages, comptés et rapportés sur la sortie standard, jamais silencieux :
- **Actifs statiques** (`.css`, `.js`, `.png`, polices, etc.) — un HAR de chargement de page
  complet en est majoritairement fait, et aucun n'a de sens à rejouer contre un tir de charge.
- **Hôtes secondaires** — l'hôte cible retenu est le **plus fréquent** du HAR, jamais le premier
  rencontré : un appel tiers (police, analytics, CDN) sans extension reconnue dans son chemin
  apparaît souvent avant le premier appel à l'API réellement testée, et le prendre pour hôte de
  base ferait passer la cible elle-même pour « un autre hôte » — bug réel trouvé en vérifiant un
  vrai HAR, corrigé avant de documenter cette section.

Limite volontaire, documentée en tête du fichier généré : les en-têtes `Authorization`/cookies
capturés sont des valeurs de session réelles, presque certainement expirées au moment de la
conversion — à revoir manuellement, comme toute corrélation dynamique (`Extract`) reste hors de
portée d'un simple mapping de requêtes. Corps multipart (upload de fichier) non pris en charge :
seul le texte brut d'un `postData.text` est converti.

Vérifié par un vrai tir : un HAR reconstitué à partir d'un véritable aller-retour login/catalogue/
checkout contre `Tempest.SampleTarget`, mêlé à un actif statique et à un appel vers un second hôte
sans extension reconnue (pour vérifier le choix de l'hôte le plus fréquent en conditions réelles),
converti puis exécuté via `tempest run scenario.csx --target-url ... --rps 5 --duration 5s` :
les 3 étapes réelles converties, actif statique et hôte secondaire bien exclus, `login` et
`browse` à 0 % d'échec, `checkout` à 100 % d'échec — le jeton capturé avait expiré au moment du
tir, exactement la mise en garde documentée plus haut, pas une anomalie.

## Convertisseur OpenAPI

Deuxième bullet de la phase 5 : `tools/Tempest.OpenApiConvert` part d'une spécification OpenAPI
3.x (JSON — l'export le plus courant, `swagger.json`/`openapi.json`) plutôt que d'un trafic
capturé. Contrairement au convertisseur HAR, une spécification ne décrit que la **forme** d'une
API, jamais des données réelles : la sortie est délibérément un **squelette**, pas un scénario
directement jouable.

```bash
dotnet run --project tools/Tempest.OpenApiConvert -- openapi.json scenario.csx --name mon-scenario
```

Même sortie scriptée (`.csx`) que le convertisseur HAR, pour la même raison — voir la
[décision structurante de la roadmap](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md). Une étape est générée par opération
(méthode + chemin) : les paramètres de chemin et les paramètres de requête **requis** sont
substitués par un placeholder dérivé du type du schéma (ou de l'`example` déclaré s'il y en a
un), le corps `application/json` est un exemple JSON construit récursivement à partir du schéma
(résolution des `$ref` locales vers `components/schemas`, avec garde anti-cycle pour un schéma
auto-référent).

Limites volontaires, comptées et documentées en tête du fichier généré plutôt que silencieuses :
- **Un seul type de contenu** : seul `application/json` est traduit en corps ; une opération dont
  le corps est `multipart/form-data` ou autre est générée sans corps, avec un commentaire dans le
  code plutôt qu'une étape manquante sans explication.
- **Aucun schéma d'authentification traduit** : comme pour le HAR, un jeton ou une clé d'API
  réelle ne peut venir que d'un humain, jamais de la spécification elle-même — les paramètres
  d'en-tête sont tout de même générés, avec un placeholder à remplacer.
- **Paramètres de requête optionnels omis** : seuls les paramètres requis sont ajoutés à l'URL,
  pour garder le squelette lisible plutôt que d'y jeter tous les paramètres facultatifs possibles.
- **YAML non pris en charge** dans cette première version — JSON seul, comme pour la plupart des
  exports d'outils (Swashbuckle, Swagger UI).

Vérifié par un vrai tir contre `Tempest.SampleTarget`, à partir d'une spécification décrivant
fidèlement ses trois routes réelles (`login`, `catalogue`, `checkout`, avec `$ref` vers des
schémas `components` pour les corps). Deux tirs, pour distinguer le squelette généré de son
usage réel :
- **Squelette non modifié** (`tempest run scenario.csx --target-url ... --rps 5 --duration 5s`) :
  `login` et `listProducts` à 0 % d'échec, `checkout` à 100 % d'échec — le placeholder
  `Authorization` n'est jamais un jeton valide, exactement la limite documentée plus haut.
- **Squelette complété à la main** (jeton lu dans la réponse de `login`, identifiant de produit lu
  dans la réponse de `listProducts`, exactement ce qu'un humain ajouterait) : les 3 étapes à 0 %
  d'échec.

## Convertisseur Postman

Troisième et dernier bullet de la phase 5 : `tools/Tempest.PostmanConvert` part d'une collection
Postman exportée (v2.1, le format courant de « Export » depuis l'application). Même nature de
sortie que le convertisseur OpenAPI — un **squelette**, pas un scénario directement jouable :
une collection décrit des requêtes qu'on a construites à la main dans Postman, pas un trafic
capturé avec de vraies données.

```bash
dotnet run --project tools/Tempest.PostmanConvert -- collection.json scenario.csx --name mon-scenario
```

Même sortie scriptée (`.csx`), pour la même raison — voir la [décision structurante de la
roadmap](https://github.com/coulibalyousmane/Tempest/blob/main/ROADMAP.md). Les dossiers d'une collection (`item` imbriqués) sont parcourus
récursivement ; chaque requête feuille devient un step, nommé d'après le nom Postman qualifié par
ses dossiers parents (`Auth / Login`). Les variables **de collection** (`collection.variable`,
`{{nom}}`) sont substituées dans l'URL, les en-têtes et le corps — y compris quand elles résolvent
l'hôte lui-même (`{{baseUrl}}/api/x`) : une fois l'URL rendue absolue par la substitution, seul
`PathAndQuery` est conservé, l'hôte reste toujours celui de `--target-url` à l'exécution, quelle
que soit la valeur de `{{baseUrl}}` dans la collection.

Limites volontaires, comptées et documentées en tête du fichier généré :
- **Un environnement Postman séparé n'est pas lu** dans cette première version — seules les
  variables déclarées au niveau de la collection elle-même le sont. Une variable `{{...}}` sans
  valeur connue devient un placeholder générique (`valeur`), compté plutôt que silencieux.
- **Corps `formdata` non pris en charge** (comme le corps multipart du HAR et le
  `multipart/form-data` de l'OpenAPI) — seuls les modes `raw` et `urlencoded` sont traduits.
- **Aucun schéma d'authentification Postman traduit** (`auth` de la requête ou de la collection)
  — même raison que pour le HAR et l'OpenAPI : une vraie valeur ne peut venir que d'un humain.
- **Un placeholder substitué dans un corps JSON peut casser sa syntaxe** s'il apparaît sans
  guillemets — convention Postman courante pour injecter un nombre (`"productId":{{id}}`). Trouvé
  en vérifiant une vraie conversion (voir plus bas) : à corriger à la main, comme les autres
  placeholders, pas une régression du convertisseur — une variable Postman n'a pas de schéma pour
  deviner si elle attend une chaîne ou un nombre, contrairement à l'OpenAPI.

Vérifié par deux vrais tirs contre `Tempest.SampleTarget`, à partir d'une collection décrivant
fidèlement ses trois routes réelles, avec un dossier (`Auth / Login`) et une variable de
collection résolvant l'hôte (`{{baseUrl}}`) :
- **Squelette non modifié** : `Auth / Login` et `Catalogue` à 0 % d'échec, `Checkout` à 100 %
  d'échec — placeholder d'authentification et corps JSON invalide (`{{productId}}` non résolu et
  injecté sans guillemets), exactement les limites documentées plus haut.
- **Squelette complété à la main** (même principe que pour l'OpenAPI) : les 3 étapes à 0 % d'échec.

## Proxy enregistreur

Dernier bullet de la phase 5 : `tools/Tempest.RecorderProxy` capture du trafic HTTP réel en
direct, sans étape d'export manuel — à la différence du convertisseur HAR, qui suppose une
capture déjà faite (« Enregistrer tout en HAR » du navigateur). Scope volontairement réduit par
rapport au *recorder* de Gatling : un **reverse proxy à cible unique**, pas un proxy HTTP
générique multi-hôtes avec interception TLS (MITM) — cohérent avec le modèle `--target-url`
unique de `tempest run`, et ça évite tout le chantier certificat/confiance qu'un vrai proxy HTTPS
exigerait pour une fonctionnalité encore conditionnée à un vrai public.

```bash
dotnet run --project tools/Tempest.RecorderProxy -- --target-url http://localhost:5299 --out scenario.csx [--listen http://localhost:8888] [--name mon-scenario]
```

Pointez votre client (navigateur configuré avec cette adresse comme hôte, `curl`, l'application
elle-même) vers `--listen` au lieu de la cible réelle : chaque requête est retransmise fidèlement
vers `--target-url` — méthode, en-têtes, corps — et la réponse réelle relayée telle quelle,
pendant que la requête est enregistrée en arrière-plan. À l'arrêt (Ctrl+C, ou
`POST /__tempest-recorder/stop` pour un pilotage scripté), le proxy s'arrête proprement puis
génère le scénario **en réutilisant `HarConverter.Convert` tel quel** — la capture en direct
alimente exactement la même forme de données (`HarEntry`) qu'un export HAR de navigateur, donc le
filtrage des actifs statiques et la génération du `.csx` sont acquis gratuitement, sans code
dupliqué.

Limites volontaires, documentées :
- **HTTP seul dans cette première version** — pas d'interception TLS. Une cible HTTPS ne peut pas
  être enregistrée par ce proxy tel qu'il est aujourd'hui.
- **Seul un corps de type textuel reconnu est capturé** (JSON, XML, texte brut, HTML, JavaScript,
  formulaire, GraphQL, d'après `Content-Type`) ; un corps binaire (upload de fichier, image, ...)
  est retransmis fidèlement en direct mais jamais enregistré dans le scénario généré — même
  limite que le corps multipart du convertisseur HAR, pas une nouvelle.
- **Authentification/cookies capturés** : mêmes valeurs de session réelles que pour le HAR,
  potentiellement expirées au moment du tir — mais capturées et rejouées dans la foulée, sans le
  délai d'un export/conversion manuel, ce qui les rend souvent *plus* susceptibles d'être encore
  valides qu'un HAR exporté puis converti plus tard (vérifié ci-dessous).

Vérifié par un vrai tir de bout en bout contre `Tempest.SampleTarget` : proxy démarré, une vraie
session login/catalogue/checkout envoyée à travers lui (statuts 200 confirmés à travers le proxy,
identiques à ceux de la cible directe), arrêté via `/__tempest-recorder/stop`, scénario généré (4
requêtes enregistrées, 4 étapes retenues). Rejoué immédiatement via `tempest run` contre la même
cible : les 4 étapes à 0 % d'échec, y compris `checkout` — le jeton capturé était encore valide,
contrairement au HAR de la section précédente où l'export manuel avait laissé le temps au jeton
d'expirer. **Clôt entièrement la phase 5.**


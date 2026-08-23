# Zéro erreur, et pourtant inutilisable : la dette d'ordonnancement

*Version anglaise : [Zero errors, and still unusable](scheduling-debt.md).*

Quatre outils de test de charge, le même scénario, la même cible, le même débit, la même minute.
Les 99e centiles qu'ils rapportent vont **d'une seconde à près de trente**.

Aucun des quatre ne ment, et aucun ne dissimule quoi que ce soit : chacun rend compte, à sa
manière, de ce qui lui a échappé. Mais les chiffres se classent selon une règle qu'aucun rapport
n'affiche : **plus un outil a délivré peu de la charge demandée, plus son 99e centile est
flatteur.** Et un seul des quatre a délivré la totalité.

Ce qui les sépare porte un nom — la **dette d'ordonnancement** — et ne dépend pas de la marque de
l'outil mais d'un choix de conception que font tous les injecteurs. Cet article explique lequel,
pourquoi la plupart des campagnes de charge ne le voient jamais, et comment le débusquer avec
l'outil que vous utilisez déjà, Sirocco ou non.

## Ce que le modèle ouvert garantit, et ce qu'il ne garantit pas

Rappel court, parce que c'est le socle. Dans un **modèle fermé**, un utilisateur virtuel envoie une
requête, attend la réponse, puis envoie la suivante. Quand la cible ralentit, l'injecteur ralentit
avec elle : il envoie moins de requêtes, et celles qu'il n'envoie pas sont précisément celles qui
auraient été lentes. Le rapport s'améliore à mesure que le système se dégrade. C'est le
*coordinated omission* décrit par Gil Tene.

Le **modèle ouvert** (*arrival rate*) corrige ça : les requêtes partent à un rythme imposé par
l'horloge, pas par la cible. k6, Gatling, JMeter, NBomber et Sirocco en ont tous un. « Nous évitons
le *coordinated omission* » n'est donc le différenciateur de personne.

Mais le modèle ouvert est une promesse sur l'**intention** : *n* requêtes par seconde seront
**planifiées**. Il ne dit rien sur le fait qu'elles seront **envoyées**. Un injecteur a des
ressources finies — un plafond d'utilisateurs virtuels, des sockets, des cœurs. Le jour où il ne
peut plus envoyer à l'heure, le modèle ouvert se dégrade silencieusement en autre chose.

C'est ce moment-là qui nous intéresse.

## La dette d'ordonnancement, définie

Pour une requête donnée, trois instants comptent :

- l'instant où elle **devait** partir, imposé par le profil de charge ;
- l'instant où elle est **réellement** partie ;
- l'instant où la réponse est arrivée.

D'où trois durées, dont deux seulement sont habituellement publiées :

```text
Service  = arrivée de la réponse − départ réel      → ce que chronomètre un client HTTP
Response = arrivée de la réponse − départ prévu     → ce que l'appelant a réellement attendu
Dette    = départ réel           − départ prévu     → le retard propre à l'injecteur
```

(Ces trois grandeurs sont les propriétés `ServiceTicks`, `ResponseTicks` et `SchedulingDelayTicks`
de [`MetricResult`](https://github.com/coulibalyousmane/Sirocco/blob/main/src/Sirocco.Domain/Metrics/MetricResult.cs).)

La dette n'est pas la latence de la cible. C'est le retard de **votre injecteur**. Et un injecteur
en retard n'a que deux options, qui faussent le rapport de deux façons différentes :

1. **Abandonner** la requête. Elle disparaît alors de tous les percentiles — et comme on n'abandonne
   que lorsque tout est déjà occupé, les requêtes sacrifiées sont exactement celles qui auraient été
   les plus lentes.
2. **L'envoyer en retard**. Elle est mesurée, mais si on la chronomètre depuis son départ *réel*
   plutôt que depuis son départ *prévu*, l'attente subie par l'appelant disparaît du chiffre.

Les deux sont défendables. Ce qui ne l'est pas, c'est de ne pas le dire.

## Pourquoi une campagne de charge ordinaire ne montre rien de tout ça

Le [benchmark comparatif](https://github.com/coulibalyousmane/Sirocco/blob/main/benchmark/results/RESULTS.md)
de ce dépôt joue le même scénario contre la même cible saturée avec les quatre outils. Dette
d'ordonnancement observée : **19,1 ms** sur un p99 de 337,9 ms. Cinq pour cent. Rien à raconter.

La raison n'est pas que les outils sont bons. C'est que **la cible était bonne**.

Cette cible-là se protège : au-delà de 8 commandes simultanées, elle attend 50 ms qu'une place se
libère, puis rend un `503`. Elle **déleste**. Un délestage libère l'utilisateur virtuel en 50 ms,
l'injecteur ne prend jamais de retard, et il n'y a effectivement rien à voir.

Or la plupart des systèmes réels ne délestent pas. Ils **mettent en file** : un pool de threads,
un pool de connexions, une file de messages, une base de données avec ses 200 connexions. Ils ne
vous refusent pas l'entrée, ils vous font patienter. C'est même souvent présenté comme une vertu.

D'où l'expérience : **changer exactement une chose.**

## L'expérience

Même cible, même scénario (login puis checkout), mêmes quatre outils, même orchestration que le
benchmark publié. Une seule variable change : `QUEUE_WAIT_MS`, l'attente maximale de la cible
avant de refuser.

[!code-yaml[](../../benchmark/docker-compose.yml)]

Capacité de la cible : 8 places, 80 à 150 ms de traitement simulé, soit environ **70 requêtes par
seconde**. On demande un débit **constant de 100 req/s pendant 60 secondes** — franchement
au-dessus, pour que la file croisse linéairement et qu'il n'y ait qu'une seule pente à expliquer.

Le plafond d'utilisateurs virtuels est fixé à **50 partout où il existe** :

[!code-javascript[](../../benchmark/k6/checkout.js)]

Partout où il existe, parce qu'il n'existe pas partout — et c'est un point important plutôt qu'un
détail d'intendance. Le `injectOpen` de Gatling et le `Simulation.Inject` de NBomber n'ont **aucun**
plafond de concurrence : ils créent autant d'utilisateurs que le débit l'exige. Ce n'est pas un
défaut, c'est une conception différente, et elle produit un comportement différent sous saturation.

Une commande, les deux passes (cible qui met en file, puis témoin qui déleste) :

```bash
./benchmark/saturation.sh
```

## Les mesures

[!include[](_mesures-fr.md)]

## Lire ces chiffres

Commençons par le tableau qu'on ne pense jamais à lire : **un seul outil a délivré la charge
demandée.** Les trois autres sont en retrait, et chacun le dit — un compteur d'abandons chez k6,
des exceptions réseau chez Gatling, un `failCount` chez NBomber. Personne ne triche.

Mais mettez les deux tableaux côte à côte, et le classement saute aux yeux :

- Sirocco, qui a délivré **tout** ce qu'on lui demandait, rapporte le p99 le plus élevé.
- NBomber, à quelques dizaines de requêtes près, rapporte pratiquement le même — et comme il n'a
  aucune notion de dette d'ordonnancement, cet accord est une **validation croisée** du chiffre
  *Response* de Sirocco. Deux mécanismes indépendants, même réponse.
- Gatling, à qui il manque un cinquième de la charge, rapporte à peu près la moitié.
- k6, à qui il manque près d'un tiers, rapporte plus de vingt fois moins.

**Le p99 est d'autant plus beau que l'outil a moins travaillé.** Ce n'est pas une coïncidence, et
ce n'est pas la même cause dans les trois cas.

### Le temps d'attente est conservé ; seul son lieu change

L'attente totale est une propriété de la **cible** : 100 requêtes par seconde qui arrivent dans un
système qui n'en traite que 70, ça fait une file qui grandit, quel que soit
l'outil qui pousse. Ce qui change d'un injecteur à l'autre, c'est **où cette file s'accumule** et
**qui la compte** :

- **Gatling et NBomber ne bornent pas leur concurrence.** Ils créent autant d'utilisateurs virtuels
  qu'il faut, les requêtes partent à l'heure, et l'attente se produit *dans la requête elle-même*.
  Elle apparaît donc naturellement dans la latence rapportée — d'où l'accord de NBomber avec
  Sirocco. Mais le prix se lit dans le tableau de livraison : à mille utilisateurs simultanés,
  Gatling a épuisé les sockets de la machine (`NoRouteToHostException`), et un cinquième de la
  charge n'a jamais atteint la cible. Son p99 porte donc sur ce qui restait, c'est-à-dire sur les
  requêtes les plus rapides. **C'est l'angle mort du modèle non borné : l'injecteur devient le
  goulot, et rien dans la latence ne le dit.**
- **k6 et Sirocco bornent leur concurrence** — 50 utilisateurs virtuels ici. La file ne peut alors
  plus s'accumuler dans la cible : elle s'accumule **dans l'injecteur**. C'est précisément la dette
  d'ordonnancement. Et c'est là que les deux outils divergent.

### k6 abandonne ; Sirocco retarde et mesure

Face à une requête qu'il ne peut pas lancer à l'heure, k6 l'**abandonne**. C'est un choix
défendable : il protège l'injecteur et évite l'effet boule de neige. Et k6 ne le cache pas, deux
fois plutôt qu'une. Pendant le tir :

```text
level=warning msg="Insufficient VUs, reached 50 active VUs and cannot initialize more"
```

et dans le résumé final, `dropped_iterations` compte les itérations sacrifiées — près d'un tiers de
la charge demandée.

Mais regardez dans quelle monnaie cette information est libellée. Une itération abandonnée ne s'est
jamais produite : elle n'a donc **aucune latence**, et ne pèse sur **aucun percentile**. Le taux
d'échec reste à 0 %, puisque aucune requête n'a échoué. Or votre SLO, votre seuil de CI et votre
alerte sont écrits en latence et en taux d'erreur. Dans cette monnaie-là, le tir est vert.

Et il y a pire que « vert » : ce sont systématiquement **les itérations les plus lentes** qui sont
abandonnées, puisque l'abandon survient précisément quand tous les utilisateurs virtuels sont
occupés à attendre. Le percentile publié porte donc sur un échantillon dont on a retiré la queue de
distribution. C'est le *coordinated omission* qui rentre par la fenêtre après avoir été mis dehors
par la porte du modèle ouvert.

Sirocco fait l'autre choix : il **envoie la requête en retard** et la chronomètre depuis l'instant
où elle aurait dû partir. Le même événement physique devient de la latence — donc quelque chose
qu'un seuil de latence attrape tout seul, sans qu'on ait eu besoin de penser à surveiller un
compteur dont on ignorait l'existence. Et il publie l'écart séparément, pour qu'on sache si on
regarde la lenteur de la cible ou celle de l'injecteur.

Aucun des deux comportements n'est faux. Mais un seul des deux survit à un tableau de bord où l'on
ne regarde que le p99 et le taux d'erreur.

### Là où la dette se loge — et où elle se cache

Le tableau par étape mérite un arrêt. La dette est portée par le **premier pas de l'itération**, et
par lui seul : c'est lui qui hérite de l'instant de départ théorique, les pas suivants partent de
leur propre instant réel (leur imputer la dette la compterait deux fois).

Conséquence très concrète : sur l'étape `checkout`, Response et Service sont **identiques**. Un
opérateur qui ouvre son rapport et regarde l'étape qui l'intéresse ne voit **rien**. Il faut lire
`__iteration`, ou le premier pas.

## Ce que Sirocco rate aussi

Un article qui ne dirait que du bien de son propre outil ne servirait à rien. Trois angles morts,
trouvés en menant cette expérience :

- **Le drapeau « l'injecteur a décroché » n'a pas été levé.** Sirocco signale `InjectorFellBehind`
  quand des jetons planifiés n'ont jamais été émis. Ici, ils ont **tous** été émis — simplement
  très en retard. Ce drapeau détecte un injecteur qui renonce, pas un injecteur qui traîne. Seule
  la jauge de dette voit le second cas.
- **Le rapport se déclare « fiable ».** Le drapeau `isTrustworthy` ne regarde que les mesures
  perdues faute de place dans le canal. Un rapport peut donc être parfaitement fiable et décrire un
  système que personne n'expédierait.
- **La dette n'est visible que sur le premier pas** (ci-dessus). C'est cohérent, c'est documenté,
  et ça reste un piège de lecture.

Et les limites du protocole lui-même, qui sont réelles : une seule machine sans isolation, des tirs
séquentiels, des réglages de saturation choisis pour tenir sur un poste personnel. L'épuisement de
sockets qui a tronqué le tir de Gatling en est la conséquence directe — sur une machine plus
généreuse, ou avec un injecteur distribué, ce plafond-là se déplacerait. Ce que cette expérience
démontre est un **mécanisme**, pas un classement de performance entre outils : n'en tirez pas
« Gatling est deux fois plus rapide », tirez-en « lisez la charge délivrée avant de lire le p99 ».

## Ce qu'il faut en retenir, quel que soit votre outil

Quatre vérifications qui ne demandent ni Sirocco ni changement d'outillage :

1. **Sachez si votre injecteur borne sa concurrence.** C'est la question qui décide de tout le
   reste. S'il ne la borne pas (Gatling, NBomber en modèle ouvert), l'attente apparaîtra dans vos
   latences — mais rien ne vous dira si c'est l'injecteur qui a lâché. S'il la borne (k6 via
   `maxVUs`, Sirocco via `--max-vus`), la file passe dans l'injecteur et devient invisible pour qui
   ne sait pas où regarder.
2. **Comparez la durée réelle du tir à la durée du profil.** C'est le signal le moins cher qui
   existe, et tous les outils l'affichent. Un profil de 60 secondes qui en prend nettement plus est
   un injecteur qui a passé la différence à rattraper son retard. Aucun percentile ne vous le dira.
3. **Comparez le nombre d'itérations obtenues au nombre demandé.** Un écart de 30 % entre les deux
   est un tir qui n'a pas eu lieu, pas un tir réussi.
4. **Cherchez le compteur d'abandons de votre outil, et alertez dessus.** Sous k6, c'est
   `dropped_iterations` — et un seuil sur ce compteur (`thresholds: { dropped_iterations: ['count<1'] }`)
   attrape ce qu'un seuil de p99 laissera toujours passer. Si vous plafonnez `maxVUs`, ce seuil
   n'est pas une précaution : c'est ce qui rend votre p99 interprétable.

Et une remarque de conception, en sortant du sujet des outils : **un système qui déleste est plus
facile à tester honnêtement qu'un système qui met en file.** Le premier vous refuse une réponse et
le dit ; le second vous fait attendre et laisse chaque couche traversée penser que tout va bien.
Le tableau témoin de cette page le montre à l'envers : la cible qui déleste affiche un tiers
d'échecs et paraît en plus mauvais état — alors que c'est elle qui traite ses appelants
correctement.

## Refaire l'expérience

Tout est dans le dépôt, et le protocole est du code, pas une description :

- [`benchmark/saturation.sh`](https://github.com/coulibalyousmane/Sirocco/blob/main/benchmark/saturation.sh) — les deux passes.
- [`benchmark/docker-compose.yml`](https://github.com/coulibalyousmane/Sirocco/blob/main/benchmark/docker-compose.yml) — la cible et son unique variable.
- [`benchmark/k6/checkout.js`](https://github.com/coulibalyousmane/Sirocco/blob/main/benchmark/k6/checkout.js), [`benchmark/gatling/CheckoutSimulation.java`](https://github.com/coulibalyousmane/Sirocco/blob/main/benchmark/gatling/CheckoutSimulation.java), [`benchmark/nbomber/Program.cs`](https://github.com/coulibalyousmane/Sirocco/blob/main/benchmark/nbomber/Program.cs), [`benchmark/scenarios/sirocco-checkout.yaml`](https://github.com/coulibalyousmane/Sirocco/blob/main/benchmark/scenarios/sirocco-checkout.yaml) — le même scénario dans les quatre outils.
- [`benchmark/results-saturation/SATURATION.md`](https://github.com/coulibalyousmane/Sirocco/blob/main/benchmark/results-saturation/SATURATION.md) — le rapport du tir commenté ici. Comme pour le benchmark publié, les sorties brutes des quatre outils ne sont pas versionnées : elles se régénèrent en une commande.

Les chiffres de cette page ne sont pas recopiés à la main : ils sont générés par
`benchmark/normalize --saturation` depuis ces sorties, et la version anglaise inclut le **même**
fragment. Il ne peut donc pas y avoir un chiffre juste dans une langue et faux dans l'autre.

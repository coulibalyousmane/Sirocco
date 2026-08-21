# Interface de ligne de commande

`Tempest.Host` reste piloté par `appsettings.json`/variables d'environnement — adapté à un hôte
qui reste actif. `Tempest.Cli` (`tempest`) répond à un besoin différent : lancer un tir depuis un
terminal, avec des options qui priment sur la configuration, et se terminer à la fin.

```bash
dotnet run --project samples/Tempest.SampleTarget -c Release   # écoute sur :5281

dotnet run --project src/Tempest.Cli -c Release -- run scenarios/smoke-test.yaml \
  --target-url http://localhost:5281 \
  --rps 20 --duration 30s \
  --threshold "__iteration:ResponseP95Milliseconds:LessThan:500" \
  --report-html rapport.html --report-json rapport.json
```

Sans fichier de scénario, `--workflow <nom>` sélectionne un scénario intégré
(`dynamic-checkout` par défaut, `websocket-echo`, `grpc-echo`, `grpc-stream-echo`,
`grpc-client-stream-echo`, `grpc-bidi-stream-echo`) — les mêmes que `Tempest.Host`. Le profil de
charge est soit constant (`--rps <n> --duration <d>`), soit une rampe
(`--from-rps <n> --to-rps <n> --duration <d>`) ; `--duration` accepte `30s`, `5m`, `1h`, `500ms`,
ou un nombre de secondes. `--threshold` (répétable) prend le format
`etape:grandeur:comparaison:limite[:nom]`, les mêmes valeurs que `ThresholdRule`.

`--rps`/`--from-rps`/`--to-rps` et `--threshold` restent optionnels si un `appsettings.json` du
répertoire courant fournit déjà `Tempest:Profile` / `Tempest:Thresholds` (même format que celui de
`Tempest.Host`) : la CLI complète la configuration plutôt que de l'exiger en double. `--target-url`
suit la même règle avec `Tempest:TargetBaseUrl`.

Le processus se termine toujours à la fin du tir (`ExitAfterRun` implicite), avec le même code de
sortie que `Tempest.Host` — c'est la seule différence de comportement documentée entre les deux :
un hôte reste actif pour continuer à servir `/metrics`, une CLI non. `--report-html`/`--report-json`
compensent cette différence en écrivant le rapport final sur disque avant que le processus ne
disparaisse (`--report-json` produit le même format que `/report`, directement réutilisable par
`Tempest.Compare`).

Vérifié par de vrais tirs : profil constant et rampe contre `Tempest.SampleTarget`, scénario
déclaratif (`scenarios/smoke-test.yaml`), workflow intégré, seuil respecté (code de sortie 0) et
seuil délibérément trop strict (code de sortie 1, `[ECHEC] trop strict (observe : 121,34)`),
rapports HTML et JSON écrits sur disque, et repli sur un `appsettings.json` du répertoire courant
en l'absence de `--rps`/`--target-url`.

**Limites de cette première version**, documentées dans `tempest run --help` : un seul processus
autonome — pas de mode distribué (Master/Workers) depuis la CLI, qui reste l'affaire de
`Tempest.Host`.


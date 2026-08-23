# Sirocco.Cli

CLI de [Sirocco](https://github.com/coulibalyousmane/Sirocco), moteur de test de charge async
cloud-native en C#/.NET.

```bash
sirocco run scenario.yaml --target-url http://localhost:5299 --rps 50 --duration 30s
```

Lance un tir de charge autonome et se termine à la fin, avec un code de sortie reflétant le
verdict des seuils (0 si tous respectés ou si aucun n'est configuré, 1 sinon). Sans fichier de
scénario, `--workflow <nom>` sélectionne un scénario intégré (`dynamic-checkout` par défaut,
`websocket-echo`, `grpc-echo`, `grpc-stream-echo`, `grpc-client-stream-echo`,
`grpc-bidi-stream-echo`). Le profil de charge est constant (`--rps <n> --duration <d>`) ou une
rampe (`--from-rps <n> --to-rps <n> --duration <d>`).

`sirocco run --help` documente toutes les options (`--threshold`, `--report-html`,
`--report-json`, `--max-vus`) et les limites de cette première version.

Le mode distribué (Master/Workers) reste l'affaire de `Sirocco.Host` — voir le
[README du dépôt](https://github.com/coulibalyousmane/Sirocco#readme).

# Sirocco.Extensions.Sql

Extension [Sirocco](https://github.com/coulibalyousmane/Sirocco) pour **SQL** (SQLite) : un protocole
réellement différent de HTTP, pas une variation de plus autour du client HTTP partagé. Chaque
itération exécute deux étapes réelles — un `SELECT` paramétré et un `INSERT` — chacune chronométrée
comme n'importe quelle étape.

## ⚠️ À consommer par `PackageReference`, pas par `--plugin-package`

Sa dépendance `Microsoft.Data.Sqlite` embarque une **bibliothèque native** (`e_sqlite3`), qui vit
dans `runtimes/<rid>/native` d'un paquet. La restauration transitive de `--plugin-package` ne sert
que les actifs `lib/<tfm>` : ce chemin échoue donc sur `DllNotFoundException` — vérifié par un vrai
tir, pas supposé.

La voie qui fonctionne passe par MSBuild, qui sait résoudre les actifs natifs :

```bash
dotnet new classlib -o mon-tir-sql && cd mon-tir-sql
dotnet add package Sirocco.Extensions.Sql
dotnet publish -c Release -o publish

SIROCCO_SQL_PLUGIN_CONNECTION_STRING="Data Source=./ma-base.db" \
  sirocco run publish/Sirocco.Extensions.Sql.dll --target-url http://localhost:1 --rps 20 --duration 30s
```

`--target-url` reste exigé par la CLI mais n'est d'aucun usage ici : la cible est la base de
données, même convention que les workflows `grpc-echo`/`websocket-echo`.

| Variable | Défaut | Rôle |
|---|---|---|
| `SIROCCO_SQL_PLUGIN_CONNECTION_STRING` | *(requis)* | Chaîne de connexion SQLite |
| `SIROCCO_SQL_PLUGIN_ROW_COUNT` | `1000` | Produits de référence semés au démarrage (graine fixe) |

`SetUpAsync` sème les produits et active le mode WAL, indispensable dès que plusieurs utilisateurs
virtuels partagent le même fichier.

## Écrire la vôtre

Cette extension est un exemple travaillé du contrat de plugin : un `IWorkflow` ordinaire, compilé
indépendamment du dépôt. Voir le [guide d'écriture d'extension](https://github.com/coulibalyousmane/Sirocco/blob/main/docs/extensions/guide.md).

Publiée sous la licence Apache 2.0.

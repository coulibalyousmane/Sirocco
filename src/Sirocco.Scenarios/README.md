# Sirocco.Scenarios

Scénarios de référence et format déclaratif du moteur de test de charge
[Sirocco](https://github.com/coulibalyousmane/Sirocco) : `DynamicCheckoutWorkflow` (login →
browse → checkout, avec jeton mis en cache et corrélation dynamique), `WebSocketEchoWorkflow`,
les quatre modes gRPC (unaire, streaming serveur/client/bidirectionnel), et
`DeclarativeWorkflow`/`ScenarioDefinitionLoader` (scénarios YAML/JSON sans recompilation).

```csharp
using Sirocco.Scenarios.Declarative;

// Un scenario ecrit en YAML/JSON, sans code C# :
IWorkflow workflow = new DeclarativeWorkflow(ScenarioDefinitionLoader.LoadFromFile("scenario.yaml"));
```

Réutilisables tels quels contre votre propre cible, ou comme modèle pour écrire un `IWorkflow`
(`Sirocco.Domain`) sur mesure. Pour dérouler l'un de ces scénarios à un débit donné, voir
`Sirocco.Application` (le moteur) et `Sirocco.Infrastructure` (la chaîne de mesure).
Documentation complète, y compris le format déclaratif, dans le
[dépôt](https://github.com/coulibalyousmane/Sirocco#readme).

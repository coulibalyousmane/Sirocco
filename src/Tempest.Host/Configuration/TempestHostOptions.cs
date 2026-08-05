using Tempest.Domain.Metrics;

namespace Tempest.Host.Configuration;

/// <summary>
/// Configuration du tir porte par cet hote. Correspond a la section <c>Tempest</c> de
/// <c>appsettings.json</c>.
/// </summary>
public sealed class TempestHostOptions
{
    /// <summary>Valeur par defaut du plafond d'utilisateurs virtuels.</summary>
    public const int DEFAULT_MAX_VIRTUAL_USERS = 200;

    /// <summary>Valeur de <see cref="Workflow"/> selectionnant <c>DynamicCheckoutWorkflow</c>.</summary>
    public const string DYNAMIC_CHECKOUT_WORKFLOW = "dynamic-checkout";

    /// <summary>Valeur de <see cref="Workflow"/> selectionnant <c>WebSocketEchoWorkflow</c>.</summary>
    public const string WEBSOCKET_ECHO_WORKFLOW = "websocket-echo";

    /// <summary>Valeur de <see cref="Workflow"/> selectionnant <c>GrpcEchoWorkflow</c>.</summary>
    public const string GRPC_ECHO_WORKFLOW = "grpc-echo";

    /// <summary>Valeur de <see cref="Role"/> : l'hote tire seul, comportement inchange.</summary>
    public const string ROLE_STANDALONE = "standalone";

    /// <summary>Valeur de <see cref="Role"/> : l'hote orchestre des workers sans tirer lui-meme.</summary>
    public const string ROLE_MASTER = "master";

    /// <summary>Valeur de <see cref="Role"/> : l'hote attend les ordres d'un maitre.</summary>
    public const string ROLE_WORKER = "worker";

    /// <summary>
    /// Role de cet hote dans le tir : <see cref="ROLE_STANDALONE"/> (par defaut, comportement
    /// inchange), <see cref="ROLE_MASTER"/> ou <see cref="ROLE_WORKER"/> (mode distribue).
    /// </summary>
    public string Role { get; init; } = ROLE_STANDALONE;

    /// <summary>Adresse de base de la cible a tester.</summary>
    public required string TargetBaseUrl { get; init; }

    /// <summary>Plafond d'utilisateurs virtuels concurrents.</summary>
    public int MaxVirtualUsers { get; init; } = DEFAULT_MAX_VIRTUAL_USERS;

    /// <summary>
    /// Paliers du profil de charge, dans l'ordre d'execution.
    /// <para>
    /// Reste deliberement minimal : composer un profil a partir de quelques paliers dans
    /// <c>appsettings.json</c> suffit, et c'est deja declaratif (aucune recompilation requise
    /// pour changer la courbe de charge). Ce qui restait fige en C# — la sequence d'etapes du
    /// scenario — est couvert par <see cref="ScenarioFile"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<LoadStageOptions> Profile { get; init; } = [];

    /// <summary>
    /// Chemin d'un fichier de scenario declaratif (<c>.yaml</c>, <c>.yml</c> ou <c>.json</c>).
    /// <para>
    /// Si renseigne, l'hote construit un <c>DeclarativeWorkflow</c> a partir de ce fichier
    /// plutot que d'utiliser <c>DynamicCheckoutWorkflow</c> — le scenario devient modifiable
    /// sans recompiler. <see langword="null"/> par defaut : le scenario code en dur reste le
    /// comportement inchange tant que ce champ n'est pas explicitement renseigne.
    /// </para>
    /// </summary>
    public string? ScenarioFile { get; init; }

    /// <summary>
    /// Scenario code en dur a utiliser quand <see cref="ScenarioFile"/> n'est pas renseigne :
    /// <see cref="DYNAMIC_CHECKOUT_WORKFLOW"/> (par defaut), <see cref="WEBSOCKET_ECHO_WORKFLOW"/>
    /// ou <see cref="GRPC_ECHO_WORKFLOW"/>.
    /// Sans effet des que <see cref="ScenarioFile"/> est present, qui garde la priorite.
    /// </summary>
    public string Workflow { get; init; } = DYNAMIC_CHECKOUT_WORKFLOW;

    /// <summary>
    /// Regles de succes/echec evaluees en fin de tir. Vide par defaut : sans seuil, il n'y a
    /// pas de gate, et le tir ne peut jamais "echouer" au sens CI du terme.
    /// </summary>
    public IReadOnlyList<ThresholdRule> Thresholds { get; init; } = [];

    /// <summary>
    /// Si vrai, l'hote s'arrete des la fin du tir, avec un code de sortie reflechissant le
    /// verdict des seuils (0 si tous respectes ou si aucun n'est configure, 1 sinon).
    /// <para>
    /// Faux par defaut : l'hote reste actif pour continuer a servir <c>/metrics</c>, comme
    /// avant l'introduction des seuils. C'est le scenario d'integration continue — un script
    /// qui attend un code de sortie — qui doit activer ce comportement explicitement.
    /// </para>
    /// </summary>
    public bool ExitAfterRun { get; init; }
}
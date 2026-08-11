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

    /// <summary>Valeur de <see cref="Workflow"/> selectionnant <c>GrpcStreamEchoWorkflow</c>.</summary>
    public const string GRPC_STREAM_ECHO_WORKFLOW = "grpc-stream-echo";

    /// <summary>Valeur de <see cref="Workflow"/> selectionnant <c>GrpcClientStreamEchoWorkflow</c>.</summary>
    public const string GRPC_CLIENT_STREAM_ECHO_WORKFLOW = "grpc-client-stream-echo";

    /// <summary>Valeur de <see cref="Workflow"/> selectionnant <c>GrpcBidiStreamEchoWorkflow</c>.</summary>
    public const string GRPC_BIDI_STREAM_ECHO_WORKFLOW = "grpc-bidi-stream-echo";

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

    /// <summary>
    /// Nombre d'utilisateurs virtuels : un plafond en modele ouvert, un effectif exact en modele
    /// ferme (voir <see cref="ClosedModelDuration"/>). Sans effet si <see cref="RampVus"/> est
    /// renseigne : l'effectif suit alors ses paliers, jusqu'a leur pic.
    /// </summary>
    public int MaxVirtualUsers { get; init; } = DEFAULT_MAX_VIRTUAL_USERS;

    /// <summary>
    /// Paliers du profil de charge (modele ouvert), dans l'ordre d'execution. Sans effet si
    /// <see cref="ClosedModelDuration"/> est renseigne.
    /// <para>
    /// Reste deliberement minimal : composer un profil a partir de quelques paliers dans
    /// <c>appsettings.json</c> suffit, et c'est deja declaratif (aucune recompilation requise
    /// pour changer la courbe de charge). Ce qui restait fige en C# — la sequence d'etapes du
    /// scenario — est couvert par <see cref="ScenarioFile"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<LoadStageOptions> Profile { get; init; } = [];

    /// <summary>
    /// Duree du modele <b>ferme</b> a effectif fixe : si renseigne, ce tir ignore
    /// <see cref="Profile"/> et fait tourner exactement <see cref="MaxVirtualUsers"/>
    /// utilisateurs virtuels sans aucune pause imposee jusqu'a expiration de cette duree, plutot
    /// que de viser un debit cible. <see langword="null"/> par defaut : le modele ouvert reste le
    /// comportement inchange. Sans effet si <see cref="RampVus"/> est renseigne, qui garde la
    /// priorite.
    /// </summary>
    public TimeSpan? ClosedModelDuration { get; init; }

    /// <summary>
    /// Paliers du modele <b>ferme</b> a effectif <b>variable</b> (montee/descente d'utilisateurs) :
    /// si renseigne, ce tir ignore <see cref="ClosedModelDuration"/> et <see cref="Profile"/>, et
    /// l'effectif concurrent suit ces paliers au lieu de rester fixe. Vide par defaut.
    /// <para>
    /// Meme mise en garde de rapport que l'effectif fixe (<see cref="Domain.Metrics.LoadTestReport.ClosedModel"/>) :
    /// sans echeancier theorique, il n'y a pas de correction du <i>coordinated omission</i> ici
    /// non plus, les chiffres ne sont jamais comparables a un tir en modele ouvert.
    /// </para>
    /// </summary>
    public IReadOnlyList<VirtualUserStageOptions> RampVus { get; init; } = [];

    /// <summary>Vrai si ce tir utilise une montee d'utilisateurs plutot qu'un effectif fixe.</summary>
    public bool IsRampingVus => RampVus.Count > 0;

    /// <summary>
    /// Nombre total d'iterations a repartir entre tous les utilisateurs virtuels (executeur
    /// <b>iterations partagees</b>) : premier arrive, premier servi, <see cref="MaxVirtualUsers"/>
    /// restant un plafond de concurrence comme en modele ouvert. <see langword="null"/> par
    /// defaut. Prioritaire sur <see cref="Profile"/>, mais c'est <see cref="IterationsPerVirtualUser"/>
    /// qui garde la priorite si les deux sont renseignes — voir sa remarque.
    /// </summary>
    public long? SharedIterations { get; init; }

    /// <summary>
    /// Nombre d'iterations que chaque utilisateur virtuel doit executer independamment des autres
    /// (executeur <b>iterations par utilisateur</b>) : contrairement a <see cref="SharedIterations"/>,
    /// chaque utilisateur en fait exactement ce nombre, jamais plus, jamais moins — voir la
    /// remarque de classe de <c>VirtualUserWorker</c>. <see langword="null"/> par defaut.
    /// Prioritaire sur <see cref="SharedIterations"/> et <see cref="Profile"/> si renseigne, mais
    /// <see cref="ClosedModelDuration"/> et <see cref="RampVus"/> gardent la priorite sur celui-ci.
    /// </summary>
    public long? IterationsPerVirtualUser { get; init; }

    /// <summary>
    /// Vrai si ce tir utilise un modele sans echeancier theorique — effectif fixe
    /// (<see cref="ClosedModelDuration"/>), montee d'utilisateurs (<see cref="RampVus"/>) ou un
    /// des deux executeurs pilotes par un nombre d'iterations (<see cref="SharedIterations"/>,
    /// <see cref="IterationsPerVirtualUser"/>) — qui partagent tous la meme mise en garde de
    /// rapport, faute de correction du <i>coordinated omission</i> possible.
    /// </summary>
    public bool IsClosedModel =>
        ClosedModelDuration is not null || IsRampingVus || SharedIterations is not null || IterationsPerVirtualUser is not null;

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
    /// <see cref="DYNAMIC_CHECKOUT_WORKFLOW"/> (par defaut), <see cref="WEBSOCKET_ECHO_WORKFLOW"/>,
    /// <see cref="GRPC_ECHO_WORKFLOW"/> ou <see cref="GRPC_STREAM_ECHO_WORKFLOW"/>.
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

    /// <summary>
    /// Secret partage authentifiant le control plane distribue (<c>/master/register</c>,
    /// <c>/master/report</c>, <c>/worker/prepare</c>, <c>/worker/start</c>), exige en
    /// <c>Authorization: Bearer &lt;secret&gt;</c>.
    /// <para>
    /// <see langword="null"/> par defaut : ces endpoints restent ouverts tant que l'operateur ne
    /// configure pas explicitement ce secret — comme la REST API locale de k6, jamais
    /// authentifiee (la securite y repose sur le perimetre reseau). Sans effet en mode
    /// autonome, qui n'expose aucun de ces endpoints.
    /// </para>
    /// </summary>
    public string? ClusterSharedSecret { get; init; }

    /// <summary>
    /// Si renseigne, le rapport final est ecrit en HTML a ce chemin a la fin du tir, en plus de
    /// tout endpoint expose. <see langword="null"/> par defaut. C'est <c>Tempest.Cli</c> qui en a
    /// besoin : contrairement a l'hote, il ne reste pas actif apres le tir pour servir
    /// <c>/report.html</c>.
    /// </summary>
    public string? ReportHtmlPath { get; init; }

    /// <summary>
    /// Si renseigne, le rapport final est ecrit en JSON a ce chemin a la fin du tir, dans le
    /// meme format que <c>/report</c> — de quoi l'archiver comme reference pour
    /// <c>Tempest.Compare</c> sans dependre d'un endpoint HTTP encore actif.
    /// </summary>
    public string? ReportJsonPath { get; init; }
}
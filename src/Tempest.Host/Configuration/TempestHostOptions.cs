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

    /// <summary>Valeur par defaut de <see cref="TimeSeriesIntervalSeconds"/>.</summary>
    public const double DEFAULT_TIME_SERIES_INTERVAL_SECONDS = 2d;

    /// <summary>Valeur par defaut de <see cref="LiveDashboardRefreshSeconds"/>.</summary>
    public const int DEFAULT_LIVE_DASHBOARD_REFRESH_SECONDS = 3;

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

    /// <summary>
    /// Adresse de base de la cible a tester. Reste obligatoire meme si <see cref="Scenarios"/> est
    /// renseigne : elle sert alors de valeur par defaut pour tout scenario qui ne precise pas la
    /// sienne (<see cref="ScenarioOptions.TargetBaseUrl"/>).
    /// </summary>
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
    /// Ecart entre deux releves de la trajectoire du tir (<see cref="Tempest.Domain.Metrics.LoadTestReport.TimeSeries"/>),
    /// en secondes. <see cref="DEFAULT_TIME_SERIES_INTERVAL_SECONDS"/> par defaut : assez fin pour
    /// une trajectoire lisible, assez large pour qu'un tir de plusieurs heures ne produise pas des
    /// dizaines de milliers de points. Sans effet si <see cref="Scenarios"/> est renseigne : voir
    /// la limite documentee sur ce champ.
    /// </summary>
    public double TimeSeriesIntervalSeconds { get; init; } = DEFAULT_TIME_SERIES_INTERVAL_SECONDS;

    /// <summary>
    /// Intervalle d'auto-rechargement de <c>/report/live.html</c>, en secondes.
    /// <see cref="DEFAULT_LIVE_DASHBOARD_REFRESH_SECONDS"/> par defaut. Sans effet si
    /// <see cref="Scenarios"/> est renseigne, pour la meme raison que <c>/report/live</c> n'est
    /// pas alimente en mode scenarios concurrents (voir la limite documentee sur ce champ).
    /// </summary>
    public int LiveDashboardRefreshSeconds { get; init; } = DEFAULT_LIVE_DASHBOARD_REFRESH_SECONDS;

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
    /// <para>
    /// Sans effet si <see cref="Scenarios"/> est renseigne : chaque scenario porte alors ses
    /// propres seuils (<see cref="ScenarioOptions.Thresholds"/>).
    /// </para>
    /// </summary>
    public IReadOnlyList<ThresholdRule> Thresholds { get; init; } = [];

    /// <summary>
    /// Plafond de debit global, en requetes par seconde, applique <b>par-dessus</b> le modele de
    /// charge choisi — ouvert ou ferme, sous n'importe laquelle de ses formes.
    /// <para>
    /// <see langword="null"/> par defaut : aucun plafond, comportement inchange. Une fois
    /// renseigne, le debit reellement transmis aux utilisateurs virtuels ne peut jamais depasser
    /// cette valeur, meme si le profil ou l'effectif configure en produirait davantage. Le retard
    /// ainsi impose se mesure comme n'importe quelle dette d'ordonnancement — voir
    /// <see cref="Tempest.Application.Execution.RateCappedScheduler"/> pour le detail du mecanisme.
    /// </para>
    /// <para>
    /// Si <see cref="Scenarios"/> est renseigne, cette valeur sert de plafond par defaut pour tout
    /// scenario qui ne precise pas le sien (<see cref="ScenarioOptions.MaxRequestsPerSecond"/>) —
    /// meme convention que <see cref="TargetBaseUrl"/>.
    /// </para>
    /// </summary>
    public double? MaxRequestsPerSecond { get; init; }

    /// <summary>
    /// Scenarios <b>concurrents</b> de ce tir : si renseigne, ce tir ignore <see cref="Profile"/>,
    /// <see cref="ClosedModelDuration"/>, <see cref="RampVus"/>, <see cref="SharedIterations"/>,
    /// <see cref="IterationsPerVirtualUser"/>, <see cref="ScenarioFile"/>, <see cref="Workflow"/> et
    /// <see cref="Thresholds"/> — chaque scenario porte les siens, isoles de ceux des autres
    /// scenarios du meme tir jusque dans son propre registre d'etapes (deux scenarios peuvent
    /// declarer une etape de meme nom sans que leurs mesures se melangent). Vide par defaut : le
    /// scenario unique reste le comportement inchange tant que ce champ n'est pas renseigne.
    /// <para>
    /// Limites de cette premiere version : mode distribue non pris en charge, <c>/report/live</c>
    /// et <c>/metrics</c> non alimentes (voir <c>MultiScenarioHost</c>) — seuls <c>/report</c>,
    /// <c>/report.html</c> et <c>/thresholds</c> le sont, une fois le tir termine. La trajectoire
    /// (<see cref="TimeSeriesIntervalSeconds"/>) n'est pas non plus relevee pour ce mode : chaque
    /// scenario tourne dans <c>MultiScenarioRunner</c>, qui construit sa chaine de mesure sans
    /// enregistreur de serie temporelle.
    /// </para>
    /// </summary>
    public IReadOnlyList<ScenarioOptions> Scenarios { get; init; } = [];

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
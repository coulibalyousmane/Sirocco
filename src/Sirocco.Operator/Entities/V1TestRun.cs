using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace Sirocco.Operator.Entities;

/// <summary>
/// Ressource <c>TestRun</c> du bullet ROADMAP "Opérateur Kubernetes" : décrit un tir distribué
/// (cible, profil de charge, nombre de workers) et laisse l'opérateur créer et détruire les
/// ressources Kubernetes qui le portent (voir <see cref="Sirocco.Operator.TestRunResources"/>).
/// </summary>
[KubernetesEntity(Group = "sirocco.dev", ApiVersion = "v1alpha1", Kind = "TestRun")]
public partial class V1TestRun : CustomKubernetesEntity<V1TestRun.TestRunSpec, V1TestRun.TestRunStatus>
{
    public sealed class TestRunSpec
    {
        /// <summary>Image (avec tag) de <c>Sirocco.Host</c> à utiliser pour le maître et les workers.</summary>
        public string Image { get; set; } = string.Empty;

        /// <summary>URL de base de la cible du tir (<c>Sirocco__TargetBaseUrl</c>).</summary>
        public string TargetBaseUrl { get; set; } = string.Empty;

        /// <summary>Nombre de workers à créer (taille du <c>StatefulSet</c>).</summary>
        public int WorkerReplicas { get; set; } = 1;

        /// <summary>Nombre maximal d'utilisateurs virtuels (<c>Sirocco__MaxVirtualUsers</c>).</summary>
        public int? MaxVirtualUsers { get; set; }

        /// <summary>Étapes du profil de charge (<c>Sirocco__Profile__N__*</c>), au moins une.</summary>
        public List<ProfileStage> Profile { get; set; } = [];

        /// <summary>
        /// Workflow nommé à exécuter (voir <c>SiroccoHostOptions</c> : <c>websocket-echo</c>,
        /// <c>grpc-echo</c>, etc.). Non renseigné = workflow par défaut
        /// (<c>DynamicCheckoutWorkflow</c>).
        /// </summary>
        public string? Workflow { get; set; }

        /// <summary>
        /// Référence (nom de <c>Secret</c> + clé) vers le secret partagé de cluster
        /// (<c>Sirocco__ClusterSharedSecret</c>). Jamais la valeur en clair dans la ressource.
        /// </summary>
        public V1SecretKeySelector? ClusterSharedSecretRef { get; set; }

        /// <summary>Fenêtre d'enregistrement des workers, en secondes (<c>Master__RegistrationTimeoutSeconds</c>).</summary>
        public int? RegistrationTimeoutSeconds { get; set; }

        /// <summary>Délai sans heartbeat avant qu'un worker soit déclaré mort (<c>Master__WorkerDeadAfterSeconds</c>).</summary>
        public int? WorkerDeadAfterSeconds { get; set; }

        /// <summary>Plafond absolu d'attente des rapports finaux (<c>Master__ReportTimeoutSeconds</c>).</summary>
        public int? ReportTimeoutSeconds { get; set; }

        /// <summary>Fréquence de heartbeat de chaque worker (<c>Worker__HeartbeatIntervalSeconds</c>).</summary>
        public int? HeartbeatIntervalSeconds { get; set; }

        /// <summary>Fréquence de sondage du tableau de bord live (<c>Master__LivePollIntervalSeconds</c>).</summary>
        public int? LivePollIntervalSeconds { get; set; }

        /// <summary>
        /// Plan de paliers calculé à partir du débit cible plutôt qu'un nombre de workers fixe
        /// (voir <see cref="Sirocco.Operator.TestRunResources.ComputeStageWorkerCounts"/>). Non
        /// renseigné (par défaut) : <see cref="WorkerReplicas"/> garde son sens actuel, aucun
        /// changement de comportement. Renseigné : prime sur <see cref="WorkerReplicas"/> (ignoré,
        /// sans erreur de validation).
        /// </summary>
        public AutoscalingSpec? Autoscaling { get; set; }
    }

    public sealed class ProfileStage
    {
        public int FromRps { get; set; }

        public int ToRps { get; set; }

        public int DurationSeconds { get; set; }
    }

    public sealed class AutoscalingSpec
    {
        /// <summary>
        /// Capacité déclarée d'un seul worker, en requêtes/s — une hypothèse de l'opérateur du
        /// cluster, jamais une mesure. Sert à calculer le nombre de workers requis à chaque
        /// palier du profil (<c>ceil(max(FromRps, ToRps) / MaxRequestsPerSecondPerWorker)</c>).
        /// </summary>
        public int MaxRequestsPerSecondPerWorker { get; set; }

        /// <summary>Plancher de workers, quel que soit le palier. Défaut 1.</summary>
        public int MinWorkerReplicas { get; set; } = 1;

        /// <summary>
        /// Garde-fou contre un palier mal renseigné qui exploserait le nombre de pods créés.
        /// </summary>
        public int MaxWorkerReplicas { get; set; }

        /// <summary>
        /// Avance, en secondes, avec laquelle le <c>StatefulSet</c> est ajusté avant qu'un
        /// palier plus exigeant ne démarre, pour laisser le temps au pod de démarrer et de
        /// s'auto-enregistrer. Best-effort : aucune garantie que le worker soit prêt à l'instant
        /// exact du palier si le démarrage du pod prend plus longtemps que ce délai.
        /// </summary>
        public int ScaleAheadSeconds { get; set; } = 15;
    }

    public sealed class TestRunStatus
    {
        public const string PHASE_PENDING = "Pending";
        public const string PHASE_RUNNING = "Running";
        public const string PHASE_SUCCEEDED = "Succeeded";
        public const string PHASE_FAILED = "Failed";

        public string Phase { get; set; } = PHASE_PENDING;

        public string? Message { get; set; }

        public int WorkersReady { get; set; }

        public DateTime? CompletionTime { get; set; }
    }
}
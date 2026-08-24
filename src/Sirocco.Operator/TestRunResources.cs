using k8s.Models;
using Sirocco.Operator.Entities;

namespace Sirocco.Operator;

/// <summary>
/// Transforme une <see cref="V1TestRun"/> en objets Kubernetes désirés (maître, workers,
/// services), sans appel réseau — pure et testable sans cluster, même esprit que
/// <c>ClusterCertificatePinning</c> côté <c>Sirocco.Host</c>.
/// <para>
/// Les workers sont un <see cref="V1StatefulSet"/> derrière un service headless : le maître
/// adresse chaque worker individuellement (<c>/worker/prepare</c>, <c>/worker/start</c>),
/// exactement comme chaque conteneur <c>worker1</c>/<c>worker2</c> a un nom DNS stable dans
/// <c>docker-compose.yml</c> — un <c>Deployment</c> derrière un service à répartition de charge
/// masquerait les workers derrière une seule adresse et ne convient pas ici.
/// </para>
/// </summary>
public static class TestRunResources
{
    public const int MASTER_PORT = 5299;
    public const int WORKER_PORT = 5300;

    public static string MasterServiceName(V1TestRun entity) => $"{entity.Metadata.Name}-master";

    public static string WorkerServiceName(V1TestRun entity) => $"{entity.Metadata.Name}-worker";

    public static V1Service BuildMasterService(V1TestRun entity) => new()
    {
        ApiVersion = "v1",
        Kind = "Service",
        Metadata = new V1ObjectMeta
        {
            Name = MasterServiceName(entity),
            NamespaceProperty = entity.Metadata.NamespaceProperty,
            OwnerReferences = [BuildOwnerReference(entity)],
        },
        Spec = new V1ServiceSpec
        {
            Selector = MasterPodLabels(entity),
            Ports = [new V1ServicePort { Port = MASTER_PORT, TargetPort = MASTER_PORT }],
        },
    };

    public static V1Service BuildWorkerHeadlessService(V1TestRun entity) => new()
    {
        ApiVersion = "v1",
        Kind = "Service",
        Metadata = new V1ObjectMeta
        {
            Name = WorkerServiceName(entity),
            NamespaceProperty = entity.Metadata.NamespaceProperty,
            OwnerReferences = [BuildOwnerReference(entity)],
        },
        Spec = new V1ServiceSpec
        {
            ClusterIP = "None",
            Selector = WorkerPodLabels(entity),
            Ports = [new V1ServicePort { Port = WORKER_PORT, TargetPort = WORKER_PORT }],
        },
    };

    /// <summary>
    /// <c>restartPolicy: Never</c> et <c>backoffLimit: 0</c> : le maître positionne déjà
    /// <c>Environment.ExitCode</c> selon le succès/échec des seuils quand <c>ExitAfterRun</c>
    /// est actif — un seul essai suffit pour que la condition <c>Complete</c>/<c>Failed</c> du
    /// Job reflète honnêtement ce résultat, sans avoir à parser le rapport de tir.
    /// </summary>
    public static V1Job BuildMasterJob(V1TestRun entity) => new()
    {
        ApiVersion = "batch/v1",
        Kind = "Job",
        Metadata = new V1ObjectMeta
        {
            Name = MasterServiceName(entity),
            NamespaceProperty = entity.Metadata.NamespaceProperty,
            OwnerReferences = [BuildOwnerReference(entity)],
        },
        Spec = new V1JobSpec
        {
            BackoffLimit = 0,
            Template = new V1PodTemplateSpec
            {
                Metadata = new V1ObjectMeta { Labels = MasterPodLabels(entity) },
                Spec = new V1PodSpec
                {
                    RestartPolicy = "Never",
                    SecurityContext = BuildPodSecurityContext(),
                    Containers = [BuildContainer(entity, MASTER_PORT, BuildMasterEnv(entity))],
                },
            },
        },
    };

    public static V1StatefulSet BuildWorkerStatefulSet(V1TestRun entity) => new()
    {
        ApiVersion = "apps/v1",
        Kind = "StatefulSet",
        Metadata = new V1ObjectMeta
        {
            Name = WorkerServiceName(entity),
            NamespaceProperty = entity.Metadata.NamespaceProperty,
            OwnerReferences = [BuildOwnerReference(entity)],
        },
        Spec = new V1StatefulSetSpec
        {
            ServiceName = WorkerServiceName(entity),
            Replicas = entity.Spec.Autoscaling is not null ? ComputeStageWorkerCounts(entity)[0] : entity.Spec.WorkerReplicas,
            Selector = new V1LabelSelector { MatchLabels = WorkerPodLabels(entity) },
            Template = new V1PodTemplateSpec
            {
                Metadata = new V1ObjectMeta { Labels = WorkerPodLabels(entity) },
                Spec = new V1PodSpec
                {
                    RestartPolicy = "Always",
                    SecurityContext = BuildPodSecurityContext(),
                    Containers = [BuildContainer(entity, WORKER_PORT, BuildWorkerEnv(entity))],
                },
            },
        },
    };

    /// <summary>
    /// Nombre de workers requis à chaque palier de <see cref="V1TestRun.TestRunSpec.Profile"/>,
    /// à partir du débit cible de ce palier et de la capacité déclarée par worker
    /// (<see cref="V1TestRun.AutoscalingSpec.MaxRequestsPerSecondPerWorker"/>) — prévisionnel,
    /// calculé une seule fois à partir du profil complet, jamais mesuré en direct. Pure et
    /// testable sans cluster, même idiome que la division plafonnée déjà utilisée pour
    /// <c>maxVirtualUsersPerWorker</c> côté maître (<c>MasterOrchestrationHostedService.BuildPrepareRequest</c>).
    /// </summary>
    public static int[] ComputeStageWorkerCounts(V1TestRun entity)
    {
        V1TestRun.AutoscalingSpec autoscaling = entity.Spec.Autoscaling
            ?? throw new InvalidOperationException("Spec.Autoscaling doit etre renseigne pour calculer un plan de paliers.");

        return
        [
            .. entity.Spec.Profile.Select(stage =>
            {
                int peakRps = Math.Max(stage.FromRps, stage.ToRps);
                int required = (int)Math.Ceiling(peakRps / (double)autoscaling.MaxRequestsPerSecondPerWorker);
                return Math.Clamp(required, autoscaling.MinWorkerReplicas, autoscaling.MaxWorkerReplicas);
            }),
        ];
    }

    private static Dictionary<string, string> MasterPodLabels(V1TestRun entity) => new()
    {
        ["sirocco.dev/testrun"] = entity.Metadata.Name,
        ["sirocco.dev/role"] = "master",
    };

    private static Dictionary<string, string> WorkerPodLabels(V1TestRun entity) => new()
    {
        ["sirocco.dev/testrun"] = entity.Metadata.Name,
        ["sirocco.dev/role"] = "worker",
    };

    private static V1OwnerReference BuildOwnerReference(V1TestRun entity) => new()
    {
        ApiVersion = entity.ApiVersion,
        Kind = entity.Kind,
        Name = entity.Metadata.Name,
        Uid = entity.Metadata.Uid,
        Controller = true,
        BlockOwnerDeletion = true,
    };

    private static V1Container BuildContainer(V1TestRun entity, int port, List<V1EnvVar> env) => new()
    {
        Name = "sirocco-host",
        Image = entity.Spec.Image,
        Env = env,
        Ports = [new V1ContainerPort { ContainerPort = port }],
        SecurityContext = BuildContainerSecurityContext(),
    };

    /// <summary>
    /// L'image de <c>Sirocco.Host</c> porte déjà <c>USER 1654</c> ; ces deux contextes en font une
    /// exigence plutôt qu'un défaut. <c>runAsNonRoot</c> fait <b>refuser le démarrage</b> du pod si
    /// l'image revenait à root, au lieu de le laisser passer en silence — c'est la différence entre
    /// une propriété vérifiée par le cluster et une propriété seulement déclarée dans l'image.
    /// <para>
    /// <c>readOnlyRootFilesystem</c> n'y est délibérément pas : un scénario scripté est compilé par
    /// Roslyn et un plugin NuGet est téléchargé dans le cache, tous deux hors de <c>/app</c>. Le
    /// verrouiller demanderait de monter les emplacements temporaires un par un, ce qui déborde du
    /// constat traité ici — limite énoncée plutôt que devinée.
    /// </para>
    /// </summary>
    private static V1PodSecurityContext BuildPodSecurityContext() => new()
    {
        RunAsNonRoot = true,
        SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
    };

    private static V1SecurityContext BuildContainerSecurityContext() => new()
    {
        AllowPrivilegeEscalation = false,
        Capabilities = new V1Capabilities { Drop = ["ALL"] },
    };

    private static List<V1EnvVar> BuildCommonEnv(V1TestRun entity)
    {
        List<V1EnvVar> env =
        [
            new V1EnvVar { Name = "Sirocco__TargetBaseUrl", Value = entity.Spec.TargetBaseUrl },
        ];

        if (!string.IsNullOrEmpty(entity.Spec.Workflow))
        {
            env.Add(new V1EnvVar { Name = "Sirocco__Workflow", Value = entity.Spec.Workflow });
        }

        if (entity.Spec.ClusterSharedSecretRef is { } secretRef)
        {
            env.Add(new V1EnvVar
            {
                Name = "Sirocco__ClusterSharedSecret",
                ValueFrom = new V1EnvVarSource { SecretKeyRef = secretRef },
            });
        }

        return env;
    }

    private static List<V1EnvVar> BuildMasterEnv(V1TestRun entity)
    {
        List<V1EnvVar> env = BuildCommonEnv(entity);

        env.Add(new V1EnvVar { Name = "ASPNETCORE_URLS", Value = $"http://+:{MASTER_PORT}" });
        env.Add(new V1EnvVar { Name = "Sirocco__Role", Value = "master" });
        env.Add(new V1EnvVar { Name = "Sirocco__ExitAfterRun", Value = "true" });

        if (entity.Spec.Autoscaling is not null)
        {
            int[] stageWorkerCounts = ComputeStageWorkerCounts(entity);
            for (int i = 0; i < stageWorkerCounts.Length; i++)
            {
                env.Add(new V1EnvVar { Name = $"Master__StagePlannedWorkers__{i}", Value = stageWorkerCounts[i].ToString() });
            }
        }
        else
        {
            env.Add(new V1EnvVar { Name = "Master__ExpectedWorkers", Value = entity.Spec.WorkerReplicas.ToString() });
        }

        if (entity.Spec.MaxVirtualUsers is { } maxVirtualUsers)
        {
            env.Add(new V1EnvVar { Name = "Sirocco__MaxVirtualUsers", Value = maxVirtualUsers.ToString() });
        }

        if (entity.Spec.RegistrationTimeoutSeconds is { } registrationTimeoutSeconds)
        {
            env.Add(new V1EnvVar { Name = "Master__RegistrationTimeoutSeconds", Value = registrationTimeoutSeconds.ToString() });
        }

        if (entity.Spec.WorkerDeadAfterSeconds is { } workerDeadAfterSeconds)
        {
            env.Add(new V1EnvVar { Name = "Master__WorkerDeadAfterSeconds", Value = workerDeadAfterSeconds.ToString() });
        }

        if (entity.Spec.ReportTimeoutSeconds is { } reportTimeoutSeconds)
        {
            env.Add(new V1EnvVar { Name = "Master__ReportTimeoutSeconds", Value = reportTimeoutSeconds.ToString() });
        }

        if (entity.Spec.LivePollIntervalSeconds is { } livePollIntervalSeconds)
        {
            env.Add(new V1EnvVar { Name = "Master__LivePollIntervalSeconds", Value = livePollIntervalSeconds.ToString() });
        }

        for (int i = 0; i < entity.Spec.Profile.Count; i++)
        {
            V1TestRun.ProfileStage stage = entity.Spec.Profile[i];
            env.Add(new V1EnvVar { Name = $"Sirocco__Profile__{i}__FromRps", Value = stage.FromRps.ToString() });
            env.Add(new V1EnvVar { Name = $"Sirocco__Profile__{i}__ToRps", Value = stage.ToRps.ToString() });
            env.Add(new V1EnvVar { Name = $"Sirocco__Profile__{i}__DurationSeconds", Value = stage.DurationSeconds.ToString() });
        }

        return env;
    }

    /// <summary>
    /// <c>Worker__SelfUrl</c> ne peut pas être une valeur littérale identique pour toutes les
    /// réplicas d'un même <see cref="V1StatefulSet"/> (même gabarit de pod) : elle est calculée
    /// par chaque pod à partir de son propre nom, injecté par la Downward API
    /// (<c>POD_NAME</c>), via l'expansion de variables native de Kubernetes (<c>$(POD_NAME)</c>)
    /// — aucun changement de code applicatif necessaire côté <c>Sirocco.Host</c>.
    /// </summary>
    private static List<V1EnvVar> BuildWorkerEnv(V1TestRun entity)
    {
        List<V1EnvVar> env = BuildCommonEnv(entity);
        string headlessServiceName = WorkerServiceName(entity);

        env.Add(new V1EnvVar { Name = "ASPNETCORE_URLS", Value = $"http://+:{WORKER_PORT}" });
        env.Add(new V1EnvVar { Name = "Sirocco__Role", Value = "worker" });
        env.Add(new V1EnvVar { Name = "Worker__MasterUrl", Value = $"http://{MasterServiceName(entity)}:{MASTER_PORT}" });
        env.Add(new V1EnvVar
        {
            Name = "POD_NAME",
            ValueFrom = new V1EnvVarSource { FieldRef = new V1ObjectFieldSelector { FieldPath = "metadata.name" } },
        });
        env.Add(new V1EnvVar
        {
            Name = "Worker__SelfUrl",
            Value = $"http://$(POD_NAME).{headlessServiceName}:{WORKER_PORT}",
        });

        if (entity.Spec.HeartbeatIntervalSeconds is { } heartbeatIntervalSeconds)
        {
            env.Add(new V1EnvVar { Name = "Worker__HeartbeatIntervalSeconds", Value = heartbeatIntervalSeconds.ToString() });
        }

        return env;
    }
}
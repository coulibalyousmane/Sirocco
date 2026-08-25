using k8s;
using k8s.Models;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using Sirocco.Operator.Entities;

namespace Sirocco.Operator.Controllers;

/// <summary>
/// Réconciliation de <see cref="V1TestRun"/> : crée les 4 ressources filles
/// (<see cref="TestRunResources"/>) si absentes, reflète la condition du <c>Job</c> maître dans
/// <c>TestRun.status</c>, et réduit le <c>StatefulSet</c> des workers à 0 réplique une fois le
/// tir terminé — c'est le "workers ... détruits automatiquement" du bullet ROADMAP. Aucune
/// finalisation personnalisée : les <c>OwnerReference</c> posées par
/// <see cref="TestRunResources"/> suffisent, le garbage collection natif de Kubernetes fait le
/// reste quand la <see cref="V1TestRun"/> elle-même est supprimée.
/// <para>
/// La condition du <c>Job</c> n'est pas observée par un watch — <c>RequeueAfter</c> fait
/// sonder ce contrôleur toutes les <see cref="_pollInterval"/> tant que le tir n'est pas
/// terminé, plutôt que d'ajouter un deuxième mécanisme de suivi pour un seul champ.
/// </para>
/// </summary>
// SEC-6 (AUDIT.md) : ces quatre-la etaient toutes en RbacVerb.All ("*"), y compris sur des
// ressources filles jamais mises a jour ni supprimees par ce controleur (voir ReconcileAsync,
// EnsureExistsAsync, ScaleWorkersToZeroAsync) — un operateur compromis pouvait donc supprimer ou
// modifier n'importe quel Job/StatefulSet/Service existant du cluster, pas seulement les siens.
// Reduits aux verbes reellement exerces : Job (cree, jamais relu par nom apres coup ni mis a
// jour), StatefulSet (cree, puis relu et mis a jour pour l'autoscaling et le retour a 0 replique),
// Service (cree une fois, jamais modifie). Aucun Delete nulle part : la suppression des ressources
// filles se fait par le garbage collection natif de Kubernetes via OwnerReference, jamais par un
// appel explicite de ce controleur (voir DeletedAsync).
[EntityRbac(typeof(V1TestRun), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Job), Verbs = RbacVerb.Get | RbacVerb.Create)]
[EntityRbac(typeof(V1StatefulSet), Verbs = RbacVerb.Get | RbacVerb.Create | RbacVerb.Update)]
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.Get | RbacVerb.Create)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch)]
public sealed class TestRunController(IKubernetesClient client, ILogger<TestRunController> logger) : IEntityController<V1TestRun>
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public async Task<ReconciliationResult<V1TestRun>> ReconcileAsync(V1TestRun entity, CancellationToken cancellationToken)
    {
        // Depuis que Sirocco.Host refuse de demarrer un role master/worker sans secret partage,
        // une TestRun sans clusterSharedSecretRef ne produirait que des pods en CrashLoopBackOff,
        // dont le motif ne serait lisible qu'en lisant leurs journaux. Echouer ici, avant de creer
        // quoi que ce soit, met le motif dans "kubectl describe testrun". Pas de resondage : c'est
        // la modification du spec qui redeclenchera la reconciliation, pas le temps qui passe.
        if (entity.Spec.ClusterSharedSecretRef is null)
        {
            entity.Status.Phase = V1TestRun.TestRunStatus.PHASE_FAILED;
            entity.Status.Message = "spec.clusterSharedSecretRef est requis : le maitre et les workers refusent de "
                + "demarrer sans secret partage de control plane. Creez un Secret et referencez-le "
                + "(voir deploy/samples/testrun-demo.yaml).";
            entity.Status.CompletionTime = DateTime.UtcNow;
            await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError("TestRun {Name} refusee : spec.clusterSharedSecretRef absent.", entity.Metadata.Name);
            }

            return ReconciliationResult<V1TestRun>.Success(entity);
        }

        await EnsureExistsAsync(TestRunResources.BuildMasterService(entity), cancellationToken).ConfigureAwait(false);
        await EnsureExistsAsync(TestRunResources.BuildWorkerHeadlessService(entity), cancellationToken).ConfigureAwait(false);
        await EnsureExistsAsync(TestRunResources.BuildMasterJob(entity), cancellationToken).ConfigureAwait(false);
        await EnsureExistsAsync(TestRunResources.BuildWorkerStatefulSet(entity), cancellationToken).ConfigureAwait(false);

        V1Job? job = await client.GetAsync<V1Job>(TestRunResources.MasterServiceName(entity), entity.Metadata.NamespaceProperty, cancellationToken)
            .ConfigureAwait(false);

        if (entity.Spec.Autoscaling is not null)
        {
            await ReconcileAutoscalingAsync(entity, job, cancellationToken).ConfigureAwait(false);
        }

        TimeSpan? requeueAfter = await UpdatePhaseAsync(entity, job, cancellationToken).ConfigureAwait(false);
        return ReconciliationResult<V1TestRun>.Success(entity, requeueAfter);
    }

    public Task<ReconciliationResult<V1TestRun>> DeletedAsync(V1TestRun entity, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("TestRun {Name} supprimée : le garbage collection natif nettoie ses ressources filles.", entity.Metadata.Name);
        }

        return Task.FromResult(ReconciliationResult<V1TestRun>.Success(entity));
    }

    private async Task EnsureExistsAsync<TResource>(TResource desired, CancellationToken cancellationToken)
        where TResource : class, IKubernetesObject<V1ObjectMeta>
    {
        TResource? existing = await client.GetAsync<TResource>(desired.Metadata.Name, desired.Metadata.NamespaceProperty, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            await client.CreateAsync(desired, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Retourne le délai de resondage, ou <see langword="null"/> si le tir est terminé.</summary>
    private async Task<TimeSpan?> UpdatePhaseAsync(V1TestRun entity, V1Job? job, CancellationToken cancellationToken)
    {
        bool succeeded = job?.Status?.Succeeded is > 0;
        bool failed = job?.Status?.Failed is > 0;

        if (!succeeded && !failed)
        {
            entity.Status.Phase = job is null ? V1TestRun.TestRunStatus.PHASE_PENDING : V1TestRun.TestRunStatus.PHASE_RUNNING;
            await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);
            return _pollInterval;
        }

        entity.Status.Phase = succeeded ? V1TestRun.TestRunStatus.PHASE_SUCCEEDED : V1TestRun.TestRunStatus.PHASE_FAILED;
        entity.Status.CompletionTime = job!.Status.CompletionTime ?? DateTime.UtcNow;
        await client.UpdateStatusAsync(entity, cancellationToken).ConfigureAwait(false);

        await ScaleWorkersToZeroAsync(entity, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Ajuste le <c>StatefulSet</c> des workers au fil des paliers du profil, en avance de
    /// <see cref="V1TestRun.AutoscalingSpec.ScaleAheadSeconds"/> sur chaque transition — à la
    /// hausse comme à la baisse. Chemin entièrement séparé de <see cref="UpdatePhaseAsync"/> et
    /// jamais appelé si <see cref="V1TestRun.TestRunSpec.Autoscaling"/> est absent : le chemin
    /// existant (dimensionnement fixe, <see cref="ScaleWorkersToZeroAsync"/> en fin de tir) n'est
    /// alors touché en rien.
    /// </summary>
    private async Task ReconcileAutoscalingAsync(V1TestRun entity, V1Job? job, CancellationToken cancellationToken)
    {
        DateTime? startTime = job?.Status?.StartTime;
        bool finished = job?.Status?.Succeeded is > 0 || job?.Status?.Failed is > 0;

        if (startTime is null || finished)
        {
            // Pas encore demarre (rien a ajuster avant le premier palier) ou deja termine
            // (ScaleWorkersToZeroAsync prend le relais) : les deux cas laissent le StatefulSet
            // inchange ici.
            return;
        }

        int[] stageWorkerCounts = TestRunResources.ComputeStageWorkerCounts(entity);
        TimeSpan elapsed = DateTime.UtcNow - startTime.Value;
        TimeSpan scaleAhead = TimeSpan.FromSeconds(entity.Spec.Autoscaling!.ScaleAheadSeconds);

        int desiredStageIndex = 0;
        TimeSpan stageStart = TimeSpan.Zero;
        for (int i = 0; i < entity.Spec.Profile.Count; i++)
        {
            if (stageStart - scaleAhead <= elapsed)
            {
                desiredStageIndex = i;
            }

            stageStart += TimeSpan.FromSeconds(entity.Spec.Profile[i].DurationSeconds);
        }

        int desiredReplicas = stageWorkerCounts[desiredStageIndex];

        V1StatefulSet? workers = await client
            .GetAsync<V1StatefulSet>(TestRunResources.WorkerServiceName(entity), entity.Metadata.NamespaceProperty, cancellationToken)
            .ConfigureAwait(false);

        if (workers is null || workers.Spec.Replicas == desiredReplicas)
        {
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "TestRun {Name} : ajustement du StatefulSet {StatefulSet} de {Current} a {Desired} replique(s) (palier {Stage}).",
                entity.Metadata.Name,
                workers.Metadata.Name,
                workers.Spec.Replicas,
                desiredReplicas,
                desiredStageIndex);
        }

        workers.Spec.Replicas = desiredReplicas;
        await client.UpdateAsync(workers, cancellationToken).ConfigureAwait(false);
    }

    private async Task ScaleWorkersToZeroAsync(V1TestRun entity, CancellationToken cancellationToken)
    {
        V1StatefulSet? workers = await client
            .GetAsync<V1StatefulSet>(TestRunResources.WorkerServiceName(entity), entity.Metadata.NamespaceProperty, cancellationToken)
            .ConfigureAwait(false);

        if (workers is null || workers.Spec.Replicas == 0)
        {
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "TestRun {Name} terminée : reduction du StatefulSet {StatefulSet} a 0 replique.",
                entity.Metadata.Name,
                workers.Metadata.Name);
        }

        workers.Spec.Replicas = 0;
        await client.UpdateAsync(workers, cancellationToken).ConfigureAwait(false);
    }
}
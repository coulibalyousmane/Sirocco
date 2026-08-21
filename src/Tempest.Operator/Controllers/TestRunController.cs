using k8s;
using k8s.Models;
using KubeOps.Abstractions.Rbac;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using Tempest.Operator.Entities;

namespace Tempest.Operator.Controllers;

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
[EntityRbac(typeof(V1TestRun), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Job), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1StatefulSet), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Get | RbacVerb.List | RbacVerb.Watch)]
public sealed class TestRunController(IKubernetesClient client, ILogger<TestRunController> logger) : IEntityController<V1TestRun>
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public async Task<ReconciliationResult<V1TestRun>> ReconcileAsync(V1TestRun entity, CancellationToken cancellationToken)
    {
        await EnsureExistsAsync(TestRunResources.BuildMasterService(entity), cancellationToken).ConfigureAwait(false);
        await EnsureExistsAsync(TestRunResources.BuildWorkerHeadlessService(entity), cancellationToken).ConfigureAwait(false);
        await EnsureExistsAsync(TestRunResources.BuildMasterJob(entity), cancellationToken).ConfigureAwait(false);
        await EnsureExistsAsync(TestRunResources.BuildWorkerStatefulSet(entity), cancellationToken).ConfigureAwait(false);

        V1Job? job = await client.GetAsync<V1Job>(TestRunResources.MasterServiceName(entity), entity.Metadata.NamespaceProperty, cancellationToken)
            .ConfigureAwait(false);

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
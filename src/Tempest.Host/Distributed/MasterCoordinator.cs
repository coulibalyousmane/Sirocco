using Tempest.Domain.Metrics;

namespace Tempest.Host.Distributed;

/// <summary>
/// Etat du maitre : workers enregistres, rapports collectes, verdict final une fois le tir
/// distribue termine.
/// <para>
/// Auto-enregistrement dynamique : le maitre ne connait a l'avance ni l'identite ni le nombre
/// exact de workers, seulement combien il en <i>attend</i> (<see cref="MasterOptions.ExpectedWorkers"/>).
/// </para>
/// </summary>
public sealed class MasterCoordinator
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _registeredWorkers = new(StringComparer.Ordinal);
    private readonly List<WorkerReport> _reports = [];

    private TaskCompletionSource? _registrationTarget;
    private int _registrationGoal;

    private TaskCompletionSource? _reportsTarget;
    private int _reportsGoal;

    /// <summary>Rapport fusionne final, disponible une fois tous les workers rentres.</summary>
    public LoadTestReport? FinalReport { get; set; }

    /// <summary>Verdict des seuils sur le rapport fusionne.</summary>
    public ThresholdReport? FinalThresholds { get; set; }

    /// <summary>
    /// Dernier rapport combine obtenu par sondage des workers pendant le tir — approximatif
    /// (intervalle de sondage), rafraichi en continu, contrairement a <see cref="FinalReport"/>
    /// qui n'est construit qu'une seule fois, a partir des rapports pousses par les workers.
    /// </summary>
    public LoadTestReport? LiveReport { get; set; }

    /// <summary>Enregistre un worker. Idempotent : un doublon n'est compte qu'une fois.</summary>
    public void Register(string workerUrl)
    {
        lock (_gate)
        {
            _registeredWorkers.Add(workerUrl);

            if (_registrationTarget is not null && _registeredWorkers.Count >= _registrationGoal)
            {
                _registrationTarget.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Attend que <paramref name="expected"/> workers se soient enregistres, ou l'expiration
    /// de <paramref name="timeout"/> — au premier des deux. Renvoie les workers enregistres a
    /// cet instant, quel qu'en soit le nombre.
    /// </summary>
    public async Task<IReadOnlyList<string>> WaitForRegistrationsAsync(int expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _registrationGoal = expected;
            _registrationTarget = tcs;

            if (_registeredWorkers.Count >= expected)
            {
                tcs.TrySetResult();
            }
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await tcs.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Fenetre d'enregistrement expiree : on distribue le tir aux workers deja la.
        }

        lock (_gate)
        {
            return [.. _registeredWorkers];
        }
    }

    /// <summary>Enregistre le rapport pousse par un worker a la fin de son tir local.</summary>
    public void SubmitReport(WorkerReport report)
    {
        lock (_gate)
        {
            _reports.Add(report);

            if (_reportsTarget is not null && _reports.Count >= _reportsGoal)
            {
                _reportsTarget.TrySetResult();
            }
        }
    }

    /// <summary>Attend que les <paramref name="expected"/> workers dispatches aient tous rapporte.</summary>
    public async Task<IReadOnlyList<WorkerReport>> WaitForReportsAsync(int expected, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _reportsGoal = expected;
            _reportsTarget = tcs;

            if (_reports.Count >= expected)
            {
                tcs.TrySetResult();
            }
        }

        await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            return [.. _reports];
        }
    }
}
using Tempest.Domain.Metrics;
using Tempest.Host.Distributed;

namespace Tempest.UnitTests.Distributed;

public sealed class MasterCoordinatorTests
{
    private static WorkerReport Report(string workerId) => new(workerId, MetricsDropped: 0L, []);

    [Fact]
    public async Task WaitForReportsAsync_completes_once_all_expected_reports_are_submitted()
    {
        MasterCoordinator coordinator = new();
        coordinator.Register("worker-a");
        coordinator.Register("worker-b");

        Task<IReadOnlyList<WorkerReport>> waiting = coordinator.WaitForReportsAsync(2, CancellationToken.None);

        coordinator.SubmitReport(Report("worker-a"));
        coordinator.SubmitReport(Report("worker-b"));

        IReadOnlyList<WorkerReport> reports = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reports.Count);
    }

    [Fact]
    public async Task WaitForReportsAsync_throws_if_cancelled_before_the_goal_is_met()
    {
        MasterCoordinator coordinator = new();
        coordinator.Register("worker-a");
        using CancellationTokenSource cts = new();

        Task<IReadOnlyList<WorkerReport>> waiting = coordinator.WaitForReportsAsync(1, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => waiting);
    }

    [Fact]
    public void MarkDeadIfStale_does_not_declare_a_recently_registered_worker_dead()
    {
        MasterCoordinator coordinator = new();
        coordinator.Register("worker-a");

        IReadOnlyList<string> newlyDead = coordinator.MarkDeadIfStale(TimeSpan.FromSeconds(60));

        Assert.Empty(newlyDead);
        Assert.Empty(coordinator.DeadWorkers);
    }

    /// <summary>
    /// Le cas central de ce chantier : un worker dispatche qui ne rapporte jamais ne doit pas
    /// bloquer indefiniment <see cref="MasterCoordinator.WaitForReportsAsync"/> — le declarer mort
    /// doit suffire a faire avancer l'attente, meme sans son rapport.
    /// </summary>
    [Fact]
    public async Task MarkDeadIfStale_lets_a_pending_wait_complete_with_fewer_reports_than_expected()
    {
        MasterCoordinator coordinator = new();
        coordinator.Register("worker-a");
        coordinator.Register("worker-b");

        Task<IReadOnlyList<WorkerReport>> waiting = coordinator.WaitForReportsAsync(2, CancellationToken.None);
        coordinator.SubmitReport(Report("worker-a"));

        await Task.Delay(TimeSpan.FromMilliseconds(20));
        IReadOnlyList<string> newlyDead = coordinator.MarkDeadIfStale(TimeSpan.FromMilliseconds(1));

        Assert.Equal(["worker-b"], newlyDead);

        IReadOnlyList<WorkerReport> reports = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["worker-a"], [.. reports.Select(report => report.WorkerId)]);
    }

    [Fact]
    public async Task MarkDeadIfStale_ignores_a_worker_that_already_reported()
    {
        MasterCoordinator coordinator = new();
        coordinator.Register("worker-a");
        coordinator.SubmitReport(Report("worker-a"));

        await Task.Delay(TimeSpan.FromMilliseconds(20));
        IReadOnlyList<string> newlyDead = coordinator.MarkDeadIfStale(TimeSpan.FromMilliseconds(1));

        Assert.Empty(newlyDead);
        Assert.Empty(coordinator.DeadWorkers);
    }

    [Fact]
    public async Task Heartbeat_reverses_a_dead_marking()
    {
        MasterCoordinator coordinator = new();
        coordinator.Register("worker-a");

        await Task.Delay(TimeSpan.FromMilliseconds(20));
        coordinator.MarkDeadIfStale(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["worker-a"], coordinator.DeadWorkers);

        coordinator.Heartbeat("worker-a");

        Assert.Empty(coordinator.DeadWorkers);
    }

    [Fact]
    public async Task A_late_report_from_a_worker_already_marked_dead_is_still_recorded()
    {
        MasterCoordinator coordinator = new();
        coordinator.Register("worker-a");

        await Task.Delay(TimeSpan.FromMilliseconds(20));
        coordinator.MarkDeadIfStale(TimeSpan.FromMilliseconds(1));

        coordinator.SubmitReport(Report("worker-a"));

        Assert.Equal(["worker-a"], [.. coordinator.ReportsSoFar.Select(report => report.WorkerId)]);
    }
}
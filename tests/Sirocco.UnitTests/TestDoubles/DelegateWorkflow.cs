using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;

namespace Sirocco.UnitTests.TestDoubles;

/// <summary>
/// Scenario de test pilote par un delegue, declarant une unique etape.
/// Le delegue recoit le scenario lui-meme afin de pouvoir lire <see cref="Step"/>,
/// qui n'est connu qu'apres <see cref="RegisterSteps"/>.
/// </summary>
internal sealed class DelegateWorkflow(
    Func<DelegateWorkflow, IVirtualUserContext, CancellationToken, ValueTask> execute,
    string stepName = DelegateWorkflow.DEFAULT_STEP_NAME) : IWorkflow
{
    public const string DEFAULT_STEP_NAME = "step";

    private readonly Func<DelegateWorkflow, IVirtualUserContext, CancellationToken, ValueTask> _execute = execute;

    public string Name => "delegate-workflow";

    public string StepName { get; } = stepName;

    public StepId Step { get; private set; } = StepId.None;

    public int SetUpCalls { get; private set; }

    public int TearDownCalls { get; private set; }

    public void RegisterSteps(StepRegistry registry) => Step = registry.Register(StepName);

    public ValueTask SetUpAsync(CancellationToken cancellationToken)
    {
        SetUpCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask TearDownAsync(CancellationToken cancellationToken)
    {
        TearDownCalls++;
        return ValueTask.CompletedTask;
    }

    public ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken) =>
        _execute(this, context, cancellationToken);

    /// <summary>Scenario qui ne fait rien : mesure le cout propre du moteur.</summary>
    public static DelegateWorkflow NoOp() =>
        new(static (_, _, _) => ValueTask.CompletedTask);

    /// <summary>Scenario minimal declarant une etape reussie, sans I/O.</summary>
    public static DelegateWorkflow SingleSuccessfulStep() =>
        new(static (self, context, _) =>
        {
            context.BeginStep(self.Step).Success();
            return ValueTask.CompletedTask;
        });

    /// <summary>Scenario qui occupe son utilisateur virtuel pendant une duree fixe.</summary>
    public static DelegateWorkflow Slow(TimeSpan duration) =>
        new(async (self, context, cancellationToken) =>
        {
            var scope = context.BeginStep(self.Step);
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            scope.Success();
        });

    /// <summary>Scenario systematiquement en erreur.</summary>
    public static DelegateWorkflow AlwaysThrows(string message = "boom") =>
        new((_, _, _) => throw new InvalidOperationException(message));
}
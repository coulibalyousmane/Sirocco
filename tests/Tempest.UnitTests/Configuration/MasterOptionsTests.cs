using Tempest.Host.Configuration;

namespace Tempest.UnitTests.Configuration;

public sealed class MasterOptionsTests
{
    [Fact]
    public void Validate_requires_at_least_one_expected_worker_when_there_is_no_stage_plan()
    {
        MasterOptions options = new() { ExpectedWorkers = 0 };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// L'operateur n'emet jamais Master__ExpectedWorkers pour une TestRun avec Autoscaling
    /// (voir TestRunResources.BuildMasterEnv) : la validation ne doit donc pas l'exiger quand un
    /// plan de paliers est present, sous peine de faire planter le maitre au demarrage avant
    /// meme d'atteindre ExecuteAdaptiveAsync — trouvaille reelle en verifiant sur un vrai cluster.
    /// </summary>
    [Fact]
    public void Validate_does_not_require_expected_workers_when_a_stage_plan_is_present()
    {
        MasterOptions options = new() { ExpectedWorkers = 0, StagePlannedWorkers = [1, 4] };

        options.Validate();
    }

    [Fact]
    public void Validate_still_rejects_a_stage_plan_with_a_value_below_one()
    {
        MasterOptions options = new() { ExpectedWorkers = 0, StagePlannedWorkers = [1, 0] };

        Assert.Throws<ArgumentException>(options.Validate);
    }
}
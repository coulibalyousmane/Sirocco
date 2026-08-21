using k8s;
using k8s.Models;
using Tempest.Operator;
using Tempest.Operator.Entities;

namespace Tempest.UnitTests.Operator;

public sealed class TestRunResourcesTests
{
    [Fact]
    public void The_worker_headless_service_has_no_cluster_ip()
    {
        V1TestRun testRun = CreateTestRun();

        V1Service service = TestRunResources.BuildWorkerHeadlessService(testRun);

        Assert.Equal("None", service.Spec.ClusterIP);
    }

    [Fact]
    public void The_worker_stateful_set_replica_count_matches_the_spec()
    {
        V1TestRun testRun = CreateTestRun(workerReplicas: 3);

        V1StatefulSet statefulSet = TestRunResources.BuildWorkerStatefulSet(testRun);

        Assert.Equal(3, statefulSet.Spec.Replicas);
    }

    [Fact]
    public void The_master_job_never_retries()
    {
        V1TestRun testRun = CreateTestRun();

        V1Job job = TestRunResources.BuildMasterJob(testRun);

        Assert.Equal(0, job.Spec.BackoffLimit);
        Assert.Equal("Never", job.Spec.Template.Spec.RestartPolicy);
    }

    [Fact]
    public void Every_child_resource_is_owned_by_the_test_run()
    {
        V1TestRun testRun = CreateTestRun();

        V1Service masterService = TestRunResources.BuildMasterService(testRun);
        V1Service workerService = TestRunResources.BuildWorkerHeadlessService(testRun);
        V1Job masterJob = TestRunResources.BuildMasterJob(testRun);
        V1StatefulSet workerStatefulSet = TestRunResources.BuildWorkerStatefulSet(testRun);

        foreach (IKubernetesObject<V1ObjectMeta> child in new IKubernetesObject<V1ObjectMeta>[] { masterService, workerService, masterJob, workerStatefulSet })
        {
            V1OwnerReference owner = Assert.Single(child.Metadata.OwnerReferences);
            Assert.Equal(testRun.Metadata.Name, owner.Name);
            Assert.Equal(testRun.Metadata.Uid, owner.Uid);
            Assert.True(owner.Controller);
        }
    }

    [Fact]
    public void The_worker_self_url_is_computed_from_the_pod_s_own_name()
    {
        V1TestRun testRun = CreateTestRun();

        V1StatefulSet statefulSet = TestRunResources.BuildWorkerStatefulSet(testRun);
        IList<V1EnvVar> env = statefulSet.Spec.Template.Spec.Containers[0].Env;

        V1EnvVar podName = Assert.Single(env, e => e.Name == "POD_NAME");
        Assert.Equal("metadata.name", podName.ValueFrom.FieldRef.FieldPath);

        V1EnvVar selfUrl = Assert.Single(env, e => e.Name == "Worker__SelfUrl");
        Assert.Equal($"http://$(POD_NAME).{TestRunResources.WorkerServiceName(testRun)}:{TestRunResources.WORKER_PORT}", selfUrl.Value);
    }

    [Fact]
    public void The_worker_master_url_points_at_the_master_service()
    {
        V1TestRun testRun = CreateTestRun();

        V1StatefulSet statefulSet = TestRunResources.BuildWorkerStatefulSet(testRun);
        IList<V1EnvVar> env = statefulSet.Spec.Template.Spec.Containers[0].Env;

        V1EnvVar masterUrl = Assert.Single(env, e => e.Name == "Worker__MasterUrl");
        Assert.Equal($"http://{TestRunResources.MasterServiceName(testRun)}:{TestRunResources.MASTER_PORT}", masterUrl.Value);
    }

    [Fact]
    public void Master_expected_workers_matches_the_worker_replica_count()
    {
        V1TestRun testRun = CreateTestRun(workerReplicas: 4);

        V1Job job = TestRunResources.BuildMasterJob(testRun);
        IList<V1EnvVar> env = job.Spec.Template.Spec.Containers[0].Env;

        V1EnvVar expectedWorkers = Assert.Single(env, e => e.Name == "Master__ExpectedWorkers");
        Assert.Equal("4", expectedWorkers.Value);
    }

    [Fact]
    public void The_shared_secret_is_mounted_from_a_secret_reference_never_as_a_literal_value()
    {
        V1TestRun testRun = CreateTestRun();
        testRun.Spec.ClusterSharedSecretRef = new V1SecretKeySelector { Name = "tempest-secret", Key = "shared-secret" };

        V1Job job = TestRunResources.BuildMasterJob(testRun);
        IList<V1EnvVar> env = job.Spec.Template.Spec.Containers[0].Env;

        V1EnvVar secretEnv = Assert.Single(env, e => e.Name == "Tempest__ClusterSharedSecret");
        Assert.Null(secretEnv.Value);
        Assert.Equal("tempest-secret", secretEnv.ValueFrom.SecretKeyRef.Name);
        Assert.Equal("shared-secret", secretEnv.ValueFrom.SecretKeyRef.Key);
    }

    [Fact]
    public void Profile_stages_are_mapped_in_order()
    {
        V1TestRun testRun = CreateTestRun();
        testRun.Spec.Profile =
        [
            new V1TestRun.ProfileStage { FromRps = 0, ToRps = 10, DurationSeconds = 10 },
            new V1TestRun.ProfileStage { FromRps = 10, ToRps = 0, DurationSeconds = 5 },
        ];

        V1Job job = TestRunResources.BuildMasterJob(testRun);
        IList<V1EnvVar> env = job.Spec.Template.Spec.Containers[0].Env;

        Assert.Equal("0", Assert.Single(env, e => e.Name == "Tempest__Profile__0__FromRps").Value);
        Assert.Equal("10", Assert.Single(env, e => e.Name == "Tempest__Profile__0__ToRps").Value);
        Assert.Equal("10", Assert.Single(env, e => e.Name == "Tempest__Profile__0__DurationSeconds").Value);
        Assert.Equal("10", Assert.Single(env, e => e.Name == "Tempest__Profile__1__FromRps").Value);
        Assert.Equal("0", Assert.Single(env, e => e.Name == "Tempest__Profile__1__ToRps").Value);
        Assert.Equal("5", Assert.Single(env, e => e.Name == "Tempest__Profile__1__DurationSeconds").Value);
    }

    [Fact]
    public void Stage_worker_counts_round_up_and_take_the_peak_of_each_stage()
    {
        V1TestRun testRun = CreateTestRun();
        testRun.Spec.Autoscaling = new V1TestRun.AutoscalingSpec { MaxRequestsPerSecondPerWorker = 5, MinWorkerReplicas = 1, MaxWorkerReplicas = 10 };
        testRun.Spec.Profile =
        [
            new V1TestRun.ProfileStage { FromRps = 0, ToRps = 5, DurationSeconds = 10 },
            new V1TestRun.ProfileStage { FromRps = 5, ToRps = 21, DurationSeconds = 15 },
            new V1TestRun.ProfileStage { FromRps = 20, ToRps = 5, DurationSeconds = 10 },
        ];

        int[] stageWorkerCounts = TestRunResources.ComputeStageWorkerCounts(testRun);

        Assert.Equal([1, 5, 4], stageWorkerCounts);
    }

    [Fact]
    public void Stage_worker_counts_are_clamped_between_min_and_max()
    {
        V1TestRun testRun = CreateTestRun();
        testRun.Spec.Autoscaling = new V1TestRun.AutoscalingSpec { MaxRequestsPerSecondPerWorker = 5, MinWorkerReplicas = 2, MaxWorkerReplicas = 3 };
        testRun.Spec.Profile =
        [
            new V1TestRun.ProfileStage { FromRps = 0, ToRps = 1, DurationSeconds = 10 },
            new V1TestRun.ProfileStage { FromRps = 0, ToRps = 100, DurationSeconds = 10 },
        ];

        int[] stageWorkerCounts = TestRunResources.ComputeStageWorkerCounts(testRun);

        Assert.Equal([2, 3], stageWorkerCounts);
    }

    [Fact]
    public void The_worker_stateful_set_starts_at_the_first_stage_s_worker_count_when_autoscaling_is_set()
    {
        V1TestRun testRun = CreateTestRun(workerReplicas: 99);
        testRun.Spec.Autoscaling = new V1TestRun.AutoscalingSpec { MaxRequestsPerSecondPerWorker = 5, MinWorkerReplicas = 1, MaxWorkerReplicas = 10 };
        testRun.Spec.Profile =
        [
            new V1TestRun.ProfileStage { FromRps = 0, ToRps = 5, DurationSeconds = 10 },
            new V1TestRun.ProfileStage { FromRps = 5, ToRps = 20, DurationSeconds = 10 },
        ];

        V1StatefulSet statefulSet = TestRunResources.BuildWorkerStatefulSet(testRun);

        // Le pic global (20 req/s => 4 workers) est ignore au demarrage : seul le premier palier
        // (1 worker) compte, c'est le geste "live" plutot qu'un dimensionnement statique pour le
        // pic. workerReplicas (99) est egalement ignore : Autoscaling prime.
        Assert.Equal(1, statefulSet.Spec.Replicas);
    }

    [Fact]
    public void Master_receives_a_stage_planned_workers_env_var_per_stage_when_autoscaling_is_set()
    {
        V1TestRun testRun = CreateTestRun();
        testRun.Spec.Autoscaling = new V1TestRun.AutoscalingSpec { MaxRequestsPerSecondPerWorker = 5, MinWorkerReplicas = 1, MaxWorkerReplicas = 10 };
        testRun.Spec.Profile =
        [
            new V1TestRun.ProfileStage { FromRps = 0, ToRps = 5, DurationSeconds = 10 },
            new V1TestRun.ProfileStage { FromRps = 5, ToRps = 20, DurationSeconds = 10 },
        ];

        V1Job job = TestRunResources.BuildMasterJob(testRun);
        IList<V1EnvVar> env = job.Spec.Template.Spec.Containers[0].Env;

        Assert.Equal("1", Assert.Single(env, e => e.Name == "Master__StagePlannedWorkers__0").Value);
        Assert.Equal("4", Assert.Single(env, e => e.Name == "Master__StagePlannedWorkers__1").Value);
        Assert.DoesNotContain(env, e => e.Name == "Master__ExpectedWorkers");
    }

    [Fact]
    public void Master_expected_workers_is_used_instead_of_a_stage_plan_when_autoscaling_is_absent()
    {
        V1TestRun testRun = CreateTestRun(workerReplicas: 3);

        V1Job job = TestRunResources.BuildMasterJob(testRun);
        IList<V1EnvVar> env = job.Spec.Template.Spec.Containers[0].Env;

        Assert.Equal("3", Assert.Single(env, e => e.Name == "Master__ExpectedWorkers").Value);
        Assert.DoesNotContain(env, e => e.Name.StartsWith("Master__StagePlannedWorkers__", StringComparison.Ordinal));
    }

    private static V1TestRun CreateTestRun(int workerReplicas = 2) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = "demo",
            NamespaceProperty = "default",
            Uid = "11111111-1111-1111-1111-111111111111",
        },
        Spec = new V1TestRun.TestRunSpec
        {
            Image = "tempest-host:local",
            TargetBaseUrl = "http://sampletarget:5281",
            WorkerReplicas = workerReplicas,
        },
    };
}
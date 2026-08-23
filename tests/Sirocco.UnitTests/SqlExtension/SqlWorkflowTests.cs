using Microsoft.Data.Sqlite;
using Sirocco.Application.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Extensions.Sql;
using Sirocco.UnitTests.TestDoubles;

namespace Sirocco.UnitTests.SqlExtension;

/// <summary>
/// Verifie <see cref="SqlWorkflow"/> contre une vraie base SQLite temporaire — le protocole de
/// reference SQL de la roadmap phase 6 n'a de sens teste que contre un vrai moteur de base de
/// donnees, pas un double.
/// </summary>
public sealed class SqlWorkflowTests
{
    private const string CONNECTION_STRING_ENVIRONMENT_VARIABLE = "SIROCCO_SQL_PLUGIN_CONNECTION_STRING";
    private const string ROW_COUNT_ENVIRONMENT_VARIABLE = "SIROCCO_SQL_PLUGIN_ROW_COUNT";

    [Fact]
    public void RegisterSteps_declares_exactly_the_two_named_steps()
    {
        StepRegistry registry = new();
        new SqlWorkflow().RegisterSteps(registry);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetId("SQL SELECT product", out _));
        Assert.True(registry.TryGetId("SQL INSERT order", out _));
    }

    [Fact]
    public async Task SetUpAsync_seeds_exactly_the_configured_row_count()
    {
        (string databasePath, string connectionString) = CreateTempDatabase();
        try
        {
            SqlWorkflow workflow = CreateWorkflow(connectionString, rowCount: 5);

            await workflow.SetUpAsync(CancellationToken.None);

            using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM products;";
            long rowCount = (long)(await count.ExecuteScalarAsync())!;

            Assert.Equal(5, rowCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_completes_both_steps_successfully_and_persists_the_order()
    {
        (string databasePath, string connectionString) = CreateTempDatabase();
        try
        {
            SqlWorkflow workflow = CreateWorkflow(connectionString, rowCount: 10);
            await workflow.SetUpAsync(CancellationToken.None);

            (VirtualUserContext context, CollectingMetricSink sink, StepId iterationStep) = CreateHarness(workflow);

            await RunIterationAsync(workflow, context);

            // 3 mesures publiees : l'etape technique __iteration (EndIteration) plus les deux
            // etapes du scenario — seules ces deux dernieres sont l'objet de ce test.
            Assert.Equal(3, sink.Results.Count);
            Assert.All(
                sink.Results.Where(result => result.Step != iterationStep),
                static result => Assert.Equal(RequestOutcome.Success, result.Outcome));

            using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM orders;";
            long orderCount = (long)(await count.ExecuteScalarAsync())!;

            Assert.Equal(1, orderCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task A_product_id_outside_the_seeded_range_fails_the_select_step_as_an_assertion()
    {
        (string databasePath, string connectionString) = CreateTempDatabase();
        try
        {
            // Un seul produit seme (id 1) : forcer la selection d'un id hors plage reproduit le
            // cas "l'assertion metier echoue" sans devoir truquer la requete elle-meme.
            SqlWorkflow workflow = CreateWorkflow(connectionString, rowCount: 1);
            await workflow.SetUpAsync(CancellationToken.None);

            using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync();
            using SqliteCommand delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM products WHERE id = 1;";
            await delete.ExecuteNonQueryAsync();

            (VirtualUserContext context, CollectingMetricSink sink, _) = CreateHarness(workflow);

            await RunIterationAsync(workflow, context);

            MetricResult selectResult = sink.Results.First();
            Assert.Equal(RequestOutcome.AssertionFailed, selectResult.Outcome);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static SqlWorkflow CreateWorkflow(string connectionString, int rowCount)
    {
        Environment.SetEnvironmentVariable(CONNECTION_STRING_ENVIRONMENT_VARIABLE, connectionString);
        Environment.SetEnvironmentVariable(ROW_COUNT_ENVIRONMENT_VARIABLE, rowCount.ToString());
        try
        {
            return new SqlWorkflow();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CONNECTION_STRING_ENVIRONMENT_VARIABLE, null);
            Environment.SetEnvironmentVariable(ROW_COUNT_ENVIRONMENT_VARIABLE, null);
        }
    }

    private static (string DatabasePath, string ConnectionString) CreateTempDatabase()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sirocco-sql-plugin-test-{Guid.NewGuid():N}.db");
        return (path, $"Data Source={path}");
    }

    private static void DeleteDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (string candidate in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    /// <summary>Meme reproduction du cycle de vie que <c>VirtualUserWorker</c>, voir DynamicCheckoutWorkflowTests.</summary>
    private static async Task RunIterationAsync(SqlWorkflow workflow, VirtualUserContext context)
    {
        ExecutionToken token = new(IterationIndex: 0, ScheduledTicks: 0L);
        context.BeginIteration(in token, startedTicks: 0L, CancellationToken.None);
        await workflow.ExecuteAsync(context, CancellationToken.None).ConfigureAwait(false);
        context.EndIteration(startedTicks: 0L, RequestOutcome.Success);
    }

    private static (VirtualUserContext Context, CollectingMetricSink Sink, StepId IterationStep) CreateHarness(SqlWorkflow workflow)
    {
        // HttpClient jamais utilise par SqlWorkflow : place tenante requise par VirtualUserContext.
        HttpClient client = new() { BaseAddress = new Uri("https://unused.example") };

        StepRegistry registry = new();
        StepId iterationStep = registry.Register(WellKnownSteps.ITERATION);
        workflow.RegisterSteps(registry);
        registry.Seal();

        CollectingMetricSink sink = new();
        VirtualUserContext context = new(virtualUserId: 0, client, sink, iterationStep);

        return (context, sink, iterationStep);
    }
}
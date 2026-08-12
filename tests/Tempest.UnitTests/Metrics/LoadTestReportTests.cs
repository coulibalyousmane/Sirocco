using Tempest.Domain.Metrics;

namespace Tempest.UnitTests.Metrics;

public sealed class LoadTestReportTests
{
    private static StepStatistics CreateStep(string name, long count, long p95Microseconds, long errorCount = 0L)
    {
        LatencySnapshot latency = new(
            Count: count,
            MinMicroseconds: 1_000L,
            MaxMicroseconds: p95Microseconds * 2,
            MeanMicroseconds: p95Microseconds,
            P50Microseconds: p95Microseconds / 2,
            P75Microseconds: (long)(p95Microseconds * 0.8),
            P90Microseconds: (long)(p95Microseconds * 0.9),
            P95Microseconds: p95Microseconds,
            P99Microseconds: (long)(p95Microseconds * 1.1),
            P999Microseconds: (long)(p95Microseconds * 1.2));

        return new StepStatistics
        {
            Name = name,
            Step = new StepId(0),
            Count = count,
            SuccessCount = count - errorCount,
            DroppedCount = 0L,
            CountByOutcome = [count - errorCount, errorCount],
            BytesReceived = 1_234L,
            MaxSchedulingDelayMicroseconds = 500L,
            Response = latency,
            Service = latency,
            ResponseHistogram = HistogramSnapshot.Empty,
        };
    }

    private static LoadTestReport CreateReport(IReadOnlyList<StepStatistics> steps, long metricsDropped = 0L)
    {
        StepStatistics iteration = steps.Count > 0 ? steps[0] : StepStatistics.Empty(StepId.None, WellKnownSteps.ITERATION);

        return new LoadTestReport
        {
            Scope = StatisticsScope.Cumulative,
            Duration = TimeSpan.FromSeconds(20),
            Steps = steps,
            Iteration = iteration,
            MetricsDropped = metricsDropped,
        };
    }

    [Fact]
    public void ToHtml_contains_step_names_and_percentiles()
    {
        LoadTestReport report = CreateReport([CreateStep("login", count: 100L, p95Microseconds: 45_000L)]);

        string html = report.ToHtml();

        Assert.Contains("login", html);
        Assert.Contains("45.00 ms", html);
        Assert.Contains("100", html);
    }

    [Fact]
    public void ToHtml_is_a_well_formed_standalone_document()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);

        string html = report.ToHtml();

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<html", html);
        Assert.Contains("</html>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_handles_a_report_with_no_steps()
    {
        LoadTestReport report = CreateReport([]);

        string html = report.ToHtml();

        Assert.Contains("<table>", html);
        Assert.Contains("<tbody>", html);
    }

    [Fact]
    public void ToHtml_warns_when_metrics_were_dropped()
    {
        LoadTestReport untrustworthy = CreateReport([CreateStep("login", 10L, 10_000L)], metricsDropped: 5L);
        LoadTestReport trustworthy = CreateReport([CreateStep("login", 10L, 10_000L)]);

        Assert.Contains("mesures perdues", untrustworthy.ToHtml());
        Assert.DoesNotContain("mesures perdues", trustworthy.ToHtml());
    }

    [Fact]
    public void ToHtml_warns_when_the_closed_model_was_used()
    {
        LoadTestReport closed = CreateReport([CreateStep("login", 10L, 10_000L)]) with { ClosedModel = true };
        LoadTestReport open = CreateReport([CreateStep("login", 10L, 10_000L)]);

        Assert.Contains("Modele ferme", closed.ToHtml());
        Assert.DoesNotContain("Modele ferme", open.ToHtml());
    }

    [Fact]
    public void ToTable_warns_when_the_closed_model_was_used()
    {
        LoadTestReport closed = CreateReport([CreateStep("login", 10L, 10_000L)]) with { ClosedModel = true };
        LoadTestReport open = CreateReport([CreateStep("login", 10L, 10_000L)]);

        Assert.Contains("Modele ferme", closed.ToTable());
        Assert.DoesNotContain("Modele ferme", open.ToTable());
    }

    private static StepStatistics CreateStepWithHistogram(string name, params long[] responseMicroseconds)
    {
        LatencyHistogram histogram = new();
        foreach (long value in responseMicroseconds)
        {
            histogram.Record(value);
        }

        return CreateStep(name, responseMicroseconds.Length, responseMicroseconds.Length > 0 ? responseMicroseconds[^1] : 0L)
            with
        { ResponseHistogram = histogram.Export() };
    }

    [Fact]
    public void ToHtml_renders_a_histogram_section_when_a_step_has_measurements()
    {
        LoadTestReport report = CreateReport([CreateStepWithHistogram("login", 1_000L, 2_000L, 3_000L, 50_000L)]);

        string html = report.ToHtml();

        Assert.Contains("Distribution des temps de reponse", html);
        Assert.Contains("<svg", html);
        Assert.Contains("<rect", html);
    }

    [Fact]
    public void ToHtml_omits_the_histogram_section_when_no_step_has_measurements()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 0L, 0L)]);

        Assert.DoesNotContain("Distribution des temps de reponse", report.ToHtml());
    }

    [Fact]
    public void ToHtml_renders_a_debt_curve_chart_when_at_least_two_samples_exist()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)])
            with
        { TimeSeries = [CreateSample(2d, 50d, 8), CreateSample(4d, 60d, 8)] };

        string html = report.ToHtml();

        Assert.Contains("<polyline", html);
        Assert.Contains("#cf222e", html);
    }

    [Fact]
    public void ToHtml_omits_the_debt_curve_chart_when_there_is_only_one_sample()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)])
            with
        { TimeSeries = [CreateSample(2d, 50d, 8)] };

        Assert.DoesNotContain("<polyline", report.ToHtml());
    }

    [Fact]
    public void ToHtml_adds_a_refresh_meta_tag_when_requested()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);

        string html = report.ToHtml(autoRefreshSeconds: 5);

        Assert.Contains("""<meta http-equiv="refresh" content="5">""", html);
    }

    [Fact]
    public void ToHtml_omits_the_refresh_meta_tag_by_default()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);

        Assert.DoesNotContain("http-equiv=\"refresh\"", report.ToHtml());
    }

    private static TimeSeriesSample CreateSample(double elapsedSeconds, double iterationsPerSecond, int activeVirtualUsers) => new()
    {
        ElapsedSeconds = elapsedSeconds,
        IterationsPerSecond = iterationsPerSecond,
        ActiveVirtualUsers = activeVirtualUsers,
        ErrorRate = 0d,
        ResponseP50Milliseconds = 10d,
        ResponseP95Milliseconds = 20d,
        ResponseP99Milliseconds = 30d,
        MaxSchedulingDelayMilliseconds = 5d,
    };

    [Fact]
    public void ToHtml_renders_a_time_series_section_when_samples_exist()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)])
            with
        { TimeSeries = [CreateSample(2d, 50d, 8)] };

        string html = report.ToHtml();

        Assert.Contains("Serie temporelle", html);
        Assert.Contains("2.0 s", html);
        Assert.Contains("<td>8</td>", html);
    }

    [Fact]
    public void ToHtml_omits_the_time_series_section_when_there_are_no_samples()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);

        Assert.DoesNotContain("Serie temporelle", report.ToHtml());
    }

    [Fact]
    public void ToTable_renders_a_time_series_section_when_samples_exist()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)])
            with
        { TimeSeries = [CreateSample(4d, 12d, 3)] };

        string table = report.ToTable();

        Assert.Contains("serie temporelle", table);
        Assert.Contains("it/s", table);
    }

    [Fact]
    public void ToTable_omits_the_time_series_section_when_there_are_no_samples()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);

        Assert.DoesNotContain("serie temporelle", report.ToTable());
    }

    [Fact]
    public void TimeSeries_is_empty_by_default() =>
        Assert.Empty(CreateReport([CreateStep("login", 10L, 10_000L)]).TimeSeries);

    [Fact]
    public void ClosedModel_is_false_by_default() =>
        Assert.False(CreateReport([CreateStep("login", 10L, 10_000L)]).ClosedModel);

    [Fact]
    public void ToHtml_escapes_step_names_to_prevent_html_injection()
    {
        const string maliciousStepName = "<script>alert(1)</script>";
        LoadTestReport report = CreateReport([CreateStep(maliciousStepName, 1L, 1_000L)]);

        string html = report.ToHtml();

        Assert.DoesNotContain(maliciousStepName, html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(maliciousStepName), html);
    }

    [Fact]
    public void ToHtml_without_thresholds_omits_the_threshold_section()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);

        Assert.DoesNotContain("class=\"verdict", report.ToHtml());
    }

    private static ThresholdRule ErrorRateRuleFor(string stepName, double limit) => new()
    {
        StepName = stepName,
        Metric = ThresholdMetric.ErrorRate,
        Comparison = ThresholdComparison.LessThanOrEqual,
        Limit = limit,
    };

    [Fact]
    public void ToHtml_reports_a_passing_threshold_verdict()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]);
        ThresholdReport thresholds = ThresholdReport.Evaluate([ErrorRateRuleFor("login", limit: 0.5)], report);

        string html = report.ToHtml(thresholds);

        Assert.Contains("tous respectes", html);
        Assert.Contains("class=\"pass\"", html);
    }

    [Fact]
    public void ToHtml_reports_a_failing_threshold_verdict()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L, errorCount: 10L)]);
        ThresholdReport thresholds = ThresholdReport.Evaluate([ErrorRateRuleFor("login", limit: 0.0)], report);

        string html = report.ToHtml(thresholds);

        Assert.Contains("au moins un echec", html);
        Assert.Contains("class=\"fail\"", html);
    }

    [Fact]
    public void No_tags_by_default()
    {
        Assert.Empty(CreateReport([CreateStep("login", 10L, 10_000L)]).Tags);
    }

    [Fact]
    public void ToTable_shows_the_tags_when_present()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]) with
        {
            Tags = new Dictionary<string, string> { ["region"] = "eu-west" },
        };

        Assert.Contains("region=eu-west", report.ToTable());
    }

    [Fact]
    public void ToTable_omits_the_tags_line_when_there_are_none()
    {
        Assert.DoesNotContain("etiquettes", CreateReport([CreateStep("login", 10L, 10_000L)]).ToTable());
    }

    [Fact]
    public void ToHtml_shows_the_tags_when_present()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]) with
        {
            Tags = new Dictionary<string, string> { ["version"] = "v2" },
        };

        Assert.Contains("version=v2", report.ToHtml());
    }

    /// <summary>
    /// Le rapport n'essaie jamais de reinterpreter un nom d'etape qualifie (voir
    /// <see cref="Declarative.HttpStepDefinition.QualifiedName"/>) comme une arborescence a
    /// l'affichage : il l'affiche tel quel, comme n'importe quel autre nom d'etape. Le
    /// regroupement est une convention de nommage, pas une syntaxe interpretee par le rapport —
    /// sinon un nom d'etape ordinaire contenant un '/' (pas rare, ni malveillant : voir le test
    /// d'echappement HTML ci-dessous, dont le nom malicieux contient lui-meme un '/') serait
    /// coupe en deux de facon inattendue.
    /// </summary>
    [Fact]
    public void ToTable_shows_a_qualified_step_name_as_a_plain_flat_name()
    {
        LoadTestReport report = CreateReport([CreateStep("checkout/pay", 10L, 10_000L)]);

        Assert.Contains("checkout/pay", report.ToTable());
    }

    [Fact]
    public void ToHtml_shows_a_qualified_step_name_as_a_plain_flat_name()
    {
        LoadTestReport report = CreateReport([CreateStep("checkout/pay", 10L, 10_000L)]);

        Assert.Contains("checkout/pay", report.ToHtml());
    }

    [Fact]
    public void No_custom_metrics_by_default()
    {
        Assert.Empty(CreateReport([CreateStep("login", 10L, 10_000L)]).CustomMetrics);
    }

    private static CustomMetricSnapshot CounterMetric(string name, double sum) => new()
    {
        Name = name,
        Kind = CustomMetricKind.Counter,
        Count = (long)sum,
        Sum = sum,
        Min = 0d,
        Max = 0d,
        Last = 0d,
    };

    [Fact]
    public void ToTable_omits_the_custom_metrics_section_when_there_are_none()
    {
        Assert.DoesNotContain("metriques personnalisees", CreateReport([CreateStep("login", 10L, 10_000L)]).ToTable());
    }

    /// <summary>
    /// ToTable() formate ses nombres dans la culture courante (lisibilite au terminal), a
    /// l'inverse de ToHtml() qui force l'invariant — voir <see cref="FormatCustomMetricValue"/>.
    /// Les assertions ci-dessous comparent donc au meme format que produirait la culture
    /// courante, plutot que d'exiger un separateur decimal fixe qui romprait des que la culture
    /// de la machine qui execute les tests differe (typiquement en CI).
    /// </summary>
    [Fact]
    public void ToTable_shows_a_counter_as_its_sum()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]) with
        {
            CustomMetrics = [CounterMetric("orders_total", 42d)],
        };

        string table = report.ToTable();
        Assert.Contains("metriques personnalisees", table);
        Assert.Contains("orders_total", table);
        Assert.Contains(42d.ToString("F2"), table);
    }

    [Fact]
    public void ToTable_shows_a_gauge_as_its_last_value()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]) with
        {
            CustomMetrics = [new CustomMetricSnapshot { Name = "active_carts", Kind = CustomMetricKind.Gauge, Count = 3, Sum = 0d, Min = 0d, Max = 0d, Last = 8d }],
        };

        Assert.Contains(8d.ToString("F2"), report.ToTable());
    }

    [Fact]
    public void ToTable_shows_a_rate_as_a_percentage()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]) with
        {
            CustomMetrics = [new CustomMetricSnapshot { Name = "cache_hit_rate", Kind = CustomMetricKind.Rate, Count = 4, Sum = 3d, Min = 0d, Max = 0d, Last = 0d }],
        };

        Assert.Contains(0.75d.ToString("P1"), report.ToTable());
    }

    [Fact]
    public void ToTable_shows_a_trend_as_min_mean_and_max()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]) with
        {
            CustomMetrics = [new CustomMetricSnapshot { Name = "order_value", Kind = CustomMetricKind.Trend, Count = 2, Sum = 60d, Min = 10d, Max = 50d, Last = 0d }],
        };

        string table = report.ToTable();
        Assert.Contains($"min {10d:F2}", table);
        Assert.Contains($"moy {30d:F2}", table);
        Assert.Contains($"max {50d:F2}", table);
    }

    [Fact]
    public void ToHtml_shows_the_custom_metrics_section()
    {
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]) with
        {
            CustomMetrics = [CounterMetric("orders_total", 42d)],
        };

        string html = report.ToHtml();
        Assert.Contains("Metriques personnalisees", html);
        Assert.Contains("orders_total", html);
    }

    [Fact]
    public void ToHtml_escapes_a_malicious_custom_metric_name()
    {
        const string maliciousName = "<script>alert(1)</script>";
        LoadTestReport report = CreateReport([CreateStep("login", 10L, 10_000L)]) with
        {
            CustomMetrics = [CounterMetric(maliciousName, 1d)],
        };

        string html = report.ToHtml();
        Assert.DoesNotContain(maliciousName, html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(maliciousName), html);
    }
}
namespace Tempest.Domain.Metrics;

/// <summary>
/// Point de sortie des mesures depuis le chemin critique.
/// <para>
/// Le contrat est volontairement <b>non bloquant</b> : un utilisateur virtuel ne doit
/// jamais attendre le consommateur de metriques. Si le buffer est plein, la mesure est
/// perdue et comptabilisee dans <see cref="DroppedMetrics"/> — un compteur non nul
/// invalide la precision du rapport et doit remonter a l'utilisateur.
/// </para>
/// </summary>
public interface IMetricSink
{
    /// <summary>
    /// Publie une mesure sans jamais bloquer.
    /// </summary>
    /// <returns><see langword="false"/> si la mesure a ete rejetee faute de place.</returns>
    bool TryWrite(in MetricResult result);

    /// <summary>
    /// Nombre d'ecritures rejetees depuis le debut du test.
    /// <para>
    /// Le moteur n'essaie qu'une fois par mesure et n'insiste jamais : pour lui, une ecriture
    /// rejetee <b>est</b> une mesure perdue. Un appelant qui reessaierait ferait diverger les
    /// deux notions et gonflerait ce compteur sans rien avoir perdu.
    /// </para>
    /// </summary>
    long DroppedMetrics { get; }
}
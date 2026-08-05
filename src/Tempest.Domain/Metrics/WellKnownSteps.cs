namespace Tempest.Domain.Metrics;

/// <summary>
/// Etapes techniques enregistrees par le moteur lui-meme, en plus de celles declarees
/// par les scenarios. Le prefixe <c>__</c> evite toute collision avec un nom metier.
/// </summary>
public static class WellKnownSteps
{
    /// <summary>
    /// Iteration complete du scenario, mesuree depuis son instant de depart <b>theorique</b>.
    /// C'est la seule metrique qui reflete le temps de bout en bout percu par l'utilisateur
    /// final, dette d'ordonnancement comprise.
    /// </summary>
    public const string ITERATION = "__iteration";
}
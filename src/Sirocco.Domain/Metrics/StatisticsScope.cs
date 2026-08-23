namespace Sirocco.Domain.Metrics;

/// <summary>Perimetre temporel sur lequel un rapport est calcule.</summary>
public enum StatisticsScope
{
    /// <summary>
    /// Depuis le debut du tir. C'est le perimetre du rapport final et de la verification
    /// des seuils en integration continue : un verdict doit porter sur tout le test.
    /// </summary>
    Cumulative = 0,

    /// <summary>
    /// Fenetre glissante recente. C'est le perimetre des tableaux de bord temps reel :
    /// un p99 cumule sur dix minutes met une eternite a reagir a une degradation, alors
    /// qu'une fenetre de dix secondes la montre immediatement.
    /// </summary>
    Sliding = 1,
}
namespace Tempest.Application.Execution;

/// <summary>
/// Autorisation de tir remise par le regulateur de debit a un utilisateur virtuel.
/// Structure <b>unmanaged</b> de 16 octets : le canal de jetons ne genere aucune pression GC,
/// meme a 50 000 jetons par seconde.
/// </summary>
/// <param name="IterationIndex">Index global de l'iteration, base 0.</param>
/// <param name="ScheduledTicks">
/// Instant theorique de depart, calcule depuis l'echeancier du profil de charge —
/// et non l'instant ou le jeton a ete emis. C'est la reference du <i>coordinated omission</i>.
/// </param>
public readonly record struct ExecutionToken(long IterationIndex, long ScheduledTicks);
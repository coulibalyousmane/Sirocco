namespace Tempest.Application.Execution;

/// <summary>
/// Compte les utilisateurs virtuels actuellement en train de consommer des jetons — la
/// concurrence reelle a un instant donne, par opposition a <see cref="LoadTestOptions.MaxVirtualUsers"/>,
/// qui n'est qu'un plafond ou un effectif configure.
/// <para>
/// Incremente/decremente par <see cref="VirtualUserWorker.RunAsync"/> a l'entree et a la sortie de
/// sa boucle de consommation, quelle que soit la raison de la sortie (fin de tir, annulation, quota
/// personnel atteint) — c'est ce compteur, releve periodiquement par un enregistreur de serie
/// temporelle, qui rend visible une montee ou une descente d'utilisateurs dans le temps plutot
/// qu'un effectif statique.
/// </para>
/// </summary>
public sealed class ActiveVirtualUserGauge
{
    private long _count;

    /// <summary>Nombre d'utilisateurs virtuels actuellement actifs.</summary>
    public int Value => (int)Interlocked.Read(ref _count);

    /// <summary>Signale qu'un utilisateur virtuel commence a consommer des jetons.</summary>
    public void Increment() => Interlocked.Increment(ref _count);

    /// <summary>Signale qu'un utilisateur virtuel a cesse de consommer des jetons.</summary>
    public void Decrement() => Interlocked.Decrement(ref _count);
}
using Tempest.Domain.Execution;

namespace Tempest.Domain.Data;

/// <summary>
/// Jeu de donnees en memoire (typiquement charge depuis un CSV ou un JSON, voir
/// <c>Tempest.Scenarios.Data.DataSetLoader</c>) dont un scenario tire une ligne par lecture,
/// selon une <see cref="DataSetIterationStrategy"/>.
/// <para>
/// Immuable et thread-safe : une seule instance est partagee par tous les utilisateurs virtuels
/// pour toute la duree du test, exactement comme le pool de comptes de
/// <c>DynamicCheckoutWorkflow</c> dont ce type generalise le principe (deja code en dur pour un
/// seul scenario) a n'importe quelle source de donnees.
/// </para>
/// </summary>
public sealed class DataSet
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, string>> _rows;
    private readonly DataSetIterationStrategy _strategy;
    private int _cursor = -1;

    /// <summary>Construit un jeu de donnees deja charge en memoire.</summary>
    /// <param name="rows">Lignes, chacune une correspondance nom de colonne vers valeur.</param>
    /// <param name="strategy">Strategie appliquee par <see cref="Pick"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="rows"/> est vide.</exception>
    public DataSet(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, DataSetIterationStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new ArgumentException("Un jeu de donnees ne peut pas etre vide.", nameof(rows));
        }

        _rows = rows;
        _strategy = strategy;
    }

    /// <summary>Nombre de lignes chargees.</summary>
    public int Count => _rows.Count;

    /// <summary>Choisit une ligne selon la strategie configuree.</summary>
    public IReadOnlyDictionary<string, string> Pick(IVirtualUserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int index = _strategy switch
        {
            DataSetIterationStrategy.Circular => (int)((uint)Interlocked.Increment(ref _cursor) % (uint)_rows.Count),
            DataSetIterationStrategy.Random => Random.Shared.Next(_rows.Count),
            DataSetIterationStrategy.UniquePerVirtualUser => (int)((uint)context.VirtualUserId % (uint)_rows.Count),
            _ => throw new NotSupportedException($"Strategie d'iteration non prise en charge : {_strategy}."),
        };

        return _rows[index];
    }
}
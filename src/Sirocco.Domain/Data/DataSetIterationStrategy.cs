namespace Sirocco.Domain.Data;

/// <summary>Facon dont <see cref="DataSet.Pick"/> choisit une ligne parmi celles chargees.</summary>
public enum DataSetIterationStrategy
{
    /// <summary>
    /// Parcourt les lignes dans l'ordre, en boucle, un curseur partage par tous les utilisateurs
    /// virtuels : deux lectures consecutives, meme d'utilisateurs virtuels differents, ne
    /// retombent jamais sur la meme ligne tant que le jeu de donnees n'a pas ete entierement
    /// parcouru.
    /// </summary>
    Circular,

    /// <summary>Une ligne tiree au hasard, uniformement, a chaque lecture.</summary>
    Random,

    /// <summary>
    /// Une ligne fixe par utilisateur virtuel (<see cref="Execution.IVirtualUserContext.VirtualUserId"/>
    /// modulo le nombre de lignes) : la meme ligne a chaque iteration de cet utilisateur, distincte
    /// de celle de ses voisins tant qu'il y a au moins autant de lignes que d'utilisateurs virtuels.
    /// </summary>
    UniquePerVirtualUser,
}
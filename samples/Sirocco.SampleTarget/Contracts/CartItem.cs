namespace Sirocco.SampleTarget.Contracts;

/// <summary>Ligne de panier recue par l'etape de commande.</summary>
internal sealed record CartItem(int ProductId, int Quantity);
namespace Sirocco.Scenarios.Contracts;

/// <summary>Ligne de panier envoyee a l'etape <c>checkout</c>.</summary>
internal sealed record CartItem(int ProductId, int Quantity);
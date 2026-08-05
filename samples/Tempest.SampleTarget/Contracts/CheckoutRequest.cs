namespace Tempest.SampleTarget.Contracts;

/// <summary>Corps de la requete de commande.</summary>
internal sealed record CheckoutRequest(IReadOnlyList<CartItem> Items);
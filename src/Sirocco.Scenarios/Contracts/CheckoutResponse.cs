namespace Sirocco.Scenarios.Contracts;

/// <summary>Confirmation de commande.</summary>
internal sealed record CheckoutResponse(string OrderId, decimal Total);
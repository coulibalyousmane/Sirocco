namespace Sirocco.SampleTarget.Contracts;

/// <summary>Confirmation de commande.</summary>
internal sealed record CheckoutResponse(string OrderId, decimal Total);
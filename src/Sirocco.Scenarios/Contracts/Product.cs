namespace Sirocco.Scenarios.Contracts;

/// <summary>Article du catalogue, tel que renvoye par l'etape <c>browse</c>.</summary>
internal sealed record Product(int Id, string Name, decimal Price);
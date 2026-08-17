using GraphQL;
using GraphQL.Types;
using Tempest.SampleTarget.Contracts;

namespace Tempest.SampleTarget.GraphQl;

/// <summary>Resultat d'une commande passee via la mutation <c>placeOrder</c>.</summary>
internal sealed class OrderResult
{
    public required string Id { get; init; }
    public required int ProductId { get; init; }
    public required int Quantity { get; init; }
    public required decimal Total { get; init; }
}

internal sealed class ProductGraphType : ObjectGraphType<Product>
{
    public ProductGraphType()
    {
        Name = "Product";
        Field(product => product.Id);
        Field(product => product.Name);
        Field(product => product.Price);
    }
}

internal sealed class OrderGraphType : ObjectGraphType<OrderResult>
{
    public OrderGraphType()
    {
        Name = "Order";
        Field(order => order.Id);
        Field(order => order.ProductId);
        Field(order => order.Quantity);
        Field(order => order.Total);
    }
}

/// <summary>
/// Racine de lecture : un seul champ, la liste complete du catalogue — suffisant pour prouver le
/// contrat de plugin cote lecture, pas une API de recherche complete.
/// </summary>
internal sealed class SampleGraphQlQuery : ObjectGraphType
{
    public SampleGraphQlQuery(Product[] catalog)
    {
        Name = "Query";
        Field<ListGraphType<ProductGraphType>>("products").Resolve(_ => catalog);
    }
}

/// <summary>
/// Racine d'ecriture : <c>placeOrder</c> echoue avec une entree GraphQL <c>errors</c> plutot
/// qu'un code de statut HTTP different de 200 pour un identifiant de produit inconnu — exactement
/// le comportement que <c>Tempest.Extensions.GraphQl</c> existe pour verifier.
/// </summary>
internal sealed class SampleGraphQlMutation : ObjectGraphType
{
    public SampleGraphQlMutation(Product[] catalog)
    {
        Name = "Mutation";
        Field<OrderGraphType>("placeOrder")
            .Argument<NonNullGraphType<IntGraphType>>("productId")
            .Argument<NonNullGraphType<IntGraphType>>("quantity")
            .Resolve(context =>
            {
                int productId = context.GetArgument<int>("productId");
                int quantity = context.GetArgument<int>("quantity");
                Product? product = Array.Find(catalog, candidate => candidate.Id == productId);
                if (product is null)
                {
                    context.Errors.Add(new ExecutionError($"Produit introuvable : {productId}"));
                    return null;
                }

                return new OrderResult
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ProductId = productId,
                    Quantity = quantity,
                    Total = product.Price * quantity,
                };
            });
    }
}

internal sealed class SampleGraphQlSchema : Schema
{
    public SampleGraphQlSchema(Product[] catalog)
    {
        Query = new SampleGraphQlQuery(catalog);
        Mutation = new SampleGraphQlMutation(catalog);
    }
}
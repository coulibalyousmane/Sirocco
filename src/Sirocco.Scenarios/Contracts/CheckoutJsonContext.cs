using System.Text.Json.Serialization;

namespace Sirocco.Scenarios.Contracts;

/// <summary>
/// Serialisation JSON generee a la compilation pour les contrats de <see cref="DynamicCheckoutWorkflow"/>.
/// <para>
/// Un <see cref="System.Text.Json.JsonSerializer"/> reflechi doit inspecter chaque type au
/// premier usage et allouer ses metadonnees a la volee ; le generateur de source produit ce
/// code une fois pour toutes a la compilation. Sur le chemin critique d'un utilisateur virtuel,
/// c'est l'equivalent managed d'un <see cref="System.Text.Json.Utf8JsonWriter"/> ecrit a la main,
/// sans le risque d'erreur qui va avec.
/// </para>
/// <para>
/// Les noms de propriete sont fixes explicitement : le nommage genere par defaut pour un type
/// tableau (<see cref="Product"/><c>[]</c>) n'est pas garanti stable d'une version du generateur
/// a l'autre.
/// </para>
/// <para>
/// La politique <c>camelCase</c> est declaree ici, explicitement, plutot que de dependre du
/// comportement par defaut d'un hote ASP.NET Core cote serveur. Sans cette declaration, un
/// <see cref="System.Text.Json.JsonSerializerContext"/> attend les noms de propriete exactement
/// tels que declares (PascalCase) ; la cible de demonstration, elle, ecrit en camelCase via les
/// options ambiantes de son hote. Sans accord explicite, un jeton de connexion arriverait au
/// client avec un nom qui ne correspond a rien — echec silencieux, sans exception.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LoginRequest), TypeInfoPropertyName = nameof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse), TypeInfoPropertyName = nameof(LoginResponse))]
[JsonSerializable(typeof(Product[]), TypeInfoPropertyName = "ProductArray")]
[JsonSerializable(typeof(CheckoutRequest), TypeInfoPropertyName = nameof(CheckoutRequest))]
[JsonSerializable(typeof(CheckoutResponse), TypeInfoPropertyName = nameof(CheckoutResponse))]
internal sealed partial class CheckoutJsonContext : JsonSerializerContext;
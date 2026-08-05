using System.Text.Json.Serialization;

namespace Tempest.SampleTarget.Contracts;

/// <summary>
/// Serialisation JSON generee a la compilation. Cote serveur comme cote client (voir
/// <c>Tempest.Scenarios.Contracts.CheckoutJsonContext</c>), eviter la reflexion sur le chemin
/// de requete est ce qui permet a une cible de demonstration aussi simple de tenir la charge
/// sans que son propre serialiseur ne devienne le facteur limitant.
/// <para>
/// La politique <c>camelCase</c> est declaree ici explicitement, et doit rester identique a
/// celle du contexte client : c'est l'accord entre les deux qui fait fonctionner le contrat
/// HTTP, pas une coincidence entre deux defauts.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LoginRequest), TypeInfoPropertyName = nameof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse), TypeInfoPropertyName = nameof(LoginResponse))]
[JsonSerializable(typeof(Product[]), TypeInfoPropertyName = "ProductArray")]
[JsonSerializable(typeof(CheckoutRequest), TypeInfoPropertyName = nameof(CheckoutRequest))]
[JsonSerializable(typeof(CheckoutResponse), TypeInfoPropertyName = nameof(CheckoutResponse))]
internal sealed partial class SampleTargetJsonContext : JsonSerializerContext;
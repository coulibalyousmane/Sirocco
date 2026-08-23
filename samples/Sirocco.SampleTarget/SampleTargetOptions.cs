namespace Sirocco.SampleTarget;

/// <summary>
/// Reglages de la cible de demonstration. Section de configuration <c>SampleTarget</c>.
/// </summary>
internal sealed class SampleTargetOptions
{
    /// <summary>Nombre de commandes traitees en simultane par defaut.</summary>
    public const int DEFAULT_MAX_CONCURRENT_CHECKOUTS = 40;

    /// <summary>Attente maximale par defaut avant de refuser une commande saturee, en millisecondes.</summary>
    public const int DEFAULT_QUEUE_WAIT_MILLISECONDS = 50;

    /// <summary>Latence simulee minimale par defaut, en millisecondes.</summary>
    public const int DEFAULT_MIN_LATENCY_MILLISECONDS = 10;

    /// <summary>Latence simulee maximale par defaut, en millisecondes.</summary>
    public const int DEFAULT_MAX_LATENCY_MILLISECONDS = 30;

    /// <summary>Duree de vie par defaut d'un jeton de connexion, en secondes.</summary>
    public const int DEFAULT_TOKEN_LIFETIME_SECONDS = 60;

    /// <summary>Taille par defaut du catalogue genere.</summary>
    public const int DEFAULT_PRODUCT_CATALOG_SIZE = 50;

    /// <summary>Graine par defaut du generateur de catalogue.</summary>
    public const int DEFAULT_RANDOM_SEED = 20_260_803;

    /// <summary>Taux d'erreur applicatif par defaut a la commande (0 = jamais).</summary>
    public const double DEFAULT_ERROR_RATE = 0d;

    /// <summary>
    /// Port par defaut du point d'ecoute gRPC dedie.
    /// <para>
    /// Separe du port HTTP principal : sans TLS, Kestrel ne multiplexe pas HTTP/1.1 et HTTP/2
    /// sur un meme port (verifie par un vrai demarrage — l'avertissement est explicite : "HTTP/2
    /// requires TLS application protocol negotiation"). REST et WebSocket restent en HTTP/1.1
    /// sur le port principal ; gRPC exige un port HTTP/2 pur, en clair.
    /// </para>
    /// </summary>
    public const int DEFAULT_GRPC_PORT = 5287;

    /// <summary>
    /// Nombre de messages envoyes par defaut par <c>StreamEcho</c>. Decide par le serveur, pas
    /// par le client : un client realiste ne dicte pas le comportement d'un flux auquel il
    /// s'abonne.
    /// </summary>
    public const int DEFAULT_STREAM_MESSAGE_COUNT = 5;

    /// <summary>
    /// Port par defaut du courtier MQTT embarque. Volontairement distinct du port MQTT
    /// conventionnel (1883) : eviter tout conflit avec un vrai courtier deja installe sur la
    /// machine, la cible de demonstration n'a pas a en dependre.
    /// </summary>
    public const int DEFAULT_MQTT_PORT = 18_830;

    /// <summary>
    /// Commandes simultanees admises avant mise en attente. Au-dela, la requete patiente
    /// <see cref="QueueWaitMilliseconds"/> puis echoue en 503 : c'est ce plafond qui rend
    /// la cible capable de saturer sous une charge suffisante.
    /// </summary>
    public int MaxConcurrentCheckouts { get; init; } = DEFAULT_MAX_CONCURRENT_CHECKOUTS;

    /// <summary>Attente maximale avant de refuser une commande faute de place.</summary>
    public int QueueWaitMilliseconds { get; init; } = DEFAULT_QUEUE_WAIT_MILLISECONDS;

    /// <summary>Borne basse de la latence simulee sur chaque appel.</summary>
    public int MinLatencyMilliseconds { get; init; } = DEFAULT_MIN_LATENCY_MILLISECONDS;

    /// <summary>Borne haute de la latence simulee sur chaque appel.</summary>
    public int MaxLatencyMilliseconds { get; init; } = DEFAULT_MAX_LATENCY_MILLISECONDS;

    /// <summary>
    /// Duree de vie d'un jeton de connexion. Volontairement finie : une commande peut
    /// echouer en 401, ce qui exerce le rafraichissement de session du scenario client.
    /// </summary>
    public int TokenLifetimeSeconds { get; init; } = DEFAULT_TOKEN_LIFETIME_SECONDS;

    /// <summary>Nombre d'articles du catalogue genere au demarrage.</summary>
    public int ProductCatalogSize { get; init; } = DEFAULT_PRODUCT_CATALOG_SIZE;

    /// <summary>Graine du generateur de catalogue : deux demarrages produisent le meme catalogue.</summary>
    public int RandomSeed { get; init; } = DEFAULT_RANDOM_SEED;

    /// <summary>
    /// Probabilite qu'une commande, par ailleurs valide, echoue avec une erreur applicative.
    /// Nulle par defaut : les echecs observes viennent alors uniquement de la saturation.
    /// </summary>
    public double ErrorRate { get; init; } = DEFAULT_ERROR_RATE;

    /// <summary>Port du point d'ecoute gRPC dedie (HTTP/2 pur, en clair).</summary>
    public int GrpcPort { get; init; } = DEFAULT_GRPC_PORT;

    /// <summary>Nombre de messages envoyes par <c>StreamEcho</c> avant fermeture du flux.</summary>
    public int StreamMessageCount { get; init; } = DEFAULT_STREAM_MESSAGE_COUNT;

    /// <summary>Port du courtier MQTT embarque.</summary>
    public int MqttPort { get; init; } = DEFAULT_MQTT_PORT;
}
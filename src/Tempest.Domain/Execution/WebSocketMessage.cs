using System.Net.WebSockets;

namespace Tempest.Domain.Execution;

/// <summary>
/// Message recu sur une <see cref="WebSocketConnection"/>, deja reassemble si le protocole
/// l'a fragmente sur plusieurs trames.
/// </summary>
/// <param name="Type">Nature du message : texte, binaire ou fermeture demandee par le pair.</param>
/// <param name="Text">Contenu decode en UTF-8 si <paramref name="Type"/> vaut <see cref="WebSocketMessageType.Text"/>, sinon <see langword="null"/>.</param>
/// <param name="ByteCount">Volume recu, en octets, quel que soit le type.</param>
public readonly record struct WebSocketMessage(WebSocketMessageType Type, string? Text, int ByteCount);
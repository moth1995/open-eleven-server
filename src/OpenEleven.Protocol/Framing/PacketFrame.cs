namespace OpenEleven.Protocol.Framing;

/// <summary>One decrypted outer packet: header fields plus the still-Blowfished body.</summary>
public readonly record struct PacketFrame(ushort Id, uint Count, byte[] Data)
{
    public const ushort TextCommand = 0x0060;

    public bool IsTextCommand => Id == TextCommand;
}

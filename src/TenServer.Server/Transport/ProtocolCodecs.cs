using TenServer.Protocol.Crypto;
using TenServer.Protocol.Framing;
using TenServer.Protocol.Kv;

namespace TenServer.Server.Transport;

/// <summary>
/// Bundle of the stateless protocol services. Registered once as a singleton so a
/// connection takes a single dependency instead of five.
/// </summary>
public sealed class ProtocolCodecs(
    XorCipher xor,
    BlowfishEcb blowfish,
    PacketCodec packets,
    KvReader reader,
    KvWriter writer)
{
    public XorCipher Xor { get; } = xor;
    public BlowfishEcb Blowfish { get; } = blowfish;
    public PacketCodec Packets { get; } = packets;
    public KvReader Reader { get; } = reader;
    public KvWriter Writer { get; } = writer;
}

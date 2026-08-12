using System.Text;
using TenServer.Protocol.Framing;
using TenServer.Protocol.Kv;
using TenServer.Server.Configuration;

namespace TenServer.Server.Transport;

/// <summary>
/// Builds the multi-line block logged for one packet. Shared by both directions so an
/// inbound and an outbound packet are directly comparable in the console.
/// </summary>
internal static class PacketLogFormatter
{
    /// <summary>A parsed key=value packet: pretty payload, then the bytes.</summary>
    public static string Format(KvMessage message, string payloadText, DebugOptions debug)
    {
        var sb = new StringBuilder();

        if (debug.PrettyPrintPayloads)
            sb.Append(KvPrettyPrinter.Format(message));
        else
            sb.Append("  ").Append(payloadText);

        AppendHex(sb, Encoding.ASCII.GetBytes(payloadText), debug, "payload");
        return sb.ToString();
    }

    /// <summary>
    /// A packet with no readable payload. These were previously logged at Debug, so at
    /// the default Information level they did not appear at all.
    /// </summary>
    public static string FormatBinary(ReadOnlySpan<byte> data, DebugOptions debug)
    {
        var sb = new StringBuilder();
        AppendHex(sb, data, debug, "body (still Blowfish-encrypted)");
        return sb.Length == 0 ? "  (no data)" : sb.ToString();
    }

    private static void AppendHex(
        StringBuilder sb, ReadOnlySpan<byte> data, DebugOptions debug, string label)
    {
        if (!debug.HexDump || data.IsEmpty)
            return;

        var max = debug.HexDumpMaxBytes <= 0 ? int.MaxValue : debug.HexDumpMaxBytes;

        if (sb.Length > 0)
            sb.Append('\n');

        sb.Append("  ").Append(label).Append(" (").Append(data.Length).Append(" bytes):\n");

        // Indent the dump so it reads as part of this packet's block rather than as
        // separate log lines.
        foreach (var line in HexDump.Format(data, maxBytes: max).Split('\n'))
            sb.Append("  ").Append(line).Append('\n');

        sb.Length--; // trailing newline
    }
}

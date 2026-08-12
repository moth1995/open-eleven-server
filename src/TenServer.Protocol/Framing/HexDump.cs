using System.Text;

namespace TenServer.Protocol.Framing;

/// <summary>Same layout as the reference implementation's <c>format_packet_hexdump</c>.</summary>
public static class HexDump
{
    public static string Format(ReadOnlySpan<byte> data, int width = 16)
    {
        if (data.IsEmpty)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i += width)
        {
            var chunk = data.Slice(i, Math.Min(width, data.Length - i));

            var hex = new StringBuilder(width * 3);
            var ascii = new StringBuilder(width);
            foreach (var b in chunk)
            {
                if (hex.Length > 0) hex.Append(' ');
                hex.Append(b.ToString("X2"));
                ascii.Append(b is >= 32 and <= 126 ? (char)b : '.');
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"{i:X4}  {hex,-48}  {ascii}");
        }

        return sb.ToString();
    }

    public static string Format(string text) => Format(Encoding.ASCII.GetBytes(text));
}

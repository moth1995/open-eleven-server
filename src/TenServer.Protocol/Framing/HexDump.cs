using System.Text;

namespace TenServer.Protocol.Framing;

/// <summary>Same layout as the reference implementation's <c>format_packet_hexdump</c>.</summary>
public static class HexDump
{
    /// <param name="maxBytes">
    /// Stop after this many bytes and note how many were left. A single room-list packet
    /// is over a kilobyte, which is more console than it is worth; the interesting bytes
    /// of a malformed payload are almost always at the front.
    /// </param>
    public static string Format(ReadOnlySpan<byte> data, int width = 16, int maxBytes = int.MaxValue)
    {
        if (data.IsEmpty)
            return string.Empty;

        var omitted = 0;
        if (maxBytes > 0 && data.Length > maxBytes)
        {
            omitted = data.Length - maxBytes;
            data = data[..maxBytes];
        }

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

        if (omitted > 0)
            sb.Append($"\n      ... {omitted} more byte{(omitted == 1 ? "" : "s")}");

        return sb.ToString();
    }

    public static string Format(string text) => Format(Encoding.ASCII.GetBytes(text));
}

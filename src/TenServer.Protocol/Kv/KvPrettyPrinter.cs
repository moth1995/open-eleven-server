using System.Globalization;
using System.Text;

namespace TenServer.Protocol.Kv;

/// <summary>
/// Renders a <see cref="KvMessage"/> as an indented, key-aligned block for logs.
/// </summary>
/// <remarks>
/// A room list on one line is roughly 1200 characters of nested brackets, which is
/// unreadable in a console and hides exactly the structural mistakes this protocol
/// punishes — a bracketed list where the client wants indexed keys, a record nested one
/// level too deep. This formats from the parsed object model rather than the wire text,
/// so what it shows is what the writer will emit.
/// <para>
/// It is a viewing aid, not a serialiser: <see cref="KvWriter"/> remains the only thing
/// that produces bytes for the client.
/// </para>
/// </remarks>
public static class KvPrettyPrinter
{
    private const string Indent = "  ";

    public static string Format(KvMessage message)
    {
        var sb = new StringBuilder();
        WriteFields(sb, message, depth: 1);
        return sb.ToString().TrimEnd('\n');
    }

    private static void WriteFields(StringBuilder sb, KvMessage message, int depth)
    {
        var fields = message.Fields.ToArray();
        if (fields.Length == 0)
            return;

        // Align the '=' within this block only. Aligning across nesting levels would
        // push deep values off the right of a terminal.
        var width = fields.Max(f => f.Key.Length);
        var pad = string.Concat(Enumerable.Repeat(Indent, depth));

        foreach (var (key, value) in fields)
        {
            sb.Append(pad).Append(key.PadRight(width)).Append(" = ");
            WriteValue(sb, value, depth);
            sb.Append('\n');
        }
    }

    private static void WriteValue(StringBuilder sb, object? value, int depth)
    {
        var pad = string.Concat(Enumerable.Repeat(Indent, depth));

        switch (value)
        {
            case KvMessage record:
                sb.Append("{\n");
                WriteFields(sb, record, depth + 1);
                sb.Append(pad).Append('}');
                break;

            case IReadOnlyList<KvMessage> list:
                WriteRecordList(sb, list, depth, pad, i => $"[{i}]");
                break;

            // Indexed scalars (teamLog[0]=108, desired_position[3]="NO") stay on one
            // line: expanded they are a dozen near-identical rows that bury the rest.
            case IndexedField indexed when indexed.Values.All(v => v is not KvMessage):
                sb.Append('[');
                for (var i = 0; i < indexed.Values.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    WriteScalar(sb, indexed.Values[i]);
                }
                sb.Append(']');
                if (indexed.Values.Count > 0)
                    sb.Append("   ← written as ").Append("key[0..")
                      .Append(indexed.Values.Count - 1).Append(']');
                break;

            case IndexedField indexedRecords:
                WriteRecordList(
                    sb,
                    indexedRecords.Values.OfType<KvMessage>().ToArray(),
                    depth, pad, i => $"[{i}] (indexed key)");
                break;

            case KvArray array:
                sb.Append('[');
                for (var i = 0; i < array.Values.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    WriteScalar(sb, array.Values[i]);
                }
                sb.Append(']');
                break;

            default:
                WriteScalar(sb, value);
                break;
        }
    }

    private static void WriteRecordList(
        StringBuilder sb,
        IReadOnlyList<KvMessage> list,
        int depth,
        string pad,
        Func<int, string> label)
    {
        if (list.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append("[\n");
        for (var i = 0; i < list.Count; i++)
        {
            sb.Append(pad).Append(Indent).Append(label(i)).Append(" {\n");
            WriteFields(sb, list[i], depth + 2);
            sb.Append(pad).Append(Indent).Append("}\n");
        }
        sb.Append(pad).Append(']');
    }

    private static void WriteScalar(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("\"\"");
                break;
            case string s:
                sb.Append('"').Append(s).Append('"');
                break;
            case KvRaw raw:
                sb.Append(raw.Text);
                break;
            case bool b:
                sb.Append('"').Append(b ? "YES" : "NO").Append('"');
                break;
            default:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}

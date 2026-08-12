using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TenServer.Server.Configuration;

/// <summary>Wire-size validation and operator-configured censorship for live text chat.</summary>
public sealed class ChatTextPolicy(IOptionsMonitor<ServerOptions> options)
{
    // PES2010 FUN_00739D10 parses statement into a 0x101-byte destination.
    public const int MaxEncodedBytes = 256;

    public bool TrySanitize(string? input, out string value, out bool censored)
    {
        value = input ?? "";
        censored = false;

        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(value) > MaxEncodedBytes)
            return false;

        foreach (var configured in options.CurrentValue.Protocol.BlockedTerms)
        {
            var term = configured.Trim();
            if (term.Length == 0)
                continue;

            var pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}_])";
            var replaced = Regex.Replace(
                value,
                pattern,
                "***",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            if (!string.Equals(replaced, value, StringComparison.Ordinal))
            {
                value = replaced;
                censored = true;
            }
        }

        return Encoding.UTF8.GetByteCount(value) <= MaxEncodedBytes;
    }
}

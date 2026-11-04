using System.Net;
using System.Text;

namespace SmsPanel.Api.Services;

public sealed class InboundTextDecoder
{
    public string Decode(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var decoded = input;
        for (var pass = 0; pass < 3; pass++)
        {
            var next = WebUtility.HtmlDecode(decoded);
            if (next == decoded) break;
            decoded = next;
        }

        var result = new StringBuilder(decoded.Length);
        for (var index = 0; index < decoded.Length; index++)
        {
            var current = decoded[index];
            if (current == '\u0006' && index + 3 < decoded.Length && decoded[index + 1] == '\0' && decoded[index + 2] == '\u00FF' && decoded[index + 3] == '\u00FD')
            {
                result.Append('\uFFFD'); index += 3; continue;
            }
            if (current == '\u0006' && index + 2 < decoded.Length && decoded[index + 1] == '\0' && decoded[index + 2] <= '\u00FF')
            {
                result.Append((char)(0x0600 | decoded[index + 2])); index += 2; continue;
            }
            if (current == '\u0006' && index + 2 < decoded.Length && decoded[index + 1] == '\u00FF' && decoded[index + 2] == '\u00FD')
            {
                result.Append('\uFFFD'); index += 2; continue;
            }
            if (current == '\u0006' && index + 1 < decoded.Length && decoded[index + 1] <= '\u00FF')
            {
                result.Append((char)(0x0600 | decoded[++index])); continue;
            }
            if (current == '\u0600' && index + 1 < decoded.Length && decoded[index + 1] <= '\u00FF')
            {
                result.Append((char)(0x0600 | decoded[++index])); continue;
            }
            if (current == '\u00FF' && index + 1 < decoded.Length && decoded[index + 1] == '\u00FD')
            {
                result.Append('\uFFFD'); index++; continue;
            }
            if (char.IsControl(current) && current is not ('\r' or '\n' or '\t')) continue;
            result.Append(current);
        }
        return result.ToString();
    }

    public bool NeedsDecoding(string input) => input.Contains('\u0006') || input.Contains('\u0600') || input.Contains("&amp;#", StringComparison.OrdinalIgnoreCase) || input.Contains("&#255;", StringComparison.OrdinalIgnoreCase);
}

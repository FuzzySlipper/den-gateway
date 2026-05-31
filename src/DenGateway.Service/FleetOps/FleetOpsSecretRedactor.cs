using System.Text.RegularExpressions;

namespace DenGateway.Service.FleetOps;

/// <summary>
/// Redacts secrets (tokens, keys, passwords, .env values) from output text.
/// </summary>
public static partial class FleetOpsSecretRedactor
{
    // Pattern matches: Bearer tokens, sk-* (OpenAI-style), various key/value patterns
    [GeneratedRegex(@"(?i)(bearer\s+)[a-z0-9_\-\.]{8,}|(sk-[a-z0-9]{8,})|(api[_-]?key\s*[:=]\s*['""]?)[a-z0-9_\-]{8,}|(secret\s*[:=]\s*['""]?)[a-z0-9_\-]{8,}|(password\s*[:=]\s*['""]?)[a-z0-9_\-]{8,}|(token\s*[:=]\s*['""]?)[a-z0-9_\-]{8,}|(auth\s*[:=]\s*['""]?)\{.*?\}|(eyJ[a-z0-9_\-\.]{10,})")]
    private static partial Regex SecretPattern();

    /// <summary>
    /// Redact known secret patterns from a single line.
    /// </summary>
    public static string RedactLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        return SecretPattern().Replace(line, "[REDACTED]");
    }

    /// <summary>
    /// Redact secrets from a list of lines and optionally truncate to max lines.
    /// </summary>
    public static IReadOnlyList<string> ProcessOutput(IReadOnlyList<string> lines, int maxLines)
    {
        var result = new List<string>(Math.Min(lines.Count, maxLines));
        var take = Math.Min(lines.Count, maxLines);
        for (int i = 0; i < take; i++)
        {
            result.Add(RedactLine(lines[i]));
        }
        return result.AsReadOnly();
    }
}

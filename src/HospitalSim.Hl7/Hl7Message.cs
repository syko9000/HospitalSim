using System.Text;

namespace HospitalSim.Hl7;

/// <summary>
/// Minimal pipe-delimited HL7 v2.x message builder. This simulator only ever emits well-formed outbound text.
/// </summary>
public sealed class Hl7Message
{
    private readonly List<string> _segments = [];

    public Hl7Message AddSegment(string segmentId, params string[] fields)
    {
        _segments.Add(string.Join('|', new[] { segmentId }.Concat(fields)));
        return this;
    }

    public string Build()
    {
        var sb = new StringBuilder();
        foreach (var segment in _segments)
        {
            sb.Append(segment).Append('\r');
        }
        return sb.ToString();
    }

    public static string EscapeField(string? value) =>
        (value ?? string.Empty)
            .Replace("\\", "\\E\\")
            .Replace("|", "\\F\\")
            .Replace("^", "\\S\\")
            .Replace("&", "\\T\\")
            .Replace("~", "\\R\\");

    // The exact reverse of EscapeField, applied in reverse order - \E\ (a literal backslash) has to be
    // resolved last, or a backslash it produces could get mistaken for the start of one of the other
    // escape sequences still waiting to be unescaped.
    public static string UnescapeField(string? value) =>
        (value ?? string.Empty)
            .Replace("\\R\\", "~")
            .Replace("\\T\\", "&")
            .Replace("\\S\\", "^")
            .Replace("\\F\\", "|")
            .Replace("\\E\\", "\\");
}

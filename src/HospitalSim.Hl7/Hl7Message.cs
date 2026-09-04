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
}

namespace HospitalSim.Hl7;

/// <summary>
/// Minimal parser for the pipe-delimited segments this project itself produces. MSH is special-cased:
/// the field separator character occupies the position a delimiter would normally hold, so splitting
/// "MSH|^~\&|..." on '|' yields a fields array already offset by one (index 0 is MSH-2, not MSH-1) -
/// Field()/Component() account for that automatically.
/// </summary>
public sealed class Hl7ParsedMessage
{
    private readonly Dictionary<string, string[]> _segments;

    private Hl7ParsedMessage(Dictionary<string, string[]> segments, string messageType, string messageControlId)
    {
        _segments = segments;
        MessageType = messageType;
        MessageControlId = messageControlId;
    }

    public string MessageType { get; }
    public string MessageControlId { get; }

    public static Hl7ParsedMessage Parse(string raw)
    {
        var segments = new Dictionary<string, string[]>();
        foreach (var line in raw.Split('\r', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4 || line[3] != '|') continue;
            var segmentId = line[..3];
            if (segments.ContainsKey(segmentId)) continue; // keep only the first occurrence of a repeated segment
            segments[segmentId] = line[4..].Split('|');
        }

        var msh = segments.GetValueOrDefault("MSH") ?? [];
        var messageType = msh.Length > 7 ? msh[7] : "";
        var controlId = msh.Length > 8 ? msh[8] : "";

        return new Hl7ParsedMessage(segments, messageType, controlId);
    }

    public string? Field(string segmentId, int fieldNumber) =>
        RawField(segmentId, fieldNumber) is { } raw ? Hl7Message.UnescapeField(raw) : null;

    public string? Component(string segmentId, int fieldNumber, int componentNumber)
    {
        // Split the raw, still-escaped text, not Field()'s unescaped result - an escaped literal ^
        // (\S\) must survive as component text, not get mistaken for a real separator. Only the
        // final extracted piece gets unescaped, same as Field() does for a whole field.
        var raw = RawField(segmentId, fieldNumber);
        if (string.IsNullOrEmpty(raw)) return null;
        var components = raw.Split('^');
        return componentNumber - 1 < components.Length ? Hl7Message.UnescapeField(components[componentNumber - 1]) : null;
    }

    private string? RawField(string segmentId, int fieldNumber)
    {
        if (!_segments.TryGetValue(segmentId, out var fields)) return null;
        var index = segmentId == "MSH" ? fieldNumber - 2 : fieldNumber - 1;
        return index >= 0 && index < fields.Length ? fields[index] : null;
    }
}

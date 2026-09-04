using System.Text.Json;

namespace HospitalSim.Ancillary;

public static class PendingResultStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(PendingResultState state, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(state.Snapshot(), Options));

    public static PendingResultState LoadOrCreate(string path)
    {
        if (!File.Exists(path)) return new PendingResultState();

        var results = JsonSerializer.Deserialize<List<PendingResult>>(File.ReadAllText(path), Options);
        return results is null ? new PendingResultState() : PendingResultState.Restore(results);
    }
}

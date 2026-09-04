using System.Text.Json;

namespace HospitalSim.Clinicals;

public static class VisitStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(VisitState state, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(state.Snapshot(), Options));

    public static VisitState LoadOrCreate(string path)
    {
        if (!File.Exists(path)) return new VisitState();

        var visits = JsonSerializer.Deserialize<List<Visit>>(File.ReadAllText(path), Options);
        return visits is null ? new VisitState() : VisitState.Restore(visits);
    }
}

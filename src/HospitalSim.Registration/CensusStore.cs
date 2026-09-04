using System.Text.Json;

using HospitalSim.World;

namespace HospitalSim.Registration;

public static class CensusStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(Census census, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(census.CreateSnapshot(), Options));

    public static Census LoadOrCreate(Hospital hospital, string path)
    {
        if (!File.Exists(path)) return new Census(hospital);

        var snapshot = JsonSerializer.Deserialize<CensusSnapshot>(File.ReadAllText(path), Options);
        return snapshot is null ? new Census(hospital) : Census.Restore(hospital, snapshot);
    }
}

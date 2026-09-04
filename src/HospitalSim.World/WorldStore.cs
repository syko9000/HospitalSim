using System.Text.Json;
using System.Text.Json.Serialization;

namespace HospitalSim.World;

public static class WorldStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(HospitalWorld world, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(world, Options));

    public static HospitalWorld? Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<HospitalWorld>(File.ReadAllText(path), Options)
            : null;

    public static HospitalWorld LoadOrGenerate(string path, int seed = 42)
    {
        var existing = Load(path);
        if (existing is not null) return existing;

        var world = new WorldGenerator(seed).Generate();
        Save(world, path);
        return world;
    }
}

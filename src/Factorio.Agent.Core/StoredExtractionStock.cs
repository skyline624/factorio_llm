namespace Factorio.Agent.Core;

/// <summary>One native photograph distinguishes empty fuel slots from a still-burning fuel item and checks storage capacity.</summary>
public sealed record StoredExtractionStock(long Output, long Insertable, long StoredFuel, double BurningJoules)
{
    public static StoredExtractionStock From(FactorySnapshot snapshot, string drillId, string chestId, string item, bool electric = false)
    {
        var chest = snapshot.Records.Single(r => r.Kind == "inventory" && r.EntityId == chestId);
        long output = chest.Data.GetProperty("items").TryGetProperty(item, out var amount) ? amount.GetInt64() : 0;
        long capacity = chest.Data.GetProperty("capacityHints").GetProperty(item).GetProperty("insertable").GetInt64();
        var burner = electric ? new NativeBurnerStock(0, 0) : NativeBurnerStock.From(snapshot, drillId);
        if (output < 0 || capacity < 0) throw new InvalidDataException("Invalid native extraction stock or capacity.");
        return new(output, capacity, burner.StoredFuel, burner.BurningJoules);
    }
}

public sealed record NativeBurnerStock(long StoredFuel, double BurningJoules)
{
    public bool Empty => StoredFuel == 0 && BurningJoules == 0;

    public static NativeBurnerStock From(FactorySnapshot snapshot, string drillId)
    {
        var drill = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == drillId);
        string fuelId = drill.Data.GetProperty("fuelInventoryId").GetString() ?? throw new InvalidDataException("Missing native fuel inventory identity.");
        var fuel = snapshot.Records.Single(r => r.Kind == "inventory" && r.EntityId == drillId && r.Id == fuelId);
        long stored = fuel.Data.GetProperty("items").EnumerateObject().Sum(p => p.Value.GetInt64());
        double burning = drill.Data.GetProperty("burnerRemainingJoules").GetDouble();
        if (stored < 0 || !double.IsFinite(burning) || burning < 0)
            throw new InvalidDataException("Invalid native extraction stock or burner evidence.");
        return new(stored, burning);
    }
}

using System.Text.Json;

namespace Factorio.Agent.Core;

public sealed record MaterialEndpoint(string EntityId, string InventoryId, string? Recipe, long UnitsPerCycle, bool Source)
{
    public static MaterialEndpoint From(FactorySnapshot snapshot, ProductionCatalog catalog, string entityId, string item, bool source)
        => TryFrom(snapshot, catalog, entityId, item, source)
            ?? throw new InvalidOperationException("Transport requires a container or an enabled, configured deterministic crafting endpoint.");

    public static MaterialEndpoint? TryFrom(FactorySnapshot snapshot, ProductionCatalog catalog, string entityId, string item, bool source)
    {
        snapshot.SummarizeStocks();
        var entity = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == entityId);
        if (entity.Data.GetProperty("type").GetString() == "container")
            return new(entityId, snapshot.Records.Single(r => r.Kind == "inventory" && r.EntityId == entityId).Id, null, 0, source);
        if (entity.Data.GetProperty("type").GetString() is not ("assembling-machine" or "furnace"))
            return null;
        var works = snapshot.Records.Where(r => r.Kind == "work" && r.EntityId == entityId).ToArray();
        if (works.Length != 1) throw new InvalidDataException("Missing or ambiguous native endpoint work evidence.");
        var work = works[0];
        string? recipeName = work.Data.TryGetProperty("recipe", out var configured) ? configured.GetString() : null;
        if (recipeName is null) return null;
        var recipe = catalog.Recipes.Single(r => r.Name == recipeName);
        if (!recipe.Enabled) return null;
        var materials = source ? recipe.Products : recipe.Ingredients;
        if ((source && materials.Count != 1) || !materials.Any(m => m.Name == item)
            || materials.Where(m => m.Name == item).Any(m => !m.DeterministicItem))
            return null;
        long units = checked((long)materials.Where(m => m.Name == item).Sum(m => m.Amount!.Value));
        return new(entityId, work.Data.GetProperty(source ? "outputInventoryId" : "inputInventoryId").GetString()!, recipeName, units, source);
    }

    public MaterialEndpointReading Read(FactorySnapshot snapshot, string item)
    {
        var inventory = snapshot.Records.Single(r => r.Id == InventoryId && r.Kind == "inventory" && r.EntityId == EntityId);
        var items = inventory.Data.GetProperty("items");
        if (Source && items.EnumerateObject().Any(p => p.Name != item && p.Value.GetInt64() > 0))
            throw new InvalidOperationException("An unfiltered source inserter requires an inventory containing only the requested item.");
        long count = items.TryGetProperty(item, out var value) ? value.GetInt64() : 0;
        if (Recipe is null) return new(count, 0, false, 0);
        var work = snapshot.Records.Single(r => r.Kind == "work" && r.EntityId == EntityId);
        if (work.Data.GetProperty("recipe").GetString() != Recipe) throw new InvalidDataException("The transport endpoint recipe changed.");
        return new(count, work.Data.GetProperty("productsFinished").GetInt64(), work.Data.GetProperty("inProcess").GetBoolean(), UnitsPerCycle);
    }
}

public sealed record MaterialEndpointReading(long Count, long Cycles, bool InProcess, long UnitsPerCycle);
public sealed record BeltTransportReading(MaterialEndpointReading Source, MaterialEndpointReading Target, long Transit)
{
    public long DeliveredSince(BeltTransportReading before)
    {
        foreach (var endpoint in new[] { Source, Target, before.Source, before.Target })
            if (endpoint.Count < 0 || endpoint.Cycles < 0 || endpoint.UnitsPerCycle < 0)
                throw new InvalidDataException("Invalid native transport stock or production counter.");
        if (Transit < 0 || before.Transit < 0 || Source.Cycles < before.Source.Cycles || Target.Cycles < before.Target.Cycles
            || Source.UnitsPerCycle != before.Source.UnitsPerCycle || Target.UnitsPerCycle != before.Target.UnitsPerCycle)
            throw new InvalidDataException("Transport counters or recipe quantities changed inconsistently.");
        long produced = checked((Source.Cycles - before.Source.Cycles) * Source.UnitsPerCycle);
        long consumedCycles = Target.Cycles - before.Target.Cycles + (Target.InProcess ? 1 : 0) - (before.Target.InProcess ? 1 : 0);
        if (consumedCycles < 0) throw new InvalidDataException("An engaged destination craft disappeared without completion.");
        long delivered = checked(before.Source.Count + produced - Source.Count - (Transit - before.Transit));
        long received = checked(Target.Count - before.Target.Count + consumedCycles * Target.UnitsPerCycle);
        if (delivered < 0 || delivered != received)
            throw new InvalidDataException("Source, transit and destination do not conserve the transported item. Reconcile native effects.");
        return delivered;
    }

    public static BeltTransportReading From(FactorySnapshot snapshot, MaterialEndpoint source, MaterialEndpoint target, string item,
        IReadOnlyList<string> belts, IReadOnlyList<string> inserters)
    {
        snapshot.SummarizeStocks();
        long transit = 0;
        foreach (var (id, arm) in belts.Select(id => (id, false)).Concat(inserters.Select(id => (id, true))))
        {
            var records = TransportRecords(snapshot, id, arm);
            foreach (var record in records)
                foreach (var content in record.Data.GetProperty("items").EnumerateObject())
                {
                    long count = content.Value.GetInt64();
                    if (content.Name != item && count > 0) throw new InvalidDataException("An unrelated item entered the isolated transport line.");
                    transit = checked(transit + count);
                }
        }
        return new(source.Read(snapshot, item), target.Read(snapshot, item), transit);
    }

    /// <summary>Includes every native owner section, including an underground input's buried sections.</summary>
    public static IReadOnlyList<FactoryRecord> TransportRecords(FactorySnapshot snapshot, string id, bool inserter)
    {
        var records = snapshot.Records.Where(r => r.Kind == "transit" && r.EntityId == id).ToArray();
        if (inserter)
        {
            if (records.Length != 1)
                throw new InvalidDataException("Missing or ambiguous native inserter hand.");
            return records;
        }
        var entities = snapshot.Records.Where(r => r.Kind == "entity" && r.EntityId == id).ToArray();
        // Preserve the existing two-section ordinary-belt contract. Underground owners declare their extra buried sections.
        bool undergroundOwner = entities.Length == 1 && entities[0].Data.TryGetProperty("type", out var type)
            && type.GetString() == "underground-belt";
        if (!undergroundOwner)
        {
            if (entities.Length > 1 || records.Length != 2) throw new InvalidDataException("Missing or ambiguous native ordinary belt lanes.");
            return records;
        }
        if (!entities[0].Data.TryGetProperty("transportLines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Missing native underground transport-line list.");
        var declared = lines.EnumerateArray().Select(p => p.GetString()).ToArray();
        int expected = 2;
        if (undergroundOwner)
        {
            if (!entities[0].Data.TryGetProperty("transport", out var transport)
                || !transport.TryGetProperty("underground", out var underground)
                || !underground.TryGetProperty("transportLineCount", out var count) || !count.TryGetInt32(out expected)
                || expected is < 2 or > 4)
                throw new InvalidDataException("Missing native underground transport-line count.");
        }
        if (declared.Length != expected || declared.Any(string.IsNullOrWhiteSpace)
            || declared.Distinct().Count() != expected || records.Length != expected
            || !declared.ToHashSet().SetEquals(records.Select(r => r.Id))
            || records.Any(r => r.Name != "transport-line"
                || !r.Data.TryGetProperty("collection", out var collection) || collection.GetString() != "native-owner-line-section"
                || !r.Data.TryGetProperty("index", out var index) || !index.TryGetInt32(out int number) || number < 1 || number > expected)
            || records.Select(r => r.Data.GetProperty("index").GetInt32()).Distinct().Count() != expected)
            throw new InvalidDataException("Missing or ambiguous native belt sections.");
        return records;
    }
}

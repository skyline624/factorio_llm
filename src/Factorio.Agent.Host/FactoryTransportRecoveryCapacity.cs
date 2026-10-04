using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Reserves shared empty main-inventory slots for equipment and all potentially arriving bus contents.</summary>
internal static class FactoryTransportRecoveryCapacity
{
    internal static IReadOnlyDictionary<string, long> Incoming(FactorySnapshot snapshot,
        IReadOnlyList<FactoryTransportRetirement> retirements, IReadOnlySet<string> busEntities, string item)
    {
        if (retirements.Count is < 1 or > 201 || retirements.Select(r => r.EntityId).Distinct(StringComparer.Ordinal).Count() != retirements.Count
            || retirements.Any(r => !busEntities.Contains(r.EntityId)))
            throw new InvalidDataException("Recovery requires a bounded, distinct set of owned bus pieces.");
        var incoming = retirements.GroupBy(r => r.Part.Item).ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);
        // Belts keep moving after the source is stopped. Reserve all known bus transit, not just the target piece's current contents.
        long transit = 0;
        foreach (var record in snapshot.Records.Where(r => r.Kind == "transit" && busEntities.Contains(r.EntityId)))
            foreach (var amount in record.Data.GetProperty("items").EnumerateObject())
            {
                if (amount.Name != item || !amount.Value.TryGetInt64(out long count) || count < 0)
                    throw new InvalidDataException("Only clean, normal single-item bus transit can be recovered.");
                transit = checked(transit + count);
            }
        incoming[item] = checked(incoming.GetValueOrDefault(item) + transit + 1); // One final in-flight source hand movement.
        return incoming;
    }

    internal static bool Fits(FactorySnapshot snapshot, ProductionCatalog catalog, IReadOnlyDictionary<string, long> incoming)
    {
        if (snapshot.Scope != catalog.Scope || incoming.Count is < 1 or > 8 || incoming.Any(p => p.Value < 1 || !catalog.Items.ContainsKey(p.Key)))
            throw new InvalidDataException("Recovery stock and native capacity belong to one actor scope and bounded item set.");
        var actor = snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        string mainId = actor.Data.GetProperty("mainInventoryId").GetString()!;
        var data = snapshot.Records.Single(r => r.Kind == "inventory" && r.EntityId == actor.EntityId && r.Id == mainId).Data;
        int usable = data.GetProperty("usableSlots").GetInt32(), slots = data.GetProperty("slots").GetInt32();
        if (usable < 0 || usable > slots || slots > 1000) throw new InvalidDataException("Invalid actor inventory slot coverage.");
        var occupied = new HashSet<int>();
        var stacks = Array(data.GetProperty("stacks")).ToArray();
        foreach (var stack in stacks)
        {
            int slot = stack.GetProperty("slot").GetInt32();
            if (slot < 1 || slot > slots || !occupied.Add(slot)) throw new InvalidDataException("Invalid or duplicate native stack slot.");
        }
        var filtered = new HashSet<int>();
        foreach (var filter in Array(data.GetProperty("filters")))
        {
            int slot = filter.GetProperty("slot").GetInt32();
            if (slot < 1 || slot > slots) throw new InvalidDataException("Invalid native filtered slot.");
            filtered.Add(slot);
            occupied.Add(slot); // Do not promise that a filtered empty slot accepts every incoming item.
        }
        long neededSlots = 0;
        foreach (var (item, count) in incoming)
        {
            int size = catalog.Items[item].StackSize;
            var hint = data.GetProperty("capacityHints").GetProperty(item);
            if (size < 1 || hint.GetProperty("certainty").GetString() != "native-estimate"
                || !hint.GetProperty("insertable").TryGetInt64(out long capacity) || capacity < 0)
                throw new InvalidDataException("Recovery requires current native capacity hints and stack sizes.");
            if (capacity < count || !hint.GetProperty("canInsertOne").GetBoolean()) return false;
            long partial = 0;
            foreach (var stack in stacks.Where(s => s.GetProperty("slot").GetInt32() <= usable
                && !filtered.Contains(s.GetProperty("slot").GetInt32()) && s.GetProperty("name").GetString() == item
                && s.GetProperty("quality").GetString() == "normal" && !s.TryGetProperty("durability", out _)
                && !s.TryGetProperty("ammo", out _)))
            {
                long present = stack.GetProperty("count").GetInt64();
                if (present < 1 || present > size) throw new InvalidDataException("Invalid native normal stack size.");
                partial = checked(partial + size - present);
            }
            long remaining = Math.Max(0, count - partial);
            if (remaining > 0) neededSlots = checked(neededSlots + (remaining - 1) / size + 1);
        }
        // Each proven partial stack belongs to one item. Remaining items must jointly fit the unfiltered empty slots.
        return neededSlots <= usable - occupied.Count(s => s <= usable);
    }

    private static IEnumerable<JsonElement> Array(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().ToArray()
        : value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any() ? []
        : throw new InvalidDataException("Expected a complete native array or empty Lua table.");
}

using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Native boiler fuel demand and the conditions under which a belt may replace an actor delivery.</summary>
internal static class PowerFuelPolicy
{
    public static bool HasLinks(FactoryState state) => (state.Transports ?? []).Any(b => b.Item == FactoryLogistics.Fuel
        && b.Consumers.Any(c => state.Cells.Any(cell => cell.Id == c.TargetCellId && cell.Kind == "power")));

    public static double? Demand(ProductionCatalog catalog, FactoryCell cell, string item, PowerState? power)
    {
        if (cell.Kind != "power" || cell.Status != "ready" || item != FactoryLogistics.Fuel || power is null) return null;
        if (power.Scope != catalog.Scope) throw new InvalidDataException("Native fuel demand belongs to another actor scope.");
        if (!cell.Entities.TryGetValue("boiler", out string? id) || !catalog.Items.TryGetValue(item, out var fuel)
            || !double.IsFinite(fuel.FuelValue) || fuel.FuelValue <= 0 || fuel.FuelCategory is null
            || !catalog.Items.TryGetValue(cell.MachineItem, out var machine)) return null;
        var boiler = power.Boilers.SingleOrDefault(b => b.Id == id);
        if (boiler is null || boiler.Name != machine.PlaceEntity || boiler.FuelCategories?.GetValueOrDefault(fuel.FuelCategory) != true
            || !double.IsFinite(boiler.EnergyPerTick) || boiler.EnergyPerTick <= 0) return null;
        // Native maximum energy usage is fuel input; effectivity scales delivered heat, not fuel input a second time.
        return boiler.EnergyPerTick * 3600 / fuel.FuelValue;
    }

    public static bool AutomatedReserve(FactorySnapshot snapshot, FactoryCell cell, long stack,
        IReadOnlySet<(string Chest, string Item)>? covered)
    {
        if (covered is null || !cell.Entities.TryGetValue("input-chest", out string? chest)
            || !covered.Contains((chest, FactoryLogistics.Fuel)) || !cell.Entities.TryGetValue("boiler", out string? boiler)) return false;
        var native = snapshot.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == boiler);
        bool lit = FactoryLogistics.Items(snapshot, boiler).Values.Any(n => n > 0)
            || native?.Data.TryGetProperty("burnerRemainingJoules", out var reserve) == true && reserve.GetDouble() > 0;
        if (!lit || FactoryLogistics.Items(snapshot, chest).GetValueOrDefault(FactoryLogistics.Fuel)
            + FactoryLogistics.Items(snapshot, boiler).GetValueOrDefault(FactoryLogistics.Fuel) < Math.Max(1, stack / 4)) return false;
        var arm = snapshot.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == cell.Entities.GetValueOrDefault("input-inserter"));
        return arm?.Data.TryGetProperty("transport", out var transport) == true
            && transport.TryGetProperty("pickupTargetId", out var pickup) && pickup.GetString() == chest
            && transport.TryGetProperty("dropTargetId", out var drop) && drop.GetString() == boiler;
    }

    public static bool ProducerActive(FactoryCell source, FactorySnapshot snapshot)
    {
        string? producer = source.Entities.GetValueOrDefault(source.IsResource ? "drill" : "machine");
        if (producer is null) return false;
        return snapshot.Records.Any(r => r.Kind == "work" && r.EntityId == producer
            && r.Data.TryGetProperty("statusName", out var status) && status.GetString() is "working" or "waiting_for_space_in_destination" or "full_output");
    }
}

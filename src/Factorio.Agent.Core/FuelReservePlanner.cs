namespace Factorio.Agent.Core;

public sealed record FuelReservePlan(string BoilerId, string ChestId, string InserterId, string Fuel,
    int TargetStock, long CurrentStock, int RefillAmount);

public sealed class FuelReservePlanner
{
    public FuelReservePlan? Choose(SpatialSnapshot map, FactorySnapshot stock, ProductionCatalog catalog,
        string boilerId, long networkId, IReadOnlyDictionary<string, long> carried, double expectedEnergy, bool reserve)
    {
        if (map.Scope != stock.Scope || map.Scope != catalog.Scope) throw new InvalidDataException("Fuel reserve observations span different actor scopes.");
        if (!double.IsFinite(expectedEnergy) || expectedEnergy < 0) throw new ArgumentOutOfRangeException(nameof(expectedEnergy));
        stock.SummarizeStocks();
        var boiler = map.Entities.Single(e => e.Id == boilerId);
        var categories = map.Prototypes[boiler.Name].FuelCategories;
        foreach (var arm in map.Entities.Where(e => e.Force == boiler.Force && map.Prototypes[e.Name].Type == "inserter"
            && e.DropTargetId == boilerId && e.PickupTargetId is not null && e.Power?.NetworkId == networkId).OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var chest = map.Entities.SingleOrDefault(e => e.Id == arm.PickupTargetId && e.Force == boiler.Force && map.Prototypes[e.Name].Type == "container");
            if (chest is null) continue;
            if (map.Entities.Any(e => e.Id != arm.Id && e.PickupTargetId == chest.Id))
                throw new InvalidDataException("The fuel chest is shared with another inserter.");
            var records = stock.Records.Where(r => (r.EntityId == chest.Id && r.Kind == "inventory") || (r.EntityId == arm.Id && r.Kind == "transit")).ToArray();
            var stockedNames = records.SelectMany(r => r.Data.GetProperty("items").EnumerateObject())
                .Where(p => p.Value.GetInt64() > 0).Select(p => p.Name).Distinct(StringComparer.Ordinal).ToArray();
            bool Supported(string fuel) => catalog.Items.TryGetValue(fuel, out var item) && item.FuelValue > 0
                && item.FuelCategory is { } category && categories?.ContainsKey(category) == true
                && catalog.Mining.Values.Any(products => products.Any(p => p.Name == fuel && p.DeterministicItem));
            if (stockedNames.Length > 1 || stockedNames.Any(name => !Supported(name)))
                throw new InvalidDataException("Fuel reserve contents are mixed or unsupported; refuse to replace them.");
            string fuel = stockedNames.FirstOrDefault() ?? catalog.Items.Keys.Where(Supported)
                .OrderByDescending(name => carried.GetValueOrDefault(name) > 0).ThenByDescending(name => catalog.Items[name].FuelValue)
                .ThenBy(name => name, StringComparer.Ordinal).First();
            var reading = FeederStockReading.From(stock, chest.Id, arm.Id, fuel);
            int target = (int)Math.Clamp(Math.Ceiling(expectedEnergy * 1.25 / catalog.Items[fuel].FuelValue), 50, 500);
            int missing = reserve || reading.Source < target * .8 ? checked((int)Math.Max(0, target - reading.Source)) : 0;
            return new(boilerId, chest.Id, arm.Id, fuel, target, reading.Source, missing);
        }
        return null;
    }
}

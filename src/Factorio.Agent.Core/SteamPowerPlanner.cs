namespace Factorio.Agent.Core;

public sealed record PowerEquipment(string Pump, string Boiler, string Engine, string Pole, string Load)
{
    public string[] Items => [Pump, Boiler, Engine, Pole, Load];
}
public sealed record PlannedMachine(string Role, string Item, PlacementCandidate Placement);
public sealed record SteamPowerPlan(ActorScope Scope, long ObservedTick, IReadOnlyList<PlannedMachine> Machines,
    FluidConnectionPlacement WaterConnection, FluidConnectionPlacement SteamConnection)
{
    public void ValidateRecovery(ActorScope current, PowerEquipment equipment)
    {
        (string Role, string Item)[] expected = [("pump", equipment.Pump), ("boiler", equipment.Boiler),
            ("engine", equipment.Engine), ("pole", equipment.Pole), ("load", equipment.Load)];
        if (Scope.WorldId != current.WorldId || Machines.Count != expected.Length
            || expected.Any(pair => Machines.Count(m => m.Role == pair.Role && m.Item == pair.Item) != 1))
            throw new InvalidDataException("The recovery plan has a different world or equipment set.");
    }
}

public sealed class SteamPowerPlanner
{
    public static MapPosition? FactoryAnchor(IEnumerable<(string Name, MapPosition Position)> knownEntities,
        ProductionCatalog catalog)
    {
        var productionNames = catalog.Items.Values.Where(i => i.PlaceEntityType is "furnace" or "mining-drill" or "assembling-machine" or "lab")
            .Select(i => i.PlaceEntity).OfType<string>().ToHashSet(StringComparer.Ordinal);
        MapPosition[] positions = knownEntities.Where(e => productionNames.Contains(e.Name)).Select(e => e.Position).ToArray();
        if (positions.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new InvalidDataException("Invalid known factory position.");
        return positions.Length == 0 ? null : new(positions.Average(p => p.X), positions.Average(p => p.Y));
    }

    public SteamPowerPlan? Find(SpatialSnapshot map, PowerEquipment equipment)
    {
        var placements = new PlacementPlanner();
        var connections = new FluidConnectionPlanner();
        EntityGeometry pump = Geometry(equipment.Pump), boiler = Geometry(equipment.Boiler), engine = Geometry(equipment.Engine);
        EntityGeometry pole = Geometry(equipment.Pole), load = Geometry(equipment.Load);
        if (pump.FluidSourceOffset is null || pole.SupplyArea is not > 0)
            throw new InvalidDataException("Missing native water source or electric supply geometry.");
        var field = new SpatialCollisionField(map);
        foreach (PlacementCandidate pumpPlacement in placements.FindCandidates(field, equipment.Pump, map.Actor.Position, requireBuildReach: false))
        {
            MapPosition offset = ExtractionPlanner.Rotate(pump.FluidSourceOffset, pumpPlacement.Direction);
            if (field.FluidAt(new(pumpPlacement.Position.X + offset.X, pumpPlacement.Position.Y + offset.Y)) != "water") continue;
            SpatialSnapshot withPump = Add(map, "pump", pump, pumpPlacement);
            foreach (FluidConnectionPlacement water in connections.Find(new(withPump), pump, pumpPlacement, equipment.Boiler, "water"))
            {
                SpatialSnapshot withBoiler = Add(withPump, "boiler", boiler, water.Placement);
                foreach (FluidConnectionPlacement steam in connections.Find(new(withBoiler), boiler, water.Placement, equipment.Engine, "steam"))
                {
                    SpatialSnapshot withEngine = Add(withBoiler, "engine", engine, steam.Placement);
                    WorldBox engineBounds = engine.CollisionBox.Rotate(steam.Placement.Direction).Translate(steam.Placement.Position);
                    foreach (PlacementCandidate polePlacement in placements.FindCandidates(new(withEngine), equipment.Pole,
                        steam.Placement.Position, requireBuildReach: false))
                    {
                        double radius = pole.SupplyArea.Value;
                        var supply = new WorldBox(new(polePlacement.Position.X - radius, polePlacement.Position.Y - radius),
                            new(polePlacement.Position.X + radius, polePlacement.Position.Y + radius));
                        if (!supply.Overlaps(engineBounds)) continue;
                        SpatialSnapshot withPole = Add(withEngine, "pole", pole, polePlacement);
                        PlacementCandidate? loadPlacement = placements.FindCandidates(new(withPole), equipment.Load,
                            polePlacement.Position, requireBuildReach: false).FirstOrDefault(p =>
                                supply.Contains(load.CollisionBox.Rotate(p.Direction).Translate(p.Position)));
                        if (loadPlacement is null) continue;
                        return new(map.Scope, map.CollectedTick,
                            [new("pump", equipment.Pump, pumpPlacement), new("boiler", equipment.Boiler, water.Placement),
                             new("engine", equipment.Engine, steam.Placement), new("pole", equipment.Pole, polePlacement),
                             new("load", equipment.Load, loadPlacement)], water, steam);
                    }
                }
            }
        }
        return null;

        EntityGeometry Geometry(string item) => map.Prototypes[map.Items[item].EntityName];
    }

    private static SpatialSnapshot Add(SpatialSnapshot map, string id, EntityGeometry geometry, PlacementCandidate placement) =>
        map with
        {
            Entities = [.. map.Entities, new("planned:" + id, geometry.Name, placement.Position,
            geometry.CollisionBox.Rotate(placement.Direction).Translate(placement.Position), placement.Direction, "planned")]
        };
}

namespace Factorio.Agent.Core;

/// <summary>A fluid machine site: its cell and the pipe routes proven for the first recipe port assignment.</summary>
public sealed record FluidCellSite(CellLayout Layout, IReadOnlyList<PlannedFluidSupply> Supplies);
/// <summary>An extractor cell on a fluid deposit, with the deposit's native amount.</summary>
public sealed record ExtractorSite(string ResourceId, double Amount, CellLayout Layout);

/// <summary>
/// Synthesizes cells for machines with pipe connections from native geometry. The pole, and the chest-fed inserters of a
/// recipe with solid ingredients or products, stand on a machine face without fluid port targets, so pipes reach every port.
/// The engine assigns recipe fluids to boxes only after construction, so a site is kept only when every assignment routes.
/// </summary>
public sealed class FluidCellPlanner
{
    public const string PlannedId = "planned:fluid-cell";
    // Filter of an input box an assignment leaves unused: routes neither enter it nor pass next to it.
    private const string Unassigned = "";
    private static readonly int[] Directions = [0, 4, 8, 12];

    /// <summary>Pipe connections a prototype exposes at a placement, rotated as the engine rotates them.</summary>
    public static IReadOnlyList<ObservedFluidConnection> Ports(EntityGeometry machine, PlacementCandidate placement) =>
        (machine.FluidBoxes ?? []).SelectMany(box => box.Connections.Where(p => p.Type == "normal").Select(port =>
        {
            if (port.Positions.Count != 4 || port.Direction is < 0 or > 12 || port.Direction % 4 != 0)
                throw new InvalidDataException("Invalid native cardinal fluid connection geometry.");
            var local = port.Positions[placement.Direction / 4];
            var position = new MapPosition(placement.Position.X + local.X, placement.Position.Y + local.Y);
            var forward = ExtractionPlanner.Rotate(new(0, -1), (port.Direction + placement.Direction) % 16);
            return new ObservedFluidConnection(box.Index, port.Index, position, new(position.X + forward.X, position.Y + forward.Y),
                Type: port.Type, FlowDirection: port.FlowDirection, Filter: box.Filter);
        })).ToArray();

    /// <summary>
    /// One cell per machine face free of port targets: the pole and, with solids, input then output inserter along the face,
    /// their chests one tile further out. The pole between the inserters supplies both and the machine.
    /// </summary>
    public IEnumerable<CellLayout> Layouts(SpatialSnapshot map, CellEquipment equipment, PlacementCandidate placement, bool input, bool output)
    {
        EntityGeometry machine = Geometry(map, equipment.Machine), pole = Geometry(map, equipment.Pole);
        bool io = input || output;
        EntityGeometry? arm = io ? Geometry(map, equipment.Inserter) : null;
        if (pole.Type != "electric-pole" || pole.TileWidth != 1 || pole.TileHeight != 1 || machine.TileWidth < 1 || machine.TileHeight < 1
            || io && (arm!.Type != "inserter" || arm.InserterPickup is null || arm.InserterDrop is null || arm.TileWidth != 1
                || Geometry(map, equipment.Chest).TileWidth != 1))
            throw new InvalidDataException("Fluid cells require native machines and one-tile poles, inserters and chests.");
        int w = placement.Direction % 8 == 0 ? machine.TileWidth : machine.TileHeight;
        int h = placement.Direction % 8 == 0 ? machine.TileHeight : machine.TileWidth;
        double left = placement.Position.X - w / 2.0, top = placement.Position.Y - h / 2.0;
        WorldBox body = machine.CollisionBox.Rotate(placement.Direction).Translate(placement.Position);
        var targets = Ports(machine, placement).Select(p => p.TargetPosition).ToArray();
        var faces = new (MapPosition[] Tiles, MapPosition Out)[]
        {
            (Enumerable.Range(0, h).Select(i => new MapPosition(left - .5, top + .5 + i)).ToArray(), new(-1, 0)),
            (Enumerable.Range(0, h).Select(i => new MapPosition(left + w + .5, top + .5 + i)).ToArray(), new(1, 0)),
            (Enumerable.Range(0, w).Select(i => new MapPosition(left + .5 + i, top + h + .5)).ToArray(), new(0, 1)),
            (Enumerable.Range(0, w).Select(i => new MapPosition(left + .5 + i, top - .5)).ToArray(), new(0, -1))
        };
        foreach (var (tiles, step) in faces)
        {
            if (tiles.Length < (io ? 3 : 1) || tiles.Any(t => targets.Any(p => p.DistanceTo(t) < .01))) continue;
            MapPosition Outward(MapPosition tile) => new(tile.X + step.X, tile.Y + step.Y);
            var entities = new List<PlannedEntity> { new("machine", equipment.Machine, placement.Position, placement.Direction) };
            if (input)
            {
                var at = tiles[0];
                entities.Add(new("input-inserter", equipment.Inserter, at, FactoryBandPlanner.Direction(arm!, at, from: Outward(at), into: body)));
                entities.Add(new("input-chest", equipment.Chest, Outward(at), 0));
            }
            if (output)
            {
                var at = tiles[2];
                entities.Add(new("output-inserter", equipment.Inserter, at, FactoryBandPlanner.Direction(arm!, at, from: body, into: Outward(at))));
                entities.Add(new("output-chest", equipment.Chest, Outward(at), 0));
            }
            var polePosition = io ? tiles[1] : tiles[tiles.Length / 2];
            entities.Add(new("pole", equipment.Pole, polePosition, 0));
            if (!entities.Where(e => e.Role is "machine" or "input-inserter" or "output-inserter")
                    .All(e => PowerGridPlanner.Supplies(polePosition, pole, Box(map, e)))) continue;
            var boxes = entities.Select(e => Box(map, e)).ToArray();
            var footprint = new WorldBox(new(Math.Floor(boxes.Min(b => b.Min.X)), Math.Floor(boxes.Min(b => b.Min.Y))),
                new(Math.Ceiling(boxes.Max(b => b.Max.X)), Math.Ceiling(boxes.Max(b => b.Max.Y))));
            var beyond = (io ? tiles.Select(Outward) : tiles).Select(Outward).ToArray();
            var walkway = new WorldBox(new(beyond.Min(p => p.X) - .5, beyond.Min(p => p.Y) - .5), new(beyond.Max(p => p.X) + .5, beyond.Max(p => p.Y) + .5));
            yield return new(new CellSlot(0, 0, true), entities, footprint, walkway);
        }
    }

    /// <summary>
    /// A cell near the anchor whose parts are clear, off deposits, whose chests stay reachable and whose every recipe port
    /// assignment routes each fluid through the given supply search. Of the keep nearest such sites, the one needing the
    /// fewest pipes wins; at most maximumRouted sites are routed.
    /// </summary>
    public FluidCellSite? Find(SpatialSnapshot map, string force, CellEquipment equipment, string pipeItem, MapPosition anchor,
        bool input, bool output, IReadOnlyList<string> fluids, Func<SpatialSnapshot, string, FluidSupplyRoute?> route,
        int radius = 12, int maximumRouted = 48, int keep = 6, CancellationToken cancellationToken = default)
    {
        if (radius is < 1 or > 48) throw new ArgumentOutOfRangeException(nameof(radius));
        if (maximumRouted is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumRouted));
        if (keep is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(keep));
        EntityGeometry machine = Geometry(map, equipment.Machine);
        var vacant = map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() };
        var field = new SpatialCollisionField(vacant);
        var deposits = map.Entities.Where(e => map.Prototypes[e.Name].Type == "resource").Select(e => e.Bounds).ToArray();
        int routed = 0;
        var found = new List<FluidCellSite>();
        foreach (var placement in Candidates(machine, anchor, radius))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!field.PlacementClear(machine, placement.Position, placement.Direction)) continue;
            foreach (var layout in Layouts(map, equipment, placement, input, output))
            {
                // Building over a deposit wastes future extraction.
                if (layout.Entities.Any(e => deposits.Any(d => d.Overlaps(Box(map, e))))) continue;
                if (!layout.Entities.All(e => e.Role == "machine" || field.PlacementClear(Geometry(map, e.Item), e.Position, e.Direction))) continue;
                if (++routed > maximumRouted || found.Count >= keep) return Shortest(found);
                var projected = Project(vacant, force, layout, equipment);
                var supplies = RouteEveryAssignment(projected, pipeItem, fluids, route, cancellationToken);
                if (supplies is null) continue;
                var piped = new SpatialCollisionField(WithPipes(projected, pipeItem, supplies.SelectMany(s => s.Supply.Route.Pipes).ToArray(), force, "chest",
                    reserveTiles: true));
                if (!PlacementPlanner.CanEscape(piped, layout.Footprint)) continue;
                if (projected.Entities.Where(e => e.Id.EndsWith("-chest", StringComparison.Ordinal))
                    .All(chest => new PlacementPlanner().FindInteractionApproach(piped, chest) is not null)) found.Add(new(layout, supplies));
            }
        }
        return Shortest(found);

        // Fewer pipes cost less iron and leave less to rebuild; the nearest site wins ties.
        static FluidCellSite? Shortest(IReadOnlyList<FluidCellSite> sites) =>
            sites.OrderBy(s => s.Supplies.Sum(p => p.Supply.Route.Pipes.Count)).FirstOrDefault();
    }

    /// <summary>The cell entities added to the observed map; the machine exposes its prototype ports under <see cref="PlannedId"/>.</summary>
    public static SpatialSnapshot Project(SpatialSnapshot map, string force, CellLayout layout, CellEquipment equipment)
    {
        EntityGeometry machine = Geometry(map, equipment.Machine);
        var placement = new PlacementCandidate(layout.Machine.Position, layout.Machine.Direction, 0);
        var added = layout.Entities.Select(e => e.Role == "machine"
            ? new SpatialEntity(PlannedId, machine.Name, e.Position, Box(map, e), e.Direction, force, FluidConnections: Ports(machine, placement))
            : new SpatialEntity($"{PlannedId}:{e.Role}", map.Items[e.Item].EntityName, e.Position, Box(map, e), e.Direction, force));
        return map with { Entities = [.. map.Entities, .. added] };
    }

    /// <summary>Filters the planned machine's input boxes as a recipe would: listed boxes take their fluid, the others none.</summary>
    public static SpatialSnapshot Assign(SpatialSnapshot projected, IReadOnlyDictionary<int, string> boxes) => projected with
    {
        Entities = projected.Entities.Select(e => e.Id != PlannedId ? e : e with
        {
            FluidConnections = (e.FluidConnections ?? []).Select(p => Input(p) ? p with { Filter = boxes.GetValueOrDefault(p.BoxIndex, Unassigned) } : p).ToArray()
        }).ToArray()
    };

    /// <summary>
    /// Routes for the first assignment of fluids to distinct compatible input boxes of the planned machine, or null when any
    /// assignment cannot route all its fluids jointly in some order.
    /// </summary>
    public static IReadOnlyList<PlannedFluidSupply>? RouteEveryAssignment(SpatialSnapshot projected, string pipeItem, IReadOnlyList<string> fluids,
        Func<SpatialSnapshot, string, FluidSupplyRoute?> route, CancellationToken cancellationToken = default)
    {
        if (fluids.Count == 0) return [];
        if (fluids.Count > 3 || fluids.Distinct(StringComparer.Ordinal).Count() != fluids.Count)
            throw new InvalidOperationException("Fluid cells support at most three distinct fluid inputs.");
        var machine = projected.Entities.Single(e => e.Id == PlannedId);
        var boxes = (machine.FluidConnections ?? []).Where(Input).GroupBy(p => p.BoxIndex).OrderBy(g => g.Key)
            .Select(g => (Index: g.Key, g.First().Filter)).ToArray();
        IReadOnlyList<PlannedFluidSupply>? first = null;
        foreach (var assignment in Assignments(fluids, boxes, new Dictionary<int, string>()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assigned = Assign(projected, assignment);
            var routed = Orders(fluids).Select(order => Route(assigned, pipeItem, machine.Force, order, route)).FirstOrDefault(r => r is not null);
            if (routed is null) return null;
            first ??= routed;
        }
        return first;
    }

    /// <summary>The nearest free deposit an extractor can stand on with its output port facing an open tile and a pole beside it.</summary>
    public ExtractorSite? FindExtractor(SpatialSnapshot map, CellEquipment equipment, string resourceName, string pipeItem)
    {
        EntityGeometry drill = Geometry(map, equipment.Machine), pipe = Geometry(map, pipeItem);
        if (drill.Type != "mining-drill" || !drill.IsElectric || drill.FluidBoxes?.Any(b => b.ProductionType == "output") != true)
            throw new InvalidDataException("Fluid extraction requires a native electric drill with an output fluid box.");
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        var deposits = map.Entities.Where(e => map.Prototypes[e.Name].Type == "resource").ToArray();
        foreach (var resource in deposits.Where(e => e.Name == resourceName && e.Amount > 0
                && map.Prototypes[e.Name].ResourceCategory is { } category && drill.ResourceCategories?.GetValueOrDefault(category) == true
                && (map.StationaryThreats ?? []).All(t => e.Position.DistanceTo(t.Position) > t.Range + SpatialCollisionField.StationaryThreatMargin))
            .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).ThenBy(e => e.Id, StringComparer.Ordinal))
            foreach (int direction in Directions)
            {
                var placement = new PlacementCandidate(resource.Position, direction, resource.Position.DistanceTo(map.Actor.Position));
                if (!field.PlacementClear(drill, resource.Position, direction)) continue;
                // Other deposits stay free for future extractors, and the first pipe must fit in front of the port.
                bool Free(WorldBox box) => !deposits.Any(d => d.Id != resource.Id && d.Bounds.Overlaps(box));
                if (!Ports(drill, placement).Where(p => p.FlowDirection is "output" or "input-output").All(p =>
                        field.PlacementClear(pipe, p.TargetPosition, 0) && Free(pipe.CollisionBox.Translate(p.TargetPosition)))) continue;
                foreach (var layout in Layouts(map, equipment, placement, false, false))
                {
                    var pole = layout.Role("pole")!;
                    if (field.PlacementClear(Geometry(map, pole.Item), pole.Position, 0) && Free(Box(map, pole)))
                        return new(resource.Id, resource.Amount!.Value, layout);
                }
            }
        return null;
    }

    private static IReadOnlyList<PlannedFluidSupply>? Route(SpatialSnapshot map, string pipeItem, string force, IReadOnlyList<string> order,
        Func<SpatialSnapshot, string, FluidSupplyRoute?> route)
    {
        var planned = new List<PlannedFluidSupply>();
        foreach (string fluid in order)
        {
            var supply = route(map, fluid);
            if (supply is null || supply.Route.Status != PipeRouteStatus.Found || supply.Route.Pipes.Count > 200) return null;
            planned.Add(new(fluid, supply));
            map = WithPipes(map, pipeItem, supply.Route.Pipes, force, fluid);
        }
        return planned;
    }

    // Planned pipes join every neighbour, so later routes keep their distance as they would from built ones.
    private static SpatialSnapshot WithPipes(SpatialSnapshot map, string pipeItem, IReadOnlyList<MapPosition> pipes, string force, string tag,
        bool reserveTiles = false)
    {
        var pipe = Geometry(map, pipeItem);
        return map with
        {
            Entities = [.. map.Entities, .. pipes.Select((position, index) => new SpatialEntity($"{PlannedId}:{tag}:{index}", pipe.Name, position,
                reserveTiles ? new(new(position.X - .5, position.Y - .5), new(position.X + .5, position.Y + .5))
                    : pipe.CollisionBox.Translate(position), 0, force, FluidConnections: Directions.Select((direction, port) =>
                {
                    var offset = ExtractionPlanner.Rotate(new(0, -1), direction);
                    return new ObservedFluidConnection(1, port + 1, position, new(position.X + offset.X, position.Y + offset.Y),
                        Type: "normal", FlowDirection: "input-output");
                }).ToArray()))]
        };
    }

    private static IEnumerable<PlacementCandidate> Candidates(EntityGeometry machine, MapPosition anchor, int radius)
    {
        var candidates = new List<PlacementCandidate>();
        foreach (int direction in Directions)
        {
            int w = direction % 8 == 0 ? machine.TileWidth : machine.TileHeight;
            int h = direction % 8 == 0 ? machine.TileHeight : machine.TileWidth;
            for (double x = Math.Floor(anchor.X - radius) + w % 2 * .5; x <= anchor.X + radius; x++)
                for (double y = Math.Floor(anchor.Y - radius) + h % 2 * .5; y <= anchor.Y + radius; y++)
                {
                    var position = new MapPosition(x, y);
                    if (position.DistanceTo(anchor) <= radius) candidates.Add(new(position, direction, position.DistanceTo(anchor)));
                }
        }
        return candidates.OrderBy(c => c.Score).ThenBy(c => c.Position.Y).ThenBy(c => c.Position.X).ThenBy(c => c.Direction);
    }

    private static IEnumerable<IReadOnlyDictionary<int, string>> Assignments(IReadOnlyList<string> fluids,
        IReadOnlyList<(int Index, string? Filter)> boxes, Dictionary<int, string> chosen)
    {
        if (chosen.Count == fluids.Count) { yield return new Dictionary<int, string>(chosen); yield break; }
        string fluid = fluids[chosen.Count];
        foreach (var box in boxes.Where(b => !chosen.ContainsKey(b.Index) && (b.Filter is null || b.Filter == fluid)))
        {
            chosen[box.Index] = fluid;
            foreach (var assignment in Assignments(fluids, boxes, chosen)) yield return assignment;
            chosen.Remove(box.Index);
        }
    }

    private static IEnumerable<IReadOnlyList<string>> Orders(IReadOnlyList<string> fluids)
    {
        if (fluids.Count <= 1) { yield return fluids; yield break; }
        for (int index = 0; index < fluids.Count; index++)
            foreach (var tail in Orders(fluids.Where((_, i) => i != index).ToArray()))
                yield return [fluids[index], .. tail];
    }

    private static bool Input(ObservedFluidConnection port) => port.Type == "normal" && port.FlowDirection is "input" or "input-output";

    private static WorldBox Box(SpatialSnapshot map, PlannedEntity e) =>
        Geometry(map, e.Item).CollisionBox.Rotate(e.Direction).Translate(e.Position);

    private static EntityGeometry Geometry(SpatialSnapshot map, string item) =>
        map.Items.TryGetValue(item, out var placeable) && map.Prototypes.TryGetValue(placeable.EntityName, out var geometry)
            ? geometry : throw new ArgumentException($"Request native geometry for {item} before planning.", nameof(item));
}

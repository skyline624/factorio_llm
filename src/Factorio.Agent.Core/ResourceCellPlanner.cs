namespace Factorio.Agent.Core;

/// <summary>Items of one resource cell. Miners have no furnace or inserter; burner miners also need no pole.</summary>
public sealed record ResourceCellEquipment(string Drill, string Chest, string? Furnace = null, string? Inserter = null, string? Pole = null);
/// <summary>"miner" drills the product into a chest; "smelter" drills the single ore of the product's recipe into a furnace.</summary>
public sealed record ResourceSupply(string Kind, string Product, string Resource, NativeRecipe? Recipe = null);
/// <summary>
/// A straight row of identical cells on one deposit. Cell i's drill tiles start at Origin + i * Pitch along the row axis,
/// and every chest opens onto one shared two-tile walkway on the drill output side.
/// </summary>
public sealed record ResourceRow(int Id, string Kind, string Product, string Resource, ResourceCellEquipment Equipment,
    MapPosition Origin, int Direction, int Pitch, int Cells, double CellPerMinute);
public enum ResourceRowSearchStatus { Found, NoSite, SearchBudgetExhausted }
public sealed record ResourceRowSearch(ResourceRowSearchStatus Status, ResourceRow? Row = null, IReadOnlyList<SpatialEntity>? Clearance = null);

/// <summary>
/// Synthesizes drill cells on observed deposits from native geometry: the drill output vector chooses the receiver tile,
/// inserter vectors choose the arm direction and chest tile, pole supply areas choose the pole tile. Drill mining areas
/// may only touch deposits of the wanted resource, and the row is accepted only with a proven exit from its walkway.
/// </summary>
public sealed class ResourceCellPlanner
{
    public const int MaximumRowCells = 8;
    public const double MinimumSupplyMinutes = 10;
    private const string ReservationName = "resource-row-reservation";

    private sealed record OreReserve(double Units, double CompetingDrills);
    private sealed record CellCoverage(int Covered, IReadOnlyDictionary<string, OreReserve> Ore);

    // Local cell frame: A runs along the row, B grows away from the drill's output edge (B = 0 touches the drill).
    private sealed record Frame(int Width, int Height, int Fx, int Fy)
    {
        public bool Vertical => Fx == 0;
        public int Depth => Vertical ? Height : Width;
        public int Span => Vertical ? Width : Height;
        public MapPosition Along => Vertical ? new(1, 0) : new(0, 1);
        public WorldBox Tiles(int a, int b, int spanA, int spanB)
        {
            int f0 = (Vertical ? Fy : Fx) < 0 ? -b - spanB : Depth + b;
            return Vertical ? new(new(a, f0), new(a + spanA, f0 + spanB)) : new(new(f0, a), new(f0 + spanB, a + spanA));
        }
        public (int A, int B) Local(int x, int y) => Vertical ? (x, Fy < 0 ? -1 - y : y - Height) : (y, Fx < 0 ? -1 - x : x - Width);
    }

    private sealed record Part(PlannedEntity Entity, int A, int B, int SpanA, int SpanB);

    private sealed record Template(Frame Frame, IReadOnlyList<Part> Parts, int SpanMin, int Pitch, int Reach)
    {
        public WorldBox Footprint => Frame.Tiles(SpanMin, -Frame.Depth, Pitch, Frame.Depth + Reach + 1);
        // The walkway lies beyond the farthest cell row; a row's walkway adds one open tile at each end.
        public WorldBox Walkway(int cells, bool ends) => Frame.Tiles(SpanMin - (ends ? 1 : 0), Reach + 1, cells * Pitch + (ends ? 2 : 0), 2);
        public MapPosition Corner(MapPosition origin, int index) =>
            new(origin.X + index * Pitch * Frame.Along.X, origin.Y + index * Pitch * Frame.Along.Y);
        public WorldBox Tiles(Part part) => Frame.Tiles(part.A, part.B, part.SpanA, part.SpanB);
    }

    /// <summary>Distance between neighbouring cells of this equipment and direction, or null if no native cell forms.</summary>
    public static int? Pitch(SpatialSnapshot map, ResourceCellEquipment equipment, int direction) => Build(map, equipment, direction)?.Pitch;

    public CellLayout Layout(SpatialSnapshot map, ResourceRow row, int index)
    {
        if (index < 0 || index >= row.Cells) throw new ArgumentOutOfRangeException(nameof(index));
        Template template = Checked(map, row);
        MapPosition corner = template.Corner(row.Origin, index);
        return new(new(row.Id, index, true), template.Parts.Select(p => p.Entity with { Position = Add(p.Entity.Position, corner) }).ToArray(),
            template.Footprint.Translate(corner), template.Walkway(1, ends: false).Translate(corner));
    }

    public static WorldBox Walkway(SpatialSnapshot map, ResourceRow row) => Checked(map, row).Walkway(row.Cells, ends: true).Translate(row.Origin);

    /// <summary>Tiles of every planned entity plus the row walkway; power links and later rows must leave them free.</summary>
    public static IReadOnlyList<WorldBox> Reservation(SpatialSnapshot map, ResourceRow row)
    {
        Template template = Checked(map, row);
        return Enumerable.Range(0, row.Cells).SelectMany(i => template.Parts.Select(p => template.Tiles(p).Translate(template.Corner(row.Origin, i))))
            .Append(template.Walkway(row.Cells, ends: true).Translate(row.Origin)).ToArray();
    }

    /// <summary>
    /// Adds reserved areas that block buildings but not the character, so rows may share walkways but never build on them.
    /// </summary>
    public static SpatialSnapshot Reserve(SpatialSnapshot map, IEnumerable<WorldBox> boxes, string buildingItem)
    {
        EntityGeometry building = Geometry(map, buildingItem);
        var mask = building.Mask with
        {
            Layers = building.Mask.Layers.Except(map.Prototypes[map.Actor.Name].Mask.Layers, StringComparer.Ordinal).ToArray()
        };
        int first = map.Entities.Count(e => e.Name == ReservationName);
        var reserved = boxes.Select((box, i) => new SpatialEntity($"{ReservationName}:{first + i}", ReservationName, Center(box), box, 0, "planned")).ToArray();
        if (reserved.Length == 0) return map;
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                [ReservationName] = building with { Name = ReservationName, Type = "reservation", Mask = mask }
            },
            Entities = [.. map.Entities, .. reserved]
        };
    }

    /// <summary>A mined product gets a miner; a single-ore enabled smelting recipe gets a smelter; anything else is unsupported.</summary>
    public static ResourceSupply? Supply(ProductionCatalog catalog, string product)
    {
        if (Mined(catalog, product)) return new("miner", product, product);
        var recipe = catalog.Recipes.Where(r => r.Enabled && r.Products.Count == 1 && r.Products[0].Name == product && r.Products[0].DeterministicItem
                && r.Ingredients.Count == 1 && r.Ingredients[0].DeterministicItem && Mined(catalog, r.Ingredients[0].Name)
                && catalog.Machines.Values.Any(m => m.Categories.ContainsKey(r.Category)))
            .OrderBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault();
        return recipe is null ? null : new("smelter", product, recipe.Ingredients[0].Name, recipe);
    }

    /// <summary>
    /// Best obtainable equipment: electric drills when poles exist, the fastest compatible burner furnace, iron chests when enabled.
    /// Smelters need an electric output inserter and pole; without them only burner miners remain possible.
    /// </summary>
    public static ResourceCellEquipment? Equipment(ProductionCatalog catalog, SpatialSnapshot map, ResourceSupply supply,
        IReadOnlyDictionary<string, long> carried)
    {
        bool Available(string item) => map.Items.TryGetValue(item, out var placeable) && map.Prototypes.ContainsKey(placeable.EntityName)
            && (carried.GetValueOrDefault(item) > 0 || catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item)));
        string? pole = Available("small-electric-pole") ? "small-electric-pole" : null;
        string? inserter = Available("inserter") ? "inserter" : null;
        string chest = Available("iron-chest") ? "iron-chest" : "wooden-chest";
        string? furnace = null;
        if (supply.Recipe is { } recipe)
        {
            if (pole is null || inserter is null) return null;
            furnace = catalog.Machines.Where(m => m.Value.Categories.ContainsKey(recipe.Category) && Available(m.Key))
                .OrderByDescending(m => m.Value.CraftingSpeed).ThenBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key).FirstOrDefault();
            if (furnace is null) return null;
        }
        var categories = map.Prototypes.Values.Where(p => p.Type == "resource" && p.ResourceCategory is not null && Yields(catalog, p.Name, supply.Resource))
            .Select(p => p.ResourceCategory!).ToHashSet(StringComparer.Ordinal);
        string? drill = catalog.Items.Where(p => p.Value.PlaceEntityType == "mining-drill" && Available(p.Key))
            .Select(p => (Item: p.Key, Geometry: map.Prototypes[map.Items[p.Key].EntityName]))
            .Where(d => ExtractionPlanner.SupportsSolidOutput(d.Geometry) && (!d.Geometry.IsElectric || pole is not null)
                && (categories.Count == 0 || categories.Any(c => d.Geometry.ResourceCategories?.ContainsKey(c) == true)))
            .OrderByDescending(d => d.Geometry.IsElectric).ThenByDescending(d => d.Geometry.MiningSpeed ?? 0).ThenBy(d => d.Item, StringComparer.Ordinal)
            .Select(d => d.Item).FirstOrDefault();
        if (drill is null) return null;
        bool electric = Geometry(map, drill).IsElectric;
        return new(drill, chest, furnace, supply.Recipe is null ? null : inserter, electric || supply.Recipe is not null ? pole : null);
    }

    /// <summary>Whether a deposit yielding the resource was observed, which native cell sizing requires.</summary>
    public static bool Observed(SpatialSnapshot map, ProductionCatalog catalog, string resource) =>
        map.Prototypes.Values.Any(p => p.Type == "resource" && p.MiningTime is > 0 && Yields(catalog, p.Name, resource));

    /// <summary>Products per minute of one cell: the slowest of drill, furnace and one basic output inserter.</summary>
    public static double CellPerMinute(SpatialSnapshot map, ProductionCatalog catalog, ResourceSupply supply, ResourceCellEquipment equipment)
    {
        EntityGeometry drill = Geometry(map, equipment.Drill);
        double speed = drill.MiningSpeed is { } s && s > 0 && double.IsFinite(s) ? s
            : throw new InvalidDataException("Missing native drill mining speed.");
        double[] ore = map.Prototypes.Values.Where(p => p.Type == "resource" && p.MiningTime is > 0 && Yields(catalog, p.Name, supply.Resource))
            .Select(p => 60 * speed / p.MiningTime!.Value * catalog.Mining[p.Name][0].Amount!.Value).ToArray();
        if (ore.Length == 0) throw new InvalidDataException($"Observe a {supply.Resource} deposit before sizing its cells.");
        if (supply.Recipe is not { } recipe) return ore.Min();
        NativeFurnace furnace = catalog.Machines[equipment.Furnace ?? throw new InvalidDataException("Smelter cells need a furnace.")];
        double products = recipe.Products[0].Amount!.Value, inputs = recipe.Ingredients[0].Amount!.Value;
        return Math.Min(Math.Min(ore.Min() * products / inputs, 60 * furnace.CraftingSpeed / recipe.EnergySeconds * products),
            60 * AutomationPlanner.InserterItemsPerSecond);
    }

    /// <summary>Whether a planned, not yet built cell still fits the observed ground and deposits.</summary>
    public bool Fits(SpatialSnapshot map, ProductionCatalog catalog, ResourceRow row, int index)
    {
        if (index < 0 || index >= row.Cells) throw new ArgumentOutOfRangeException(nameof(index));
        Template? template = Build(map, row.Equipment, row.Direction);
        var supply = Supply(catalog, row.Product);
        if (template?.Pitch != row.Pitch || supply?.Resource != row.Resource) return false;
        var cell = new Probe(map, catalog, row.Resource, template).Cell(template.Corner(row.Origin, index));
        return cell is not null && SupplyMinutes([cell], OrePerMinute(supply, row.CellPerMinute)) >= MinimumSupplyMinutes;
    }

    /// <summary>
    /// The longest viable row, then the longest observed ore reserve, then coverage and distance.
    /// Each cell must have ten minutes of planned supply after sharing ore with other planned and observed drills.
    /// Rows are accepted only after an escape proof; the proof budget separates exhaustion from absence.
    /// </summary>
    public ResourceRowSearch Find(SpatialSnapshot map, ProductionCatalog catalog, ResourceSupply supply, ResourceCellEquipment equipment,
        int cells, MapPosition preferred, CancellationToken token = default, int maximumProofs = 64)
    {
        if (cells is < 1 or > MaximumRowCells) throw new ArgumentOutOfRangeException(nameof(cells));
        if (maximumProofs is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumProofs));
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Resource row observations span different scopes.");
        if (!Observed(map, catalog, supply.Resource)) return new(ResourceRowSearchStatus.NoSite);
        double rate = CellPerMinute(map, catalog, supply, equipment);
        double orePerMinute = OrePerMinute(supply, rate);
        var candidates = new List<(ResourceRow Row, Template Template, double Minutes, int Covered, double Distance)>();
        foreach (int direction in new[] { 0, 4, 8, 12 })
        {
            Template? template = Build(map, equipment, direction);
            if (template is null) continue;
            var probe = new Probe(map, catalog, supply.Resource, template);
            var cache = new Dictionary<MapPosition, CellCoverage?>();
            CellCoverage? Cell(MapPosition corner) => cache.TryGetValue(corner, out var known) ? known : cache[corner] = probe.Cell(corner);
            for (int x = (int)Math.Floor(map.Bounds.Min.X) - template.Pitch; x <= map.Bounds.Max.X; x++)
                for (int y = (int)Math.Floor(map.Bounds.Min.Y) - template.Pitch; y <= map.Bounds.Max.Y; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var origin = new MapPosition(x, y);
                    if (!probe.Walkable(template.Frame.Tiles(template.SpanMin - 1, template.Reach + 1, 1, 2).Translate(origin))) continue;
                    var coverage = new List<CellCoverage>();
                    int covered = 0;
                    while (coverage.Count < cells && Cell(template.Corner(origin, coverage.Count)) is { } ore)
                    {
                        coverage.Add(ore);
                        covered += ore.Covered;
                        double minutes = SupplyMinutes(coverage, orePerMinute);
                        if (minutes < MinimumSupplyMinutes) break; // More consumers cannot improve an earlier cell's reserve.
                        if (!probe.Walkable(template.Frame.Tiles(
                            template.SpanMin + coverage.Count * template.Pitch, template.Reach + 1, 1, 2).Translate(origin))) continue;
                        // Keep viable prefixes: the next cell can share too little ore even when the first fits.
                        var row = new ResourceRow(0, supply.Kind, supply.Product, supply.Resource, equipment, origin, direction, template.Pitch, coverage.Count, rate);
                        candidates.Add((row, template, minutes, covered, Center(Area(template, row)).DistanceTo(preferred)));
                    }
                }
        }
        int proofs = 0;
        foreach (var candidate in candidates.OrderByDescending(c => c.Row.Cells).ThenByDescending(c => c.Minutes)
            .ThenByDescending(c => c.Covered).ThenBy(c => c.Distance)
            .ThenBy(c => c.Row.Direction).ThenBy(c => c.Row.Origin.Y).ThenBy(c => c.Row.Origin.X))
        {
            token.ThrowIfCancellationRequested();
            if (proofs++ >= maximumProofs) return new(ResourceRowSearchStatus.SearchBudgetExhausted);
            var reserved = Reservation(map, candidate.Row);
            var clearance = map.Entities.Where(e => e.Id != map.Actor.Id && FactoryZonePlanner.Removable.Contains(map.Prototypes[e.Name].Type)
                && reserved.Any(box => box.Overlaps(e.Bounds))).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
            if (Escapes(map, candidate.Row, candidate.Template, clearance, token)) return new(ResourceRowSearchStatus.Found, candidate.Row, clearance);
        }
        return new(ResourceRowSearchStatus.NoSite);
    }

    private static double OrePerMinute(ResourceSupply supply, double rate) => supply.Recipe is { } recipe
        ? rate * recipe.Ingredients[0].Amount!.Value / recipe.Products[0].Amount!.Value : rate;

    private static double SupplyMinutes(IReadOnlyList<CellCoverage> cells, double orePerMinute)
    {
        var users = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cell in cells)
            foreach (string id in cell.Ore.Keys) users[id] = users.GetValueOrDefault(id) + 1;
        return cells.Min(c => c.Ore.Sum(p => p.Value.Units / (users[p.Key] + p.Value.CompetingDrills))) / orePerMinute;
    }

    private static Template? Build(SpatialSnapshot map, ResourceCellEquipment equipment, int direction)
    {
        if (direction is not (0 or 4 or 8 or 12)) throw new ArgumentOutOfRangeException(nameof(direction));
        EntityGeometry drill = Geometry(map, equipment.Drill);
        EntityGeometry chest = Geometry(map, equipment.Chest);
        if (!ExtractionPlanner.SupportsSolidOutput(drill) || drill.MiningRadius is not > 0 || chest.Type != "container"
            || chest.TileWidth != 1 || chest.TileHeight != 1) return null;
        int w = direction % 8 == 0 ? drill.TileWidth : drill.TileHeight, h = direction % 8 == 0 ? drill.TileHeight : drill.TileWidth;
        // Positions use the engine's 1/256-tile grid, exactly as ExtractionPlanner solves native drill outputs.
        MapPosition vector = ExtractionPlanner.Rotate(drill.MiningOutput!, direction);
        int tx = (int)Math.Floor(w / 2.0 + Math.Truncate(vector.X * 256) / 256), ty = (int)Math.Floor(h / 2.0 + Math.Truncate(vector.Y * 256) / 256);
        bool acrossX = tx >= 0 && tx < w, acrossY = ty >= 0 && ty < h;
        Frame? frame = acrossX && ty < 0 ? new(w, h, 0, -1) : acrossX && ty >= h ? new(w, h, 0, 1)
            : acrossY && tx < 0 ? new(w, h, -1, 0) : acrossY && tx >= w ? new(w, h, 1, 0) : null;
        if (frame is null) return null; // The output lands inside the drill or past a corner: no receiver row can face it.
        var (dropA, dropB) = frame.Local(tx, ty);
        var drillPart = new Part(new("drill", equipment.Drill, new(w / 2.0, h / 2.0), direction), 0, -frame.Depth, frame.Span, frame.Depth);
        if (equipment.Furnace is null) return Complete(map, equipment, frame, [drillPart, Unit(frame, "output-chest", equipment.Chest, dropA, dropB, 0)], drill, null);
        if (equipment.Inserter is null) return null;
        EntityGeometry furnace = Geometry(map, equipment.Furnace), arm = Geometry(map, equipment.Inserter);
        if (furnace.Type != "furnace" || arm.Type != "inserter" || arm.InserterPickup is null || arm.InserterDrop is null
            || arm.TileWidth != 1 || arm.TileHeight != 1) return null;
        int furnaceSpan = frame.Vertical ? furnace.TileWidth : furnace.TileHeight, furnaceDepth = frame.Vertical ? furnace.TileHeight : furnace.TileWidth;
        // Keep the furnace within the drill's width when possible: overhangs widen every cell of the row.
        foreach (int start in Enumerable.Range(dropA - furnaceSpan + 1, furnaceSpan)
            .OrderBy(s => s >= 0 && s + furnaceSpan <= frame.Span ? 0 : 1).ThenBy(s => s))
        {
            var furnaceTiles = frame.Tiles(start, dropB, furnaceSpan, furnaceDepth);
            var furnacePart = new Part(new("furnace", equipment.Furnace, Center(furnaceTiles), 0), start, dropB, furnaceSpan, furnaceDepth);
            WorldBox furnaceBox = furnace.CollisionBox.Translate(furnacePart.Entity.Position);
            int armB = dropB + furnaceDepth;
            for (int a = start; a < start + furnaceSpan; a++)
            {
                MapPosition at = Center(frame.Tiles(a, armB, 1, 1));
                for (int armDirection = 0; armDirection < 16; armDirection += 4)
                {
                    MapPosition pickup = Add(ExtractionPlanner.Rotate(arm.InserterPickup, armDirection), at);
                    MapPosition drop = Add(ExtractionPlanner.Rotate(arm.InserterDrop, armDirection), at);
                    if (!furnaceBox.Contains(pickup)) continue;
                    var (chestA, chestB) = frame.Local((int)Math.Floor(drop.X), (int)Math.Floor(drop.Y));
                    if (chestB <= armB) continue; // The chest must face the walkway, beyond the arm.
                    var template = Complete(map, equipment, frame, [drillPart, furnacePart,
                        Unit(frame, "output-inserter", equipment.Inserter, a, armB, armDirection),
                        Unit(frame, "output-chest", equipment.Chest, chestA, chestB, 0)], drill, arm);
                    if (template is not null) return template;
                }
            }
        }
        return null;
    }

    private static Template? Complete(SpatialSnapshot map, ResourceCellEquipment equipment, Frame frame, List<Part> parts,
        EntityGeometry drill, EntityGeometry? arm)
    {
        var occupied = new HashSet<(int, int)>();
        foreach (var part in parts)
            for (int a = part.A; a < part.A + part.SpanA; a++)
                for (int b = part.B; b < part.B + part.SpanB; b++)
                    if (!occupied.Add((a, b))) return null;
        int min = parts.Min(p => p.A), max = parts.Max(p => p.A + p.SpanA), reach = parts.Max(p => p.B + p.SpanB - 1);
        if (drill.IsElectric || arm?.IsElectric == true)
        {
            if (equipment.Pole is null) return null;
            EntityGeometry pole = Geometry(map, equipment.Pole);
            if (pole.Type != "electric-pole" || pole.TileWidth != 1 || pole.TileHeight != 1 || pole.SupplyArea is not > 0 || pole.MaxWireDistance is not > 0)
                return null;
            var consumers = parts.Where(p => p.Entity.Role == "drill" ? drill.IsElectric : p.Entity.Role == "output-inserter" && arm!.IsElectric)
                .Select(p => Geometry(map, p.Entity.Item).CollisionBox.Rotate(p.Entity.Direction).Translate(p.Entity.Position)).ToArray();
            // Stay within the cell's width first; one more column widens every cell of the row.
            int[][] groups = [Enumerable.Range(min, max - min).ToArray(), [max], [min - 1]];
            Part? chosen = groups.SelectMany(columns => Enumerable.Range(-frame.Depth, reach + frame.Depth + 1)
                    .SelectMany(b => columns.Select(a => (A: a, B: b))))
                .Where(t => !occupied.Contains(t) && consumers.All(box => PowerGridPlanner.Supplies(Center(frame.Tiles(t.A, t.B, 1, 1)), pole, box)))
                .Select(t => Unit(frame, "pole", equipment.Pole, t.A, t.B, 0)).FirstOrDefault();
            if (chosen is null) return null;
            parts = [.. parts, chosen];
            min = Math.Min(min, chosen.A);
            max = Math.Max(max, chosen.A + 1);
            if (max - min > pole.MaxWireDistance) return null; // Neighbouring cell poles must join by wire.
        }
        return new(frame, parts, min, max - min, reach);
    }

    private static Part Unit(Frame frame, string role, string item, int a, int b, int direction) =>
        new(new(role, item, Center(frame.Tiles(a, b, 1, 1)), direction), a, b, 1, 1);

    private static bool Escapes(SpatialSnapshot map, ResourceRow row, Template template, IReadOnlyList<SpatialEntity> clearance,
        CancellationToken token)
    {
        var cleared = clearance.Select(e => e.Id).Append(map.Actor.Id).ToHashSet(StringComparer.Ordinal);
        var planned = Enumerable.Range(0, row.Cells).SelectMany(i => template.Parts.Select(p =>
        {
            EntityGeometry geometry = Geometry(map, p.Entity.Item);
            var position = Add(p.Entity.Position, template.Corner(row.Origin, i));
            return new SpatialEntity($"planned:{i}:{p.Entity.Role}", geometry.Name, position,
                geometry.CollisionBox.Rotate(p.Entity.Direction).Translate(position), p.Entity.Direction, "planned");
        }));
        var start = Center(template.Frame.Tiles(template.SpanMin, template.Reach + 1, 1, 1).Translate(row.Origin));
        var future = map with
        {
            Actor = map.Actor with { Position = start },
            Entities = [.. map.Entities.Where(e => !cleared.Contains(e.Id)), .. planned]
        };
        return PlacementPlanner.CanEscape(new SpatialCollisionField(future), Area(template, row), token);
    }

    private static WorldBox Area(Template template, ResourceRow row) => template.Frame.Tiles(template.SpanMin - 1, -template.Frame.Depth,
        row.Cells * template.Pitch + 2, template.Frame.Depth + template.Reach + 3).Translate(row.Origin);

    /// <summary>Validity of one cell against ground without the actor or removable obstacles, which are cleared first.</summary>
    private sealed class Probe
    {
        private readonly SpatialSnapshot map;
        private readonly SpatialCollisionField field;
        private readonly Template template;
        private readonly EntityGeometry drill;
        private readonly Dictionary<(int, int), List<(SpatialEntity Deposit, bool Target)>> deposits = [];
        private readonly Dictionary<string, OreReserve> reserves = new(StringComparer.Ordinal);

        public Probe(SpatialSnapshot map, ProductionCatalog catalog, string resource, Template template)
        {
            this.map = map;
            this.template = template;
            drill = Geometry(map, template.Parts[0].Entity.Item);
            field = new(map with
            {
                Entities = map.Entities.Where(e => e.Id != map.Actor.Id && !FactoryZonePlanner.Removable.Contains(map.Prototypes[e.Name].Type)).ToArray()
            });
            var consumers = map.Entities.Where(e => map.Prototypes[e.Name] is { Type: "mining-drill", MiningRadius: > 0 })
                .Select(e => (Entity: e, Geometry: map.Prototypes[e.Name])).ToArray();
            foreach (var e in map.Entities.Where(e => e.Amount is > 0 && double.IsFinite(e.Amount.Value)
                && map.Prototypes[e.Name].ResourceCategory is { } category
                && drill.ResourceCategories!.ContainsKey(category)))
            {
                var key = ((int)Math.Floor(e.Position.X), (int)Math.Floor(e.Position.Y));
                if (!deposits.TryGetValue(key, out var list)) deposits[key] = list = [];
                bool target = Yields(catalog, e.Name, resource);
                list.Add((e, target));
                if (!target) continue;
                double competing = consumers.Where(c => c.Geometry.ResourceCategories?.ContainsKey(map.Prototypes[e.Name].ResourceCategory!) == true
                    && MiningArea(c.Entity.Position, c.Geometry.MiningRadius!.Value).Contains(e.Position))
                    .Sum(c => c.Geometry.MiningSpeed is > 0 && double.IsFinite(c.Geometry.MiningSpeed.Value)
                        ? c.Geometry.MiningSpeed.Value / drill.MiningSpeed!.Value : double.PositiveInfinity);
                reserves[e.Id] = new(e.Amount!.Value * catalog.Mining[e.Name][0].Amount!.Value, competing);
            }
        }

        /// <summary>Wanted deposits under the drill, or null if the cell cannot stand at this drill corner.</summary>
        public CellCoverage? Cell(MapPosition corner)
        {
            MapPosition center = Add(template.Parts[0].Entity.Position, corner);
            double radius = drill.MiningRadius!.Value;
            var area = MiningArea(center, radius);
            if (!map.Bounds.Contains(area)) return null;
            int covered = 0;
            var ore = new Dictionary<string, OreReserve>(StringComparer.Ordinal);
            for (int x = (int)Math.Floor(area.Min.X) - 1; x <= area.Max.X; x++)
                for (int y = (int)Math.Floor(area.Min.Y) - 1; y <= area.Max.Y; y++)
                    foreach (var (deposit, target) in deposits.GetValueOrDefault((x, y)) ?? [])
                    {
                        if (!area.Overlaps(deposit.Bounds)) continue;
                        if (!target) return null; // A foreign deposit would mix into the receiver.
                        covered++;
                        if (area.Contains(deposit.Position)) ore[deposit.Id] = reserves[deposit.Id];
                    }
            if (ore.Count == 0) return null;
            foreach (var part in template.Parts)
                if (!field.PlacementClear(Geometry(map, part.Entity.Item), Add(part.Entity.Position, corner), part.Entity.Direction)) return null;
            return Walkable(template.Walkway(1, ends: false).Translate(corner)) ? new(covered, ore) : null;
        }

        public bool Walkable(WorldBox tiles)
        {
            for (double x = tiles.Min.X + .5; x < tiles.Max.X; x++)
                for (double y = tiles.Min.Y + .5; y < tiles.Max.Y; y++)
                    if (!field.Walkable(new(x, y))) return false;
            return true;
        }
    }

    private static WorldBox MiningArea(MapPosition center, double radius) =>
        new(new(center.X - radius, center.Y - radius), new(center.X + radius, center.Y + radius));

    private static Template Checked(SpatialSnapshot map, ResourceRow row)
    {
        Template template = Build(map, row.Equipment, row.Direction)
            ?? throw new InvalidDataException("The row's native drill output no longer forms a cell.");
        return template.Pitch == row.Pitch ? template : throw new InvalidDataException("Native geometry changed since the resource row was planned.");
    }

    private static bool Mined(ProductionCatalog catalog, string item) => catalog.Mining.Any(p =>
        catalog.MiningSourceTypes?.GetValueOrDefault(p.Key) == "resource" && Yields(catalog, p.Key, item));

    private static bool Yields(ProductionCatalog catalog, string deposit, string item) => catalog.Mining.TryGetValue(deposit, out var products)
        && products.Length == 1 && products[0].Name == item && products[0].DeterministicItem;

    private static EntityGeometry Geometry(SpatialSnapshot map, string item) =>
        map.Items.TryGetValue(item, out var placeable) && map.Prototypes.TryGetValue(placeable.EntityName, out var geometry)
            ? geometry : throw new ArgumentException($"Request native geometry for {item} before planning.", nameof(item));

    private static MapPosition Center(WorldBox box) => new((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2);
    private static MapPosition Add(MapPosition a, MapPosition b) => new(a.X + b.X, a.Y + b.Y);
}

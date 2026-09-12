using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

public sealed class NativeArrayConverter<T> : JsonConverter<IReadOnlyList<T>>
{
    public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            return Array.AsReadOnly(JsonSerializer.Deserialize<T[]>(ref reader, options) ?? []);
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.EnumerateObject().Any())
            return Array.Empty<T>();
        throw new JsonException("Expected a native array or empty Lua table.");
    }
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}

public sealed record WorldBox(MapPosition Min, MapPosition Max)
{
    [JsonIgnore] public double Width => Max.X - Min.X;
    [JsonIgnore] public double Height => Max.Y - Min.Y;
    public bool Contains(MapPosition point) => point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y;
    public bool Contains(WorldBox other) => Contains(other.Min) && Contains(other.Max);
    public bool Overlaps(WorldBox other) => Min.X < other.Max.X && Max.X > other.Min.X && Min.Y < other.Max.Y && Max.Y > other.Min.Y;
    public WorldBox Translate(MapPosition origin) => new(new(Min.X + origin.X, Min.Y + origin.Y), new(Max.X + origin.X, Max.Y + origin.Y));
    public WorldBox Rotate(int direction) => direction switch
    {
        0 => this,
        4 => new(new(-Max.Y, Min.X), new(-Min.Y, Max.X)),
        8 => new(new(-Max.X, -Max.Y), new(-Min.X, -Min.Y)),
        12 => new(new(Min.Y, -Max.X), new(Max.Y, -Min.X)),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };
}

public sealed record CollisionMask(
    [property: JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string> Layers,
    bool TilesOnly, bool IgnoreSameMask, bool TileTransitions)
{
    public bool CollidesWith(CollisionMask other, bool tile)
    {
        if (!tile && (TilesOnly || other.TilesOnly)) return false;
        if (!Layers.Intersect(other.Layers, StringComparer.Ordinal).Any()) return false;
        return tile || !(IgnoreSameMask && other.IgnoreSameMask
            && Layers.Count == other.Layers.Count && Layers.All(other.Layers.Contains));
    }
}

public sealed record EntityGeometry(string Name, string Type, WorldBox CollisionBox, CollisionMask Mask, int TileWidth, int TileHeight,
    double? MiningRadius = null, MapPosition? MiningOutput = null, string? ResourceCategory = null,
    IReadOnlyDictionary<string, bool>? ResourceCategories = null, IReadOnlyDictionary<string, bool>? FuelCategories = null,
    [property: JsonConverter(typeof(NativeArrayConverter<FluidBoxGeometry>))] IReadOnlyList<FluidBoxGeometry>? FluidBoxes = null,
    MapPosition? FluidSourceOffset = null,
    [property: JsonConverter(typeof(NativeArrayConverter<TileBuildRule>))] IReadOnlyList<TileBuildRule>? TileBuildability = null,
    double? SupplyArea = null, double? MaxWireDistance = null, bool IsElectric = false,
    MapPosition? InserterPickup = null, MapPosition? InserterDrop = null, double? BeltSpeed = null,
    double? MiningSpeed = null, double? MiningTime = null, double? EnergyPerTick = null, double? BurnerEffectivity = null);
public sealed record FluidBoxGeometry(int Index, string ProductionType,
    [property: JsonConverter(typeof(NativeArrayConverter<FluidPortGeometry>))] IReadOnlyList<FluidPortGeometry> Connections,
    string? Filter = null, double? MinimumTemperature = null, double? MaximumTemperature = null);
public sealed record FluidPortGeometry(int Index, string Type, int Direction, string FlowDirection,
    [property: JsonConverter(typeof(NativeArrayConverter<MapPosition>))] IReadOnlyList<MapPosition> Positions,
    [property: JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string> Categories);
public sealed record TileBuildRule(WorldBox Area, CollisionMask CollidingTiles, CollisionMask RequiredTiles);
public sealed record SpatialActor(string Id, string Name, MapPosition Position, double BuildDistance, double ReachDistance, string ControlMode);
public sealed record TileRun(int X, int Y, int Length, string Name);
public sealed record SpatialEntity(string Id, string Name, MapPosition Position, WorldBox Bounds, int Direction, string Force, double? Amount = null,
    MapPosition? DropPosition = null, string? DropTargetId = null,
    [property: JsonConverter(typeof(NativeArrayConverter<ObservedFluidConnection>))] IReadOnlyList<ObservedFluidConnection>? FluidConnections = null,
    ObservedPower? Power = null, double BoundsOrientation = 0, MapPosition? PickupPosition = null, string? PickupTargetId = null,
    ObservedBeltConnections? BeltConnections = null, string? Status = null);
public sealed record ObservedBeltConnections(
    [property: JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string> Inputs,
    [property: JsonConverter(typeof(NativeArrayConverter<string>))] IReadOnlyList<string> Outputs);
public sealed record ObservedFluidConnection(int BoxIndex, int PortIndex, MapPosition Position, MapPosition TargetPosition,
    string? TargetEntityId = null, int? TargetBoxIndex = null,
    string? Type = null, string? FlowDirection = null, string? Filter = null);
public sealed record ObservedPower(double Energy, long? NetworkId = null, double? GeneratedLastTick = null);
public sealed record PlaceableItem(string EntityName, int StackSize);
public sealed record SpatialCoverage(bool Atomic, bool Complete, string Visibility, int Radius);
public sealed record SpatialSnapshot(ActorScope Scope, long CollectedTick, int SurfaceIndex, WorldBox Bounds, SpatialActor Actor,
    IReadOnlyDictionary<string, EntityGeometry> Prototypes, IReadOnlyDictionary<string, CollisionMask> TilePrototypes,
    [property: JsonConverter(typeof(NativeArrayConverter<TileRun>))] IReadOnlyList<TileRun> Rows,
    [property: JsonConverter(typeof(NativeArrayConverter<SpatialEntity>))] IReadOnlyList<SpatialEntity> Entities,
    IReadOnlyDictionary<string, PlaceableItem> Items, SpatialCoverage Coverage,
    IReadOnlyDictionary<string, string>? TileFluids = null)
{
    public static SpatialSnapshot Parse(GameResponse response)
    {
        if (!response.Ok) throw new GameRpcException(response.Error ?? new("invalid_response", "Spatial observation failed."));
        try
        {
            SpatialSnapshot map = response.Data.Deserialize<SpatialSnapshot>(Protocol.Json)
                ?? throw new InvalidDataException("Missing spatial observation.");
            if (map.CollectedTick != response.Tick || !map.Coverage.Atomic || !map.Coverage.Complete
                || map.Coverage.Visibility != "current-character-local-area" || !ValidBox(map.Bounds)
                || map.Bounds.Width is < 1 or > 97 || map.Bounds.Height is < 1 or > 97
                || !map.Bounds.Contains(map.Actor.Position) || !map.Prototypes.ContainsKey(map.Actor.Name)
                || map.Actor.ControlMode is not ("ai" or "manual")
                || !double.IsFinite(map.Actor.BuildDistance) || map.Actor.BuildDistance <= 0
                || !double.IsFinite(map.Actor.ReachDistance) || map.Actor.ReachDistance <= 0)
                throw new InvalidDataException("Incomplete or inconsistent spatial observation.");
            foreach (EntityGeometry geometry in map.Prototypes.Values)
                if (!ValidBox(geometry.CollisionBox) || geometry.TileWidth < 0 || geometry.TileHeight < 0)
                    throw new InvalidDataException("Invalid native prototype geometry.");
            if (map.Entities.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != map.Entities.Count
                || map.Entities.Any(e => !ValidBox(e.Bounds) || !map.Prototypes.ContainsKey(e.Name)))
                throw new InvalidDataException("Invalid spatial entity identity or shape.");
            return map;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Invalid native spatial data.", error);
        }
    }

    private static bool ValidBox(WorldBox box) => double.IsFinite(box.Min.X) && double.IsFinite(box.Min.Y)
        && double.IsFinite(box.Max.X) && double.IsFinite(box.Max.Y) && box.Min.X <= box.Max.X && box.Min.Y <= box.Max.Y;
}

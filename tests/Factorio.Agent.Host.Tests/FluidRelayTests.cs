using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidRelayTests
{
    [Fact]
    public void ARelayKeepsItsCandidateBudgetForUsableOrientationsBeyondAnUnrelatedNetwork()
    {
        var map = Map();
        var blocked = new List<SpatialEntity>();
        for (int x = -1; x <= 9; x++)
            for (int y = -5; y <= 5; y++)
                if ((x + y) % 2 == 0) blocked.Add(Pipe(map, $"gas:{x}:{y}", new(x + .5, y + .5), "petroleum-gas"));
        map = map with { Actor = map.Actor with { Position = new(-20.5, 5.5) }, Entities = [.. map.Entities, .. blocked] };
        var site = new FluidRelayPlanner().Find(map, "pipe", "source", "water", new(130.5, .5));
        Assert.NotNull(site);
        Assert.InRange(site.Route.Pipes.Count, 1, FluidRelayPlanner.MaximumPipes);
        Assert.All(site.Route.Pipes.Append(site.Outlet.Position), p => Assert.DoesNotContain(blocked,
            other => other.FluidConnections!.Any(port => port.TargetPosition == p)));
        Assert.True(site.Outlet.Position.Y is < -4.5 or > 5.5 || site.Outlet.Position.X is < -.5 or > 9.5);
    }

    [Fact]
    public void ARelayAdvancesTowardADistantConsumerWithinObservedGroundAndItsPipeBudget()
    {
        var map = Map();
        var destination = new MapPosition(130.5, .5);
        var site = new FluidRelayPlanner().Find(map, "pipe", "source", "water", destination);
        Assert.NotNull(site);
        Assert.InRange(site.Outlet.Position.DistanceTo(destination), 0, new MapPosition(-20.5, .5).DistanceTo(destination) - 8);
        Assert.InRange(site.Route.Pipes.Count, 1, FluidRelayPlanner.MaximumPipes);
        Assert.All(site.Route.Pipes, p => Assert.True(map.Bounds.Contains(p)));
        Assert.Equal("source", site.Route.Source!.EntityId);
        Assert.Equal(FluidRelayPlanner.PlannedId, site.Route.Target!.EntityId);
    }

    [Fact]
    public void ARelayAvoidsOccupiedSitesAndAnUnrelatedFluidNetwork()
    {
        var map = Map();
        var at = new MapPosition(3.5, .5);
        var foreign = Pipe(map, "foreign", new(1.5, 1.5), "petroleum-gas");
        map = map with { Entities = [.. map.Entities, foreign, OilMaps.Wall("wall", at)] };
        var site = new FluidRelayPlanner().Find(map, "pipe", "source", "water", new(130.5, .5));
        Assert.NotNull(site);
        Assert.NotEqual(at, site.Outlet.Position);
        Assert.All(site.Route.Pipes, p => Assert.DoesNotContain(foreign.FluidConnections!, port => port.TargetPosition == p));
        Assert.DoesNotContain(site.Outlet.Position, foreign.FluidConnections!.Select(p => p.TargetPosition));
    }

    [Fact]
    public void ASourceFilteredToAnotherFluidCannotStartARelay()
    {
        var map = Map();
        map = map with { Entities = map.Entities.Select(e => e.Id == "source" ? e with
            { FluidConnections = e.FluidConnections!.Select(p => p with { Filter = "petroleum-gas" }).ToArray() } : e).ToArray() };
        Assert.Null(new FluidRelayPlanner().Find(map, "pipe", "source", "water", new(130.5, .5)));
    }

    [Fact]
    public void AppliedButUnrecordedPipeReceiptsAreAdoptedAtTheirOriginalPlan()
    {
        var map = Map();
        var planned = new MapPosition(3.5, .5);
        var cell = new FactoryCell("link", 0, new(0, 0, true), FluidRelayController.Kind, "pipe", "water",
            new Dictionary<string, string>(), "building", 1, Plan: new Dictionary<string, PlannedEntity>
                { ["outlet"] = new("outlet", "pipe", planned, 0), ["pipe-0"] = new("pipe-0", "pipe", new(2.5, .5), 0) });
        map = map with { Entities = [.. map.Entities, Pipe(map, "applied", planned, "water"), Pipe(map, "unrelated", new(9.5, .5), "water")] };
        var adopted = FluidRelayController.ObserveParts(map, cell);
        Assert.Equal("applied", adopted["outlet"]);
        Assert.Single(adopted);
        FluidRelayController.RequireIsolated(map, adopted.Values.ToHashSet(), "source");
        map = map with { Entities = map.Entities.Select(e => e.Id == "applied" ? e with
            { FluidConnections = e.FluidConnections!.Select(p => p with { TargetEntityId = "unrelated" }).ToArray() } : e).ToArray() };
        Assert.Throws<InvalidDataException>(() => FluidRelayController.RequireIsolated(map, adopted.Values.ToHashSet(), "source"));
    }

    [Fact]
    public void ARegisteredDownstreamSectionDoesNotPreventRepairOfItsSourceSection()
    {
        var map = Map();
        var source = map.Entities.Single(e => e.Id == "source");
        var port = source.FluidConnections!.Single(p => p.TargetPosition == new MapPosition(-19.5, .5));
        source = source with { FluidConnections = source.FluidConnections!.Select(p => p == port ? p with
            { TargetEntityId = "downstream", TargetBoxIndex = 1 } : p).ToArray() };
        map = map with { Entities = [source, Pipe(map, "downstream", port.TargetPosition, "water")] };
        var cell = new FactoryCell("next", 0, new(0, 0, true), FluidRelayController.Kind, "pipe", "water",
            new Dictionary<string, string>(), "ready", 1, Plan: new Dictionary<string, PlannedEntity>
                { ["pipe-0"] = new("pipe-0", "pipe", port.TargetPosition, 0) },
            FluidRoute: new(PipeRouteStatus.Found, new("old-source-id", port.BoxIndex, port.PortIndex, port.Position, port.TargetPosition), null, [], 0));
        var parts = new HashSet<string> { "source" };
        var state = new FactoryState(1, map.Scope.WorldId, [], [cell]);
        var allowed = FluidRelayController.RegisteredDownstreamParts(map, parts, state, "water");
        FluidRelayController.RequireIsolated(map, parts, "upstream", allowed);
        Assert.Throws<InvalidDataException>(() => FluidRelayController.RequireIsolated(map, parts, "upstream",
            FluidRelayController.RegisteredDownstreamParts(map, parts, state, "oil")));
        var displaced = cell with { Plan = new Dictionary<string, PlannedEntity>
            { ["pipe-0"] = new("pipe-0", "pipe", new(-18.5, .5), 0) } };
        Assert.Throws<InvalidDataException>(() => FluidRelayController.RequireIsolated(map, parts, "upstream",
            FluidRelayController.RegisteredDownstreamParts(map, parts, state with { Cells = [displaced] }, "water")));
    }

    [Fact]
    public void NativeWaterAtARelaySuppliesATerrainRecipeWithoutANewLocalPump()
    {
        var map = Map();
        var stock = new FactorySnapshot("photo", map.Scope, 1, 1, Protocol.ToElement(new { }), [
            new("water", "fluid", "source", "fluid", Protocol.ToElement(new { aggregateSafe = true, contents = new { water = 90 },
                sourceBoxes = new[] { new { entityId = "source", index = 1 } } }))]);
        Assert.Empty(FluidCellBuilder.MissingTerrainSupply(map, stock, ["water"], OilCatalogs.Oil()));
        map = map with { Entities = map.Entities.Select(e => e.Id == "source" ? e with
            { FluidConnections = e.FluidConnections!.Select(p => p with { FlowDirection = "input" }).ToArray() } : e).ToArray() };
        Assert.Equal(["water"], FluidCellBuilder.MissingTerrainSupply(map, stock, ["water"], OilCatalogs.Oil()));
    }

    [Fact]
    public void WaterLoadedInABoilerIsACandidatePendingItsNativePortValidation()
    {
        var map = Map();
        var stock = new FactorySnapshot("photo", map.Scope, 1, 1, Protocol.ToElement(new { }), [
            Entity("boiler", "boiler", new(120, 0)), Entity("pump", "offshore-pump", new(-20, 0)),
            Fluid("boiler"), Fluid("pump")]);
        Assert.Equal(["boiler", "pump"], FluidRelayController.Sources(stock, "water").Select(s => s.Id));
        static FactoryRecord Entity(string id, string type, MapPosition position) => new(id, "entity", id, id, Protocol.ToElement(new { type, position, role = "factory" }));
        static FactoryRecord Fluid(string id) => new(id + ":fluid", "fluid", id, "fluid", Protocol.ToElement(new { contents = new { water = 90 },
            sourceBoxes = new[] { new { entityId = id, index = 1 } } }));
    }

    [Fact]
    public void AKnownEmptyPumpRemainsACandidateForNativeTerrainIntakeValidation()
    {
        var map = Map();
        var stock = new FactorySnapshot("photo", map.Scope, 1, 1, Protocol.ToElement(new { }), [
            new("pump", "entity", "pump", "offshore-pump", Protocol.ToElement(new { type = "offshore-pump", role = "factory", position = new MapPosition(-20, 0) }))]);
        Assert.Equal("pump", Assert.Single(FluidRelayController.Sources(stock, "water")).Id);
    }

    [Fact]
    public void ARelayWithdrawsOnlyFromTheStockedNativeBoxWithAnUnoccupiedOutlet()
    {
        var map = Map();
        var source = map.Entities.Single(e => e.Id == "source");
        var right = source.FluidConnections!.Single(p => p.TargetPosition.X > p.Position.X);
        var lower = source.FluidConnections!.Single(p => p.TargetPosition.Y > p.Position.Y) with { BoxIndex = 2, Filter = null };
        map = map with { Entities = map.Entities.Select(e => e.Id == source.Id ? e with { FluidConnections = [right, lower] } : e).ToArray() };
        var stock = SourceStock(map, "factory", 2);
        var boxes = FluidRelayController.WithdrawableBoxes(map, stock, source.Id, "water");
        Assert.Equal([2], boxes);
        var site = new FluidRelayPlanner().Find(map, "pipe", source.Id, "water", new(130.5, .5), sourceBoxIndices: boxes);
        Assert.NotNull(site);
        Assert.Equal(2, site.Route.Source!.BoxIndex);
        // The other box is still visible to the safety checks, even though it is not an eligible source.
        Assert.DoesNotContain(right.TargetPosition, site.Route.Pipes);
        map = map with { Entities = map.Entities.Select(e => e.Id == source.Id ? e with
            { FluidConnections = [right, lower with { TargetEntityId = "occupied" }] } : e).ToArray() };
        Assert.Empty(FluidRelayController.WithdrawableBoxes(map, stock, source.Id, "water"));
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("foreign")]
    public void OnlyOwnKnownFactoryEntitiesCanSupplyARelay(string role)
    {
        var map = Map();
        var stock = SourceStock(map, role, 1);
        Assert.Empty(FluidRelayController.Sources(stock, "water"));
        Assert.Empty(FluidRelayController.WithdrawableBoxes(map, stock, "source", "water"));
    }

    [Fact]
    public void SourceBoxValidationRejectsAChangedActorScope()
    {
        var map = Map();
        var stock = SourceStock(map, "factory", 1) with { Scope = map.Scope with { Generation = map.Scope.Generation + 1 } };
        Assert.Throws<InvalidDataException>(() => FluidRelayController.WithdrawableBoxes(map, stock, "source", "water"));
    }

    [Fact]
    public void ADedicatedRelayIntakeAvoidsTheExistingInstallationAndReservedGround()
    {
        var map = ShoreMap();
        var existing = new SpatialEntity("steam-pump", "pump", new(-20.5, .5), new(new(-20.65, .35), new(-20.35, .65)), 0, "agent",
            FluidConnections: FluidCellPlanner.Ports(map.Prototypes["pump"], new(new(-20.5, .5), 0, 0))
                .Select(p => p with { TargetEntityId = "boiler" }).ToArray());
        map = map with { Entities = [.. map.Entities.Where(e => e.Id != "source"), existing] };
        var reserved = new WorldBox(new(-23, 0), new(-14, 11));
        var site = new FluidRelayPlanner().FindOffshore(FactoryGround.Reserve(map, [reserved], "pipe"), "pump", "pipe",
            existing.Position, "water", new(130.5, .5));
        Assert.NotNull(site);
        Assert.NotNull(site.Pump);
        Assert.NotEqual(existing.Position, site.Pump.Placement.Position);
        Assert.DoesNotContain(site.Route.Pipes.Append(site.Outlet.Position).Append(site.Pump.Placement.Position), reserved.Contains);
        Assert.InRange(site.Route.Pipes.Count, 1, FluidRelayPlanner.MaximumPipes);
        var pump = site.Pump.Placement;
        var offset = ExtractionPlanner.Rotate(map.Prototypes["pump"].FluidSourceOffset!, pump.Direction);
        Assert.Equal("water", new SpatialCollisionField(map).FluidAt(new(pump.Position.X + offset.X, pump.Position.Y + offset.Y)));
    }

    [Fact]
    public void ANewRelayPumpNeedsObservedWaterRatherThanOnlyFreeLand()
    {
        var map = ShoreMap();
        map = map with { Rows = map.Rows.Select(r => r with { Name = "grass" }).ToArray() };
        Assert.Null(new FluidRelayPlanner().FindOffshore(map, "pump", "pipe", new(-20.5, .5), "water", new(130.5, .5)));
    }

    [Fact]
    public void APlannedPumpReceiptIsAdoptedOnlyAtItsOriginalOrientation()
    {
        var map = ShoreMap();
        var at = new MapPosition(-20.5, .5);
        var pump = new SpatialEntity("applied", "pump", at, map.Prototypes["pump"].CollisionBox.Translate(at), 0, "agent");
        var cell = new FactoryCell("link", 0, new(0, 0, true), FluidRelayController.Kind, "pipe", "water",
            new Dictionary<string, string>(), "building", 1, Plan: new Dictionary<string, PlannedEntity> { ["pump"] = new("pump", "pump", at, 0) });
        map = map with { Entities = [.. map.Entities, pump] };
        Assert.Equal("applied", FluidRelayController.ObserveParts(map, cell)["pump"]);
        map = map with { Entities = map.Entities.Select(e => e.Id == pump.Id ? e with { Direction = 4 } : e).ToArray() };
        Assert.Empty(FluidRelayController.ObserveParts(map, cell));
    }

    private static FactorySnapshot SourceStock(SpatialSnapshot map, string role, int box) => new("photo", map.Scope, 1, 1,
        Protocol.ToElement(new { }), [
            new("source", "entity", "source", "pipe", Protocol.ToElement(new { role, type = "pipe", position = new MapPosition(-20.5, .5) })),
            new("source:fluid", "fluid", "source", "fluid", Protocol.ToElement(new { aggregateSafe = true, contents = new { water = 90 },
                sourceBoxes = new[] { new { entityId = "source", index = box } } }))]);

    private static SpatialSnapshot ShoreMap()
    {
        var map = Map();
        var native = SteamPowerPlannerTests.Map(true);
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                ["pump"] = native.Prototypes["pump"] with { Type = "offshore-pump" },
                ["pipe"] = map.Prototypes["pipe"] with { Mask = native.Prototypes["boiler"].Mask },
                ["character"] = map.Prototypes["character"] with { Mask = native.Prototypes["boiler"].Mask }
            },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["pump"] = new("pump", 50) },
            Entities = [.. map.Entities, new(map.Actor.Id, "character", map.Actor.Position,
                map.Prototypes["character"].CollisionBox.Translate(map.Actor.Position), 0, "agent")],
            TilePrototypes = native.TilePrototypes,
            TileFluids = native.TileFluids,
            Rows = Enumerable.Range(-48, 96).SelectMany(y => new TileRun[] { new(-48, y, 27, "water"), new(-21, y, 69, "grass") }).ToArray()
        };
    }

    [Fact]
    public void APreviouslyPlacedPipeContainingAnotherFluidIsRejectedBeforeConnection()
    {
        var map = Map();
        var stock = new FactorySnapshot("photo", map.Scope, 1, 1, Protocol.ToElement(new { }), [
            new("other", "fluid", "outlet", "fluid", Protocol.ToElement(new { aggregateSafe = true, contents = new { oil = 90 },
                sourceBoxes = new[] { new { entityId = "outlet", index = 1 } } }))]);
        Assert.Throws<InvalidDataException>(() => FluidRelayController.RequireCompatibleStock(stock, ["outlet"], "water"));
        FluidRelayController.RequireCompatibleStock(stock, ["outlet"], "oil");
    }

    private static SpatialSnapshot Map()
    {
        var map = FactoryMaps.Grass(48);
        var pipe = new EntityGeometry("pipe", "pipe", new(new(-.3, -.3), new(.3, .3)),
            map.Prototypes["iron-chest"].Mask, 1, 1, FluidBoxes: [new(1, "input-output", Enumerable.Range(0, 4).Select(i =>
                new FluidPortGeometry(i + 1, "normal", i * 4, "input-output", [new(0, 0), new(0, 0), new(0, 0), new(0, 0)], ["default"])).ToArray())]);
        map = map with { Actor = map.Actor with { Position = new(-20.5, 3.5) },
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["pipe"] = pipe,
                ["wall"] = new("wall", "wall", new(new(-.3, -.3), new(.3, .3)), pipe.Mask, 1, 1) },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["pipe"] = new("pipe", 100) } };
        return map with { Entities = [.. map.Entities.Where(e => e.Id != "source"), Pipe(map, "source", new(-20.5, .5), "water")] };
    }

    private static SpatialEntity Pipe(SpatialSnapshot map, string id, MapPosition at, string fluid) => new(id, "pipe", at,
        map.Prototypes["pipe"].CollisionBox.Translate(at), 0, "agent", FluidConnections:
            FluidCellPlanner.Ports(map.Prototypes["pipe"], new(at, 0, 0)).Select(p => p with { Filter = fluid }).ToArray());
}

using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PowerFuelTests
{
    [Theory]
    [InlineData(30000, 4000000, 1, 27)]
    [InlineData(30000, 4000000, .5, 27)]
    [InlineData(15000, 4000000, 1, 13.5)]
    [InlineData(30000, 2000000, 1, 54)]
    public void NativeFuelInputAndItemValueDetermineDemand(double usage, double value, double effectivity, double expected)
    {
        var (catalog, cell, power) = Native(usage, value, effectivity);
        Assert.Equal(expected, PowerFuelPolicy.Demand(catalog, cell, "coal", power));
    }

    [Fact]
    public void ARecipeNameCannotReplaceNativeBoilerIdentityAndFuelAcceptance()
    {
        var (catalog, cell, power) = Native();
        Assert.Null(PowerFuelPolicy.Demand(catalog, cell, "iron-plate", power));
        Assert.Null(PowerFuelPolicy.Demand(catalog, cell, "coal", power with { Boilers = [] }));
        Assert.Null(PowerFuelPolicy.Demand(catalog, cell, "coal", power with { Boilers = [power.Boilers[0] with { FuelCategories = null }] }));
        Assert.Null(PowerFuelPolicy.Demand(catalog, cell, "coal", power with { Boilers = [power.Boilers[0] with { FuelCategories = new Dictionary<string, bool> { ["nuclear"] = true } }] }));
        Assert.Throws<InvalidDataException>(() => PowerFuelPolicy.Demand(catalog, cell, "coal", power with { Scope = power.Scope with { Generation = 2 } }));
    }

    [Theory]
    [InlineData(20, true, true)]
    [InlineData(11, true, false)]
    [InlineData(20, false, false)]
    public void AHealthyRatedBeltStillNeedsAStartedBoilerAndAnIgnitionReserve(long coal, bool lit, bool automated)
    {
        var (catalog, cell, _) = Native();
        var snapshot = Reserves(catalog.Scope, coal, lit);
        var covered = new HashSet<(string, string)> { ("chest", "coal") };
        Assert.Equal(automated, PowerFuelPolicy.AutomatedReserve(snapshot, cell, 50, covered));
        Assert.Equal(automated ? 0 : 250 - coal, FactoryLogistics.FuelRefillNeed(snapshot, [cell], 50, covered));
    }

    [Fact]
    public void ADisconnectedOrChangedFeederKeepsActorDeliveriesAvailable()
    {
        var (catalog, cell, _) = Native();
        var snapshot = Reserves(catalog.Scope, 20, true);
        Assert.False(PowerFuelPolicy.AutomatedReserve(snapshot, cell, 50, new HashSet<(string, string)>()));
        var changed = snapshot with { Records = snapshot.Records.Where(r => r.EntityId != "arm").ToArray() };
        Assert.False(PowerFuelPolicy.AutomatedReserve(changed, cell, 50, new HashSet<(string, string)> { ("chest", "coal") }));
    }

    [Theory]
    [InlineData("working", true)]
    [InlineData("waiting_for_space_in_destination", true)]
    [InlineData("full_output", true)]
    [InlineData("no_power", false)]
    [InlineData("no_fuel", false)]
    [InlineData("no_minable_resources", false)]
    public void NativeProducerStatusesDistinguishBackpressureFromStoppedSupply(string status, bool active)
    {
        var (state, snapshot, _, _) = CoalFixture([30]);
        var source = state.Cells.Single(c => c.Id == "source-0");
        snapshot = snapshot with { Records = snapshot.Records.Select(r => r.Kind == "work" && r.EntityId == source.Entities["drill"]
            ? r with { Data = Protocol.ToElement(new { statusName = status }) } : r).ToArray() };
        Assert.Equal(active, PowerFuelPolicy.ProducerActive(source, snapshot));
    }

    [Theory]
    [InlineData(26, 1, 0)]
    [InlineData(27, 1, 1)]
    [InlineData(48, 2, 0)]
    [InlineData(1000, 2, 0)]
    public void CoalSourcesCannotPromiseTheSameCapacityToSeveralBoilers(double capacity, int boilers, int covered)
    {
        var (state, snapshot, catalog, power) = CoalFixture([capacity], boilers);
        Assert.Equal(covered, FactoryTransportCoverage.Connected(state, snapshot, catalog, new Dictionary<string, double>(), power).Count);
        Assert.Empty(FactoryTransportCoverage.Connected(state, snapshot, catalog, null));
    }

    [Fact]
    public void SeveralSmallSourcesCanCoverOneBoilerWhileAStoppedOrReservedSourceCannot()
    {
        var (state, snapshot, catalog, power) = CoalFixture([16, 16]);
        Assert.Single(FactoryTransportCoverage.Connected(state, snapshot, catalog, null, power));
        var stopped = snapshot with { Records = snapshot.Records.Where(r => r.EntityId != "source-0-drill").ToArray() };
        Assert.Empty(FactoryTransportCoverage.Connected(state, stopped, catalog, null, power));
        var reserved = state with { Transports = state.Transports!.Select(b => b with { ActorReserve = 300 }).ToArray() };
        Assert.Empty(FactoryTransportCoverage.Connected(reserved, snapshot, catalog, null, power));
        Assert.Equal(0, PowerFuelTransport.Available(state, snapshot, catalog, new Dictionary<string, double>(), power, state.Cells.Single(c => c.Id == "source-0"), 27));
    }

    [Fact]
    public void APartialNewConsumerCannotDiluteAnExistingBoilerSupply()
    {
        var (state, snapshot, catalog, power) = CoalFixture([30]);
        var source = state.Cells.Single(c => c.Id == "source-0");
        Assert.Single(FactoryTransportCoverage.Connected(state, snapshot, catalog, null, power));
        Assert.Equal(0, PowerFuelTransport.Available(state, snapshot, catalog, null, power, source, 27));
        var unused = state with { Transports = [] };
        Assert.Equal(30, PowerFuelTransport.Available(unused, snapshot, catalog, null, power, source, 27));
        var (small, smallSnapshot, _, _) = CoalFixture([16]);
        Assert.Equal(16, PowerFuelTransport.Available(small with { Transports = [] }, smallSnapshot, catalog, null, power,
            small.Cells.Single(c => c.Id == "source-0"), 27));
    }

    [Fact]
    public void CoalCapacityForPowerIsSeededEvenWhenTheBagCoversTheImmediateHorizon()
    {
        var raw = new Dictionary<string, double> { ["coal"] = 45 };
        var state = new FactoryState(1, "world", [], []);
        var seeds = FactoryDirector.RawSeeds(Catalogs.Raw(), state, raw, new Dictionary<string, long> { ["coal"] = 1000 }, 135);
        Assert.Equal(new[] { ("coal", 180.0) }, seeds);
    }

    [Fact]
    public void SixWorkingSourcesDoNotCoverFiveBoilersWhenTwoAreOutsideTheNativeFrame()
    {
        var (state, snapshot, catalog, power) = SourceFrame();
        var need = PowerFuelTransport.SourceNeed(state, snapshot, catalog, power);
        Assert.Equal(new PowerFuelSourceNeed(5, 4, 27), need);
        Assert.Equal(1, need.Missing);
        Assert.DoesNotContain(PowerFuelTransport.LocalSources(state, snapshot, state.Cells.Where(c => c.Kind == "power").ToArray()),
            c => c.Id is "source-0" or "source-1");
    }

    [Fact]
    public void OneMoreCompatibleLocalMinerCompletesTheJointSourcePool()
    {
        var (state, snapshot, catalog, power) = SourceFrame(extra: true);
        var need = PowerFuelTransport.SourceNeed(state, snapshot, catalog, power);
        Assert.Equal(new PowerFuelSourceNeed(5, 5, 27), need);
        Assert.Equal(0, need.Missing);
    }

    [Fact]
    public void ASourceMustCoverNativeDemandRatherThanMerelyCountAsALocalCell()
    {
        var (state, snapshot, catalog, power) = SourceFrame();
        state = state with { Rows = state.Rows!.Select(r => r.Id == 5 ? r with { CellPerMinute = 26 } : r).ToArray() };
        Assert.Equal(2, PowerFuelTransport.SourceNeed(state, snapshot, catalog, power).Missing);
        snapshot = snapshot with { Records = snapshot.Records.Select(r => r.EntityId == "source-4-drill" && r.Kind == "work"
            ? r with { Data = Protocol.ToElement(new { statusName = "no_minable_resources" }) } : r).ToArray() };
        Assert.Equal(3, PowerFuelTransport.SourceNeed(state, snapshot, catalog, power).Missing);
    }

    [Fact]
    public void UnfinishedFuelLinksRetainTheirSourcesWithoutStartingUnnecessaryNewMiners()
    {
        var (state, snapshot, catalog, power) = SourceFrame();
        state = state.With(new FactoryCell("line", 0, new(0, 0, true), "transport", "transport-belt", null,
            new Dictionary<string, string>(), "building", 1)).With(new FactoryTransportBus("bus", "source-5", "coal", "line",
                [new("target-0", "receiver", 100)]));
        Assert.Equal(0, PowerFuelTransport.SourceNeed(state, snapshot, catalog, power).Missing);
    }

    [Fact]
    public void ASourcePromisedToAnotherLinkIsNotCountedAgain()
    {
        var (state, snapshot, catalog, power) = SourceFrame();
        state = state.With(new FactoryCell("line", 0, new(0, 0, true), "transport", "transport-belt", null,
            new Dictionary<string, string>(), "ready", 1)).With(new FactoryTransportBus("bus", "source-5", "coal", "line", []));
        Assert.Equal(2, PowerFuelTransport.SourceNeed(state, snapshot, catalog, power).Missing);
    }

    [Fact]
    public void OneBoilerStillUsesTheExistingMultiSourceConnectionInsteadOfGrowingForABatch()
    {
        var (state, snapshot, catalog, power) = SourceFrame();
        state = state with { Cells = state.Cells.Where(c => c.Kind != "power" || c.Id == "target-0").ToArray() };
        Assert.Equal(0, PowerFuelTransport.SourceNeed(state, snapshot, catalog, power).Missing);
    }

    [Fact]
    public void SourceGrowthCannotUseThePreviousActorsNativePowerOrInventory()
    {
        var (state, snapshot, catalog, power) = SourceFrame();
        Assert.Throws<InvalidDataException>(() => PowerFuelTransport.SourceNeed(state,
            snapshot with { Scope = snapshot.Scope with { Generation = 20 } }, catalog, power));
        Assert.Throws<InvalidDataException>(() => PowerFuelTransport.SourceNeed(state, snapshot, catalog,
            power with { Scope = power.Scope with { Generation = 20 } }));
    }

    [Fact]
    public void TheNormalEightExistingSourcesCoverSixBoilersWithoutBuildingAnotherMiner()
    {
        var (state, snapshot, catalog, power) = SourceFrame(extra: true, normalGeometry: true, targetCount: 6);
        Assert.Equal(new PowerFuelSourceNeed(6, 8, 27), PowerFuelTransport.SourceNeed(state, snapshot, catalog, power));
        var endpoints = state.Cells.Select(c => c.Entities[c.Kind == "power" ? "input-chest" : "output-chest"]).ToArray();
        Assert.Null(FactoryTransportBuilder.PlanningCenter(snapshot, endpoints, 1));
        Assert.NotNull(FactoryTransportBuilder.PlanningCenter(snapshot, endpoints, 1, FactoryTransportBuilder.FuelPlanningRadius));
    }

    private static (FactoryState State, FactorySnapshot Snapshot, ProductionCatalog Catalog, PowerState Power) SourceFrame(
        bool extra = false, bool normalGeometry = false, int targetCount = 5)
    {
        var (catalog, _, template) = Native();
        var cells = new List<FactoryCell>();
        var records = new List<FactoryRecord>();
        var rows = new List<ResourceRow>();
        void Entity(string id, MapPosition position) => records.Add(new(id, "entity", id, "native",
            Protocol.ToElement(new { role = "factory", position })));
        for (int target = 0; target < targetCount; target++)
        {
            string id = $"target-{target}";
            var parts = new Dictionary<string, string> { ["boiler"] = id + "-boiler", ["input-chest"] = id + "-in", ["input-inserter"] = id + "-arm" };
            var position = new MapPosition(target % 2 == 0 ? 70.5 : 66.5, -2.5 - 3 * target);
            foreach (string part in parts.Values) Entity(part, position);
            cells.Add(new(id, 0, new(0, target, true), "power", "boiler", null, parts, "ready", target + 1));
        }
        double[] xs = normalGeometry
            ? extra ? [-23.5, -20.5, -13.5, -13.5, -15.5, -12.5, -19.5, -9.5] : [-23.5, -20.5, -13.5, -13.5, -15.5, -12.5]
            : extra ? [-52.5, -49.5, -13.5, -13.5, -15.5, -12.5, -9.5] : [-52.5, -49.5, -13.5, -13.5, -15.5, -12.5];
        for (int source = 0; source < xs.Length; source++)
        {
            string id = $"source-{source}";
            var position = new MapPosition(xs[source], -10.5);
            var parts = new Dictionary<string, string> { ["drill"] = id + "-drill", ["output-chest"] = id + "-out" };
            foreach (string part in parts.Values) Entity(part, position);
            cells.Add(new(id, 0, new(source, 0, true), "miner", "electric-mining-drill", "coal", parts, "ready", 1));
            rows.Add(new(source, "miner", "coal", "coal", new("electric-mining-drill", "iron-chest"), position, 8, 3, 1, 30));
            records.Add(new(id + "-work", "work", id + "-drill", "native-mining", Protocol.ToElement(new { statusName = "working" })));
        }
        var state = new FactoryState(1, catalog.Scope.WorldId, [], cells, rows, Transports: []);
        var snapshot = new FactorySnapshot("native", catalog.Scope, 10, 100, Protocol.ToElement(new { atomic = true }), records);
        var power = template with { Boilers = cells.Where(c => c.Kind == "power").Select(c => template.Boilers[0] with { Id = c.Entities["boiler"] }).ToArray() };
        return (state, snapshot, catalog, power);
    }

    private static (ProductionCatalog Catalog, FactoryCell Cell, PowerState Power) Native(double usage = 30000, double value = 4000000, double effectivity = 1)
    {
        var original = Catalogs.Early();
        var catalog = original with { Items = new Dictionary<string, NativeItem>(original.Items)
            { ["coal"] = new(value, 50, "chemical"), ["boiler"] = new(0, 50, PlaceEntity: "boiler", PlaceEntityType: "boiler") } };
        var cell = new FactoryCell("power", 0, new(0, 0, true), "power", "boiler", null,
            new Dictionary<string, string> { ["boiler"] = "boiler", ["input-chest"] = "chest", ["input-inserter"] = "arm" }, "ready", 1);
        var power = new PowerState(catalog.Scope, 10, 1, 1, true, [], [new("boiler", "boiler", new(0, 0), 0, usage, effectivity,
            new Dictionary<string, long>(), [], FuelCategories: new Dictionary<string, bool> { ["chemical"] = true })]);
        return (catalog, cell, power);
    }

    private static FactorySnapshot Reserves(ActorScope scope, long coal, bool lit) => new("native", scope, 10, 4,
        Protocol.ToElement(new { atomic = true }),
        [new("boiler", "entity", "boiler", "boiler", Protocol.ToElement(new { role = "factory", type = "boiler", fuelInventoryId = "boiler-stock", burnerRemainingJoules = lit ? 1000 : 0 })),
         new("chest-stock", "inventory", "chest", "chest", Protocol.ToElement(new { items = new Dictionary<string, long> { ["coal"] = coal } })),
         new("boiler-stock", "inventory", "boiler", "fuel", Protocol.ToElement(new { items = new Dictionary<string, long>() })),
         new("arm", "entity", "arm", "inserter", Protocol.ToElement(new { role = "factory", transport = new { pickupTargetId = "chest", dropTargetId = "boiler" } }))]);

    private static (FactoryState State, FactorySnapshot Snapshot, ProductionCatalog Catalog, PowerState Power) CoalFixture(double[] rates, int targets = 1)
    {
        var (state, snapshot, _) = FactoryTransportCoverageTests.Fixture(rates, targets);
        var (catalog, _, template) = Native();
        state = state with
        {
            Cells = state.Cells.Select(c => c.Id.StartsWith("target-", StringComparison.Ordinal) ? c with
                { Kind = "power", MachineItem = "boiler", Recipe = null, Entities = new Dictionary<string, string>(c.Entities) { ["boiler"] = c.Id + "-boiler" } }
                : c.Id.EndsWith("-line", StringComparison.Ordinal) ? c : c with
                { Kind = "miner", MachineItem = "electric-mining-drill", Recipe = "coal", Entities = new Dictionary<string, string>(c.Entities) { ["drill"] = c.Id + "-drill" } }).ToArray(),
            Rows = state.Rows!.Select(r => r with { Kind = "miner", Product = "coal" }).ToArray(),
            Transports = state.Transports!.Select(b => b with { Item = "coal" }).ToArray()
        };
        var records = snapshot.Records.Select(r => r with { Data = JsonDocument.Parse(r.Data.GetRawText().Replace("iron-plate", "coal", StringComparison.Ordinal)).RootElement.Clone() }).ToList();
        foreach (var source in state.Cells.Where(c => c.IsResource))
        {
            records.Add(new(source.Id + "-work", "work", source.Id + "-drill", "native-mining", Protocol.ToElement(new { statusName = "working" })));
            records.Add(new(source.Id + "-stock", "inventory", source.Entities["output-chest"], "chest", Protocol.ToElement(new { items = new { coal = 300 } })));
        }
        snapshot = snapshot with { Records = records };
        var power = template with { Boilers = state.Cells.Where(c => c.Kind == "power").Select(c => template.Boilers[0] with { Id = c.Entities["boiler"] }).ToArray() };
        return (state, snapshot, catalog, power);
    }
}

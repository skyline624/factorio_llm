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

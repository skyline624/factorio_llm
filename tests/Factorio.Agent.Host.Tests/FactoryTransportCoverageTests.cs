using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryTransportCoverageTests
{
    [Theory]
    [InlineData(6, true)]
    [InlineData(12, false)]
    public void AHealthySourceStopsCoveringTheConsumerWhenDemandGrows(double crafts, bool covered)
    {
        var (state, snapshot, shares) = Fixture([15], crafts: crafts);
        Assert.Single(FactoryTransportHealth.Connected(state, snapshot));
        Assert.Equal(covered, Covered(state, snapshot, shares).Contains(("target-0-in", "iron-plate")));
    }

    [Fact]
    public void AdditionalHealthyRawSuppliersCoverTheSameConsumersInput()
    {
        var (state, snapshot, shares) = Fixture([15, 18.75]);
        Assert.All(state.Transports!, bus => Assert.True(FactoryTransportHealth.Healthy(state, snapshot, bus)));
        Assert.Contains(("target-0-in", "iron-plate"), Covered(state, snapshot, shares));
    }

    [Fact]
    public void ADepletedSupplierDoesNotCountAndItsReplacementCanCoverTheInput()
    {
        var (state, snapshot, shares) = Fixture([15, 18.75], crafts: 6);
        state = state.With(state.Cells.Single(c => c.Id == "source-0") with { Status = "depleted" });
        Assert.Contains(("target-0-in", "iron-plate"), Covered(state, snapshot, shares));
        state = state.With(state.Cells.Single(c => c.Id == "source-1") with { Status = "depleted" });
        Assert.Empty(Covered(state, snapshot, shares));
    }

    [Theory]
    [InlineData(15, 0)]
    [InlineData(30, 2)]
    public void ACommonSourceCannotPromiseItsWholeCapacityToEveryConsumer(double sourceRate, int coveredCount)
    {
        var (state, snapshot, shares) = Fixture([sourceRate], targets: 2, crafts: 6);
        Assert.Equal(2, FactoryTransportHealth.Connected(state, snapshot).Count);
        Assert.Equal(coveredCount, Covered(state, snapshot, shares).Count);
    }

    [Fact]
    public void PausedConsumersReleaseTheirSupplyShareAndRemainExcluded()
    {
        var (state, snapshot, shares) = Fixture([15], targets: 2, crafts: 6, pausedSecond: true);
        Assert.True(FactoryTransportHealth.Healthy(state, snapshot, state.Transports!.Single()));
        Assert.Equal(new[] { ("target-0-in", "iron-plate") }, Covered(state, snapshot, shares));
    }

    [Fact]
    public void NominalCapacityDoesNotReplaceNativeRouteHealth()
    {
        var (state, snapshot, shares) = Fixture([40]);
        snapshot = snapshot with { Records = snapshot.Records.Where(r => r.EntityId != "source-0-belt").ToArray() };
        Assert.Empty(Covered(state, snapshot, shares));
    }

    [Fact]
    public void RegistriesWithoutARequestedRateKeepTheirHealthyConnectionSemantics()
    {
        var (state, snapshot, shares) = Fixture([1]);
        Assert.Empty(Covered(state, snapshot, shares));
        Assert.Single(Covered(state, snapshot, null));
    }

    [Fact]
    public void AConsumerOutsideThePlanKeepsItsDeclaredHealthyPauseWithoutActorRefills()
    {
        var (state, snapshot, _) = Fixture([1], targets: 2, pausedSecond: true);
        Assert.Equal(2, FactoryTransportHealth.Connected(state, snapshot).Count);
        Assert.Equal(2, Covered(state, snapshot, null).Count);
    }

    [Fact]
    public void UnknownRawCapacityKeepsActorRefillsAvailable()
    {
        var (state, snapshot, shares) = Fixture([40]);
        Assert.Empty(Covered(state with { Rows = null }, snapshot, shares));
    }

    [Fact]
    public void ASourceExtractorBoundsTheTransportEvenWithAFasterProducer()
    {
        var (state, snapshot, shares) = Fixture([1000], crafts: 30);
        Assert.Empty(Covered(state, snapshot, shares)); // 60 plates/min needed, only 48 pass through the source arm.
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(12, true)]
    public void IntermediateSuppliersUseTheirPlannedProductionShare(double sourceRate, bool covered)
    {
        var (state, snapshot, shares) = Fixture([sourceRate], item: "iron-gear-wheel");
        Assert.Equal(covered, Covered(state, snapshot, shares).Contains(("target-0-in", "iron-gear-wheel")));
    }

    [Fact]
    public void IntermediateSuppliersRemainBoundedByTheirOwnMachineAndInserters()
    {
        var (state, snapshot, shares) = Fixture([100], item: "iron-gear-wheel", crafts: 30);
        Assert.Empty(Covered(state, snapshot, shares)); // One gear cell's input arm permits 24 gears/min.
    }

    private static HashSet<(string Chest, string Item)> Covered(FactoryState state, FactorySnapshot snapshot,
        IReadOnlyDictionary<string, double>? shares) => FactoryTransportCoverage.Connected(state, snapshot, Catalogs.Early(), shares);

    private static (FactoryState State, FactorySnapshot Snapshot, IReadOnlyDictionary<string, double> Shares) Fixture(
        double[] sourceRates, int targets = 1, double crafts = 12, string item = "iron-plate", bool pausedSecond = false)
    {
        bool raw = item == "iron-plate";
        string targetRecipe = raw ? "iron-gear-wheel" : "automation-science-pack";
        var cells = new List<FactoryCell>();
        var records = new List<FactoryRecord>();
        var rows = new List<ResourceRow>();
        var buses = new List<FactoryTransportBus>();
        for (int target = 0; target < targets; target++)
        {
            string id = $"target-{target}";
            cells.Add(new(id, 1, new(0, target, true), "assembler", "assembling-machine-1", targetRecipe,
                new Dictionary<string, string> { ["input-chest"] = id + "-in" }, "ready", 1));
            string[] arms = Enumerable.Range(0, sourceRates.Length).Select(source => $"source-{source}-receiver-{target}").ToArray();
            records.Add(Entity(id + "-in", "container", new { redNeighbours = arms, redNeighbourCount = arms.Length }));
        }
        for (int source = 0; source < sourceRates.Length; source++)
        {
            string id = $"source-{source}", belt = id + "-belt", chest = id + "-out", extractor = id + "-extractor";
            cells.Add(new(id, raw ? 0 : 1, new(source, 0, true), raw ? "smelter" : "assembler",
                raw ? "stone-furnace" : "assembling-machine-1", raw ? "iron-plate" : "iron-gear-wheel",
                new Dictionary<string, string> { ["output-chest"] = chest }, "ready", 1));
            if (raw) rows.Add(new(source, "smelter", item, "iron-ore", new("burner-mining-drill", "iron-chest", "stone-furnace"),
                new(source * 10, 0), 4, 4, 1, sourceRates[source]));
            var entities = new Dictionary<string, string> { ["source-inserter"] = extractor, ["belt-0"] = belt };
            records.Add(Entity(chest, "container", new { redNeighbours = Array.Empty<string>(), redNeighbourCount = 0 }));
            records.Add(Entity(belt, "transport-belt", new { beltConnections = new ObservedBeltConnections([], [], 0, 0) }));
            records.Add(Entity(extractor, "inserter", new { pickupTargetId = chest, dropTargetId = belt, inserterControl = Control(null, false) }));
            var consumers = new List<FactoryTransportConsumer>();
            for (int target = 0; target < targets; target++)
            {
                string arm = $"{id}-receiver-{target}", targetId = $"target-{target}", role = $"target-inserter-{target}";
                bool paused = pausedSecond && target == 1;
                entities[role] = arm;
                consumers.Add(new(targetId, role, 40, paused));
                records.Add(Entity(arm, "inserter", new { pickupTargetId = belt, dropTargetId = targetId + "-in",
                    inserterControl = Control(targetId + "-in", paused) }));
            }
            cells.Add(new(id + "-line", 0, new(0, 0, true), "transport", "transport-belt", null, entities, "ready", 1,
                Plan: new Dictionary<string, PlannedEntity> { ["belt-0"] = new("belt-0", "transport-belt", new(source * 10, 3), 4) }));
            buses.Add(new(id + "-bus", id, item, id + "-line", consumers));
        }
        records.Add(Entity("generator", "generator", new { }));
        var state = new FactoryState(1, "world", [], cells, rows, Transports: buses);
        var snapshot = new FactorySnapshot("native", Catalogs.Early().Scope, 10, 20, Protocol.ToElement(new { atomic = true }), records);
        var shares = new Dictionary<string, double> { [targetRecipe] = crafts };
        if (!raw) shares["iron-gear-wheel"] = sourceRates[0];
        return (state, snapshot, shares);

        ObservedInserterControl Control(string? input, bool paused) => new(true, "whitelist", [item], input is not null,
            input is null ? null : item, input is null ? null : "<", input is null ? null : paused ? 0 : 40,
            input is null ? [] : [input], input is null ? 0 : 1, NativeNormalFilters: true);
    }

    private static FactoryRecord Entity(string id, string type, object transport) => new(id, "entity", id, type,
        Protocol.ToElement(new { role = "factory", type, force = "own", surfaceIndex = 1, electricNetworkId = 1,
            power = new { energy = 1000, networkId = 1 }, transport }));
}

using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PowerStateTests
{
    // Native 2.0.77 values: boiler 1.8 MW, steam engine 900 kW, assembler 75 kW + 2.5 kW drain.
    private const double Boiler = 30000, Engine = 15000;

    [Fact]
    public void BoilerHeatBoundsTheEnginesItFeeds()
    {
        Assert.Equal(15000, Budget(Engines("e1"), Boilers(("b1", ["e1"]))).CapacityPerTick);
        Assert.Equal(30000, Budget(Engines("e1", "e2"), Boilers(("b1", ["e1", "e2"]))).CapacityPerTick);
        Assert.Equal(30000, Budget(Engines("e1", "e2", "e3"), Boilers(("b1", ["e1", "e2", "e3"]))).CapacityPerTick);
        Assert.Equal(45000, Budget(Engines("e1", "e2", "e3"), Boilers(("b1", ["e1", "e2"]), ("b2", ["e3"]))).CapacityPerTick);
    }

    [Fact]
    public void EnginesWithoutAnObservedBoilerAddNothingButInterfacesCountTheirProduction()
    {
        var sources = Engines("e1", "orphan").Append(new PowerSource("eei", "electric-energy-interface", "electric-energy-interface", new(0, 0), 20000)).ToArray();
        var budget = Budget(sources, Boilers(("b1", ["e1"]), ("elsewhere", ["other-network-engine"])));
        Assert.Equal(15000 + 20000, budget.CapacityPerTick);
    }

    [Fact]
    public void DemandIncludesDrainAndExpansionStartsAboveEightyPercent()
    {
        var consumers = new[] { new PowerConsumers("assembling-machine-1", 2, 2500, 83.4), new PowerConsumers("inserter", 4, 980, 26.6) };
        var budget = Budget(Engines("e1", "e2"), Boilers(("b1", ["e1", "e2"])), consumers);
        Assert.Equal(3590, budget.DemandPerTick, 6);
        Assert.False(budget.Exceeded(24000 - 3590));
        Assert.True(budget.Exceeded(24000 - 3590 + 1));
        Assert.True(Budget([], []).Exceeded(1));
    }

    [Fact]
    public void TheMainNetworkHasTheLargestCapacity()
    {
        var small = new ElectricNetworkState(7, 1, [new("eei", "electric-energy-interface", "electric-energy-interface", new(0, 0), 1000)], []);
        var large = new ElectricNetworkState(3, 2, Engines("e1", "e2"), []);
        var unpowered = new ElectricNetworkState(9, 1, [], []);
        var state = State([small, large, unpowered], Boilers(("b1", ["e1", "e2"])));
        Assert.Equal(3, state.Main()!.NetworkId);
        Assert.Null(State([unpowered], []).Main());
    }

    [Fact]
    public void ParsesTheNativeReadingIncludingEmptyLuaTables()
    {
        var data = new
        {
            scope = Scope, collectedTick = 50, surfaceIndex = 1, knownEntityCount = 4, atomic = true,
            networks = new object[]
            {
                new
                {
                    networkId = 1, poles = 1,
                    sources = new[] { new { id = "e1", name = "steam-engine", type = "generator", position = new { x = 1.5, y = 2.5 },
                        maxPowerOutput = 15000.0, generatedLastTick = 12.5, status = "working" } },
                    consumers = new { },
                    statistics = new { precision = "five_seconds", consumption = 12.0, production = 12.5 }
                }
            },
            boilers = new[] { new { id = "b1", name = "boiler", position = new { x = 0.5, y = 0 }, direction = 4, energyPerTick = 30000.0,
                effectivity = 1.0, fuel = new { coal = 5 }, fuelCategories = new { chemical = true }, generatorIds = new[] { "e1" }, status = "working" } }
        };
        var state = PowerState.Parse(Response(data));
        Assert.Empty(state.Networks[0].Consumers);
        Assert.Equal(5, state.Boilers[0].Fuel["coal"]);
        Assert.True(state.Boilers[0].FuelCategories!["chemical"]);
        Assert.Equal(15000, state.Budget(state.Networks[0]).CapacityPerTick);
    }

    [Fact]
    public void RejectsInconsistentOrNonFiniteReadings()
    {
        var engine = new { id = "e1", name = "steam-engine", type = "generator", position = new { x = 0, y = 0 }, maxPowerOutput = -1.0 };
        object Reading(object source, long tick = 50) => new
        {
            scope = Scope, collectedTick = tick, surfaceIndex = 1, knownEntityCount = 1, atomic = true,
            networks = new[] { new { networkId = 1, poles = 1, sources = new[] { source }, consumers = new object[0] } },
            boilers = new object[0]
        };
        Assert.Throws<InvalidDataException>(() => PowerState.Parse(Response(Reading(engine))));
        Assert.Throws<InvalidDataException>(() => PowerState.Parse(Response(Reading(engine with { maxPowerOutput = 1.0 }, tick: 49))));
    }

    private static readonly ActorScope Scope = new("world", "session", "actor", 1, 1);

    private static GameResponse Response(object data) => new(1, "r", true, 50, Protocol.ToElement(data));

    internal static PowerSource[] Engines(params string[] ids) =>
        ids.Select(id => new PowerSource(id, "steam-engine", "generator", new(0, 0), Engine)).ToArray();

    internal static ObservedBoiler[] Boilers(params (string Id, string[] Generators)[] boilers) => boilers.Select(b =>
        new ObservedBoiler(b.Id, "boiler", new(0, 0), 0, Boiler, 1, new Dictionary<string, long>(), b.Generators)).ToArray();

    private static PowerState State(IReadOnlyList<ElectricNetworkState> networks, IReadOnlyList<ObservedBoiler> boilers) =>
        new(Scope, 50, 1, 10, true, networks, boilers);

    private static PowerBudget Budget(IReadOnlyList<PowerSource> sources, IReadOnlyList<ObservedBoiler> boilers,
        IReadOnlyList<PowerConsumers>? consumers = null)
    {
        var network = new ElectricNetworkState(1, 1, sources, consumers ?? []);
        return State([network], boilers).Budget(network);
    }
}

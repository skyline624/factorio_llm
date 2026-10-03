using System.Text.Json;
using System.Text.Json.Nodes;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SurvivalKitTests
{
    // Base 2.0.77 item.lua: pistol cooldown 15 range 15, submachine gun cooldown 6 range 18, shotgun shells are not bullets;
    // light armor 3/20% physical, heavy armor 6/30%, modular armor 6/30% with 10 inventory slots; rounds of 5, 8 and 24 damage.
    private static ProductionCatalog Catalog(params string[] enabled)
    {
        static NativeRecipe Recipe(string item, bool on) => new(item, on, "crafting", 1, [new("iron-plate", "item", 1)], [new(item, "item", 1)], false);
        string[] items = ["pistol", "submachine-gun", "shotgun", "light-armor", "heavy-armor", "modular-armor",
            "firearm-magazine", "piercing-rounds-magazine", "uranium-rounds-magazine", "shotgun-shell"];
        var early = Catalogs.Early();
        return early with
        {
            Recipes = [.. early.Recipes, .. items.Select(i => Recipe(i, enabled.Contains(i)))],
            Items = new Dictionary<string, NativeItem>(early.Items)
            {
                ["pistol"] = new(0, 5), ["submachine-gun"] = new(0, 5), ["shotgun"] = new(0, 5),
                ["light-armor"] = new(0, 1), ["heavy-armor"] = new(0, 1), ["modular-armor"] = new(0, 1),
                ["firearm-magazine"] = new(0, 200, AmmoCategory: "bullet", MagazineSize: 10, RoundDamage: 5),
                ["piercing-rounds-magazine"] = new(0, 200, AmmoCategory: "bullet", MagazineSize: 10, RoundDamage: 8),
                ["uranium-rounds-magazine"] = new(0, 200, AmmoCategory: "bullet", MagazineSize: 10, RoundDamage: 24),
                ["shotgun-shell"] = new(0, 200, AmmoCategory: "shotgun-shell", MagazineSize: 10, RoundDamage: 40)
            },
            Guns = new Dictionary<string, NativeGun>
            {
                ["pistol"] = new(15, 15, 1, ["bullet"]),
                ["submachine-gun"] = new(18, 6, 1, ["bullet"]),
                ["shotgun"] = new(15, 60, 1, ["shotgun-shell"])
            },
            Armors = new Dictionary<string, NativeArmor>
            {
                ["light-armor"] = new(3, .2, 0), ["heavy-armor"] = new(6, .3, 0), ["modular-armor"] = new(6, .3, 10)
            }
        };
    }

    [Fact]
    public void TheBestObtainableArmorReplacesOnlyWeakerProtection()
    {
        Assert.Equal("light-armor", SurvivalKitPlanner.Armor(Catalog("light-armor"), new Dictionary<string, long>(), null));
        Assert.Equal("heavy-armor", SurvivalKitPlanner.Armor(Catalog("light-armor", "heavy-armor"), new Dictionary<string, long>(), "light-armor"));
        // Equal physical protection with more inventory is better; a carried armor is obtained even without its recipe.
        Assert.Equal("modular-armor", SurvivalKitPlanner.Armor(Catalog("light-armor"), new Dictionary<string, long> { ["modular-armor"] = 1 }, "heavy-armor"));
        Assert.Null(SurvivalKitPlanner.Armor(Catalog("light-armor", "heavy-armor"), new Dictionary<string, long>(), "heavy-armor"));
        Assert.Null(SurvivalKitPlanner.Armor(Catalog(), new Dictionary<string, long>(), null));
    }

    [Fact]
    public void ArmorFallbacksNeverIncludeEqualOrWeakerWornProtection()
    {
        var catalog = Catalog("light-armor", "heavy-armor", "modular-armor");
        var carried = new Dictionary<string, long>();
        Assert.Equal(new[] { "modular-armor", "heavy-armor", "light-armor" }, SurvivalKitPlanner.Armors(catalog, carried, null));
        Assert.Equal(new[] { "modular-armor", "heavy-armor" }, SurvivalKitPlanner.Armors(catalog, carried, "light-armor"));
        Assert.Equal(new[] { "modular-armor" }, SurvivalKitPlanner.Armors(catalog, carried, "heavy-armor"));
        Assert.Empty(SurvivalKitPlanner.Armors(catalog, carried, "modular-armor"));
    }

    [Fact]
    public void TheSubmachineGunReplacesThePistolOnceObtainable()
    {
        var none = new Dictionary<string, long>();
        Assert.Null(SurvivalKitPlanner.Gun(Catalog(), none, ["pistol"]));
        Assert.Equal("submachine-gun", SurvivalKitPlanner.Gun(Catalog("submachine-gun"), none, ["pistol"]));
        Assert.Equal("submachine-gun", SurvivalKitPlanner.Gun(Catalog(), new Dictionary<string, long> { ["submachine-gun"] = 1 }, []));
        Assert.Null(SurvivalKitPlanner.Gun(Catalog("submachine-gun"), none, ["pistol", "submachine-gun"]));
        // The reflex fires bullets only: a shotgun is never chosen, however obtainable.
        Assert.Null(SurvivalKitPlanner.Gun(Catalog("shotgun"), none, ["pistol"]));
    }

    [Fact]
    public void TheStrongestObtainableBulletAmmunitionIsPreferred()
    {
        var none = new Dictionary<string, long>();
        Assert.Equal(new[] { "firearm-magazine" }, SurvivalKitPlanner.Ammunition(Catalog("firearm-magazine", "shotgun-shell"), none));
        Assert.Equal(new[] { "piercing-rounds-magazine", "firearm-magazine" }, SurvivalKitPlanner.Ammunition(Catalog("firearm-magazine", "piercing-rounds-magazine"), none));
        Assert.Equal("uranium-rounds-magazine", SurvivalKitPlanner.Ammunition(Catalog("firearm-magazine"),
            new Dictionary<string, long> { ["uranium-rounds-magazine"] = 3 })[0]);
    }

    [Fact]
    public void NativeCatalogAndLoadoutDescribeGunsArmorAndRoundDamage()
    {
        var catalog = ProductionCatalog.Parse(new(1, "r", true, 7, Protocol.ToElement(new
        {
            scope = new ActorScope("world", "session", "actor", 1, 1), collectedTick = 7, recipes = new { },
            items = new Dictionary<string, object>
            {
                ["firearm-magazine"] = new { fuelValue = 0, stackSize = 200, ammoCategory = "bullet", magazineSize = 10, roundDamage = 5 },
                ["heavy-armor"] = new { fuelValue = 0, stackSize = 1 }
            },
            mining = new { }, machines = new { }, handCategories = new { crafting = true },
            guns = new { submachineGun = new { range = 18, cooldown = 6, damageModifier = 1, ammoCategories = new[] { "bullet" }, minRange = 0, projectile = true } },
            armors = new Dictionary<string, object> { ["heavy-armor"] = new { physicalDecrease = 6, physicalPercent = .3, inventoryBonus = 0 } }
        })));
        Assert.Equal(5, catalog.Items["firearm-magazine"].RoundDamage);
        Assert.Equal(.3, catalog.Armors!["heavy-armor"].PhysicalPercent);
        Assert.Equal(10, SurvivalKitPlanner.ShotsPerSecond(catalog.Guns!["submachineGun"]));

        var loadout = EquipmentState.Parse(Protocol.ToElement(new
        {
            complete = true, armor = "light-armor",
            slots = new[] { new { index = 1, gun = "pistol", bulletGun = true, range = 15d, ammo = "firearm-magazine", rounds = 100, ready = true } },
            carried = new object[] { new { slot = 4, name = "heavy-armor", kind = "armor", count = 1, bullet = false, range = 0d, rounds = 0 },
                new { slot = 5, name = "firearm-magazine", kind = "ammo", count = 20, bullet = true, range = 0d, rounds = 200, damage = 5d } }
        }));
        Assert.Equal("light-armor", loadout.Armor);
        Assert.Equal(("heavy-armor", "armor"), (loadout.Carried[0].Name, loadout.Carried[0].Kind));
        Assert.Equal(5, loadout.Carried[1].Damage);
    }

    [Fact]
    public void KitProductionUsesOnlyCarriedAndFinishedFactoryStock()
    {
        // Run 16 (2026-10-01, seed 20261002): the actor respawned with an empty bag and left unarmored; the kit must come from
        // stock, never from mining or exploration that would send it out unprotected first.
        var catalog = Catalogs.Early() with
        {
            Recipes = [.. Catalogs.Early().Recipes,
                new("submachine-gun", true, "crafting", 10, [new("iron-gear-wheel", "item", 10), new("copper-plate", "item", 5), new("iron-plate", "item", 10)],
                    [new("submachine-gun", "item", 1)], false),
                new("firearm-magazine", true, "crafting", 1, [new("iron-plate", "item", 4)], [new("firearm-magazine", "item", 1)], false),
                new("steel-plate", true, "smelting", 16, [new("iron-plate", "item", 5)], [new("steel-plate", "item", 1)], false),
                new("heavy-armor", true, "crafting", 8, [new("copper-plate", "item", 100), new("steel-plate", "item", 50)], [new("heavy-armor", "item", 1)], false)]
        };
        var stock = new Dictionary<string, long> { ["iron-plate"] = 25, ["copper-plate"] = 120, ["iron-gear-wheel"] = 4 };
        // 4 carried gears leave 6 to craft from 12 plates; 10 more plates go into the gun: 22 of 25.
        Assert.Empty(SurvivalKitPlanner.Shortfall(catalog, new Dictionary<string, long> { ["submachine-gun"] = 1 }, stock));
        Assert.Equal(new Dictionary<string, long> { ["iron-plate"] = 77 },
            SurvivalKitPlanner.Shortfall(catalog, new Dictionary<string, long> { ["submachine-gun"] = 1, ["firearm-magazine"] = 20 }, stock));
        // Steel is smelted, never hand-crafted: heavy armor needs finished steel in stock.
        Assert.Equal(new Dictionary<string, long> { ["steel-plate"] = 50 },
            SurvivalKitPlanner.Shortfall(catalog, new Dictionary<string, long> { ["heavy-armor"] = 1 }, stock));
    }

    [Fact]
    public void EquipmentStepsWearArmorThenMountAndLoadTheBetterGun()
    {
        CarriedWeaponItem Carried(int slot, string name, string kind, int count = 1, double damage = 0) =>
            new(slot, name, kind, count, kind != "armor", 0, kind == "ammo" ? count * 10 : 0, damage);
        WeaponSlot pistol = new(1, true, 15, 100, true, "pistol", "firearm-magazine"), empty2 = new(2, false, 0, 0, false), empty3 = new(3, false, 0, 0, false);
        var worn = new EquipmentState([pistol, empty2, empty3], [Carried(4, "heavy-armor", "armor"), Carried(6, "submachine-gun", "gun"),
            Carried(7, "firearm-magazine", "ammo", 20, 5), Carried(8, "piercing-rounds-magazine", "ammo", 3, 8)], "light-armor");

        var armor = SurvivalKitPlanner.NextEquipment(worn, "heavy-armor", "submachine-gun")!;
        var args = Protocol.ToElement(armor.Arguments);
        Assert.Equal(("armor", 1, 4, "heavy-armor", "light-armor"), (args.GetProperty("compartment").GetString(), args.GetProperty("slot").GetInt32(),
            args.GetProperty("sourceSlot").GetInt32(), args.GetProperty("item").GetString(), args.GetProperty("replaces").GetString()));
        Assert.False(Protocol.ToElement(SurvivalKitPlanner.NextEquipment(worn with { Armor = null }, "heavy-armor", null)!.Arguments).TryGetProperty("replaces", out _));

        var armored = worn with { Armor = "heavy-armor", Carried = worn.Carried.Skip(1).ToArray() };
        args = Protocol.ToElement(SurvivalKitPlanner.NextEquipment(armored, "heavy-armor", "submachine-gun")!.Arguments);
        Assert.Equal(("gun", 2, 6), (args.GetProperty("compartment").GetString(), args.GetProperty("slot").GetInt32(), args.GetProperty("sourceSlot").GetInt32()));

        var mounted = armored with { Slots = [pistol, new(2, true, 18, 0, false, "submachine-gun"), empty3], Carried = armored.Carried.Skip(1).ToArray() };
        args = Protocol.ToElement(SurvivalKitPlanner.NextEquipment(mounted, "heavy-armor", "submachine-gun")!.Arguments);
        Assert.Equal(("ammo", 2, 8, 3), (args.GetProperty("compartment").GetString(), args.GetProperty("slot").GetInt32(),
            args.GetProperty("sourceSlot").GetInt32(), args.GetProperty("count").GetInt32()));

        var loaded = mounted with { Slots = [pistol, new(2, true, 18, 30, true, "submachine-gun", "piercing-rounds-magazine"), empty3] };
        Assert.Null(SurvivalKitPlanner.NextEquipment(loaded, "heavy-armor", "submachine-gun"));
    }

    [Fact]
    public async Task KitWearsCarriedArmorBeforeATripAndThenStaysQuiet()
    {
        var game = new KitGame(Catalog("light-armor"));
        var result = (await new SurvivalKitController(game, new Journal()).BeforeTripAsync("resource-search", CancellationToken.None))!;
        Assert.Equal(("complete", "light-armor"), (result.Status, result.Armor));
        Assert.Equal(new[] { "armor:light-armor" }, result.Equipped);
        Assert.Single(game.Calls, c => c == "submit");
        // An unchanged loadout of the same incarnation is not reconsidered at the next trip: one small observation only.
        game.Calls.Clear();
        Assert.Null(await new SurvivalKitController(game, new Journal()).BeforeTripAsync("travel", CancellationToken.None));
        Assert.Equal(["observe"], game.Calls);
    }

    [Fact]
    public async Task KitNeverChangesEquipmentWithAnEnemyInSight()
    {
        var game = new KitGame(Catalog("light-armor")) { Enemy = true };
        var result = await new SurvivalKitController(game, new Journal()).EnsureAsync("travel", CancellationToken.None);
        Assert.Equal(("deferred", null), (result.Status, result.Armor));
        Assert.DoesNotContain("submit", game.Calls);
    }

    [Fact]
    public async Task ALoadedPistolDoesNotLeaveTheMountedSubmachineGunEmptyBeforeATrip()
    {
        var game = new KitGame(Catalog("light-armor")) { MountedSubmachineGun = true };
        var result = await new SurvivalKitController(game, new Journal()).EnsureAsync("resource-search", CancellationToken.None);
        Assert.Equal(("complete", "light-armor"), (result.Status, result.Armor));
        Assert.Equal(new[] { "armor:light-armor", "ammo:firearm-magazine" }, result.Equipped);
        Assert.Equal(20, result.CarriedMagazines);
        Assert.Empty(result.Produced);
        Assert.Equal(2, game.Calls.Count(c => c == "submit"));
    }

    [Fact]
    public async Task MissingSteelForHeavyArmorStillAllowsCarriedLightArmor()
    {
        var catalog = Catalog("light-armor", "heavy-armor");
        catalog = catalog with { Recipes = catalog.Recipes.Select(r => r.Name == "heavy-armor"
            ? r with { Ingredients = [new("steel-plate", "item", 50)] } : r).ToArray() };
        var game = new KitGame(catalog);
        var result = await new SurvivalKitController(game, new Journal()).EnsureAsync("resource-search", CancellationToken.None);
        Assert.Equal(("complete", "light-armor"), (result.Status, result.Armor));
        Assert.Equal(50, result.Missing["heavy-armor"]["steel-plate"]);
        Assert.Equal(new[] { "armor:light-armor" }, result.Equipped);
        Assert.Single(game.Calls, c => c == "submit");
        Assert.Empty(result.Produced);
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }

    /// <summary>A living actor carrying light armor in main slot 4; equip operations move it natively.</summary>
    private sealed class KitGame(ProductionCatalog catalog) : IGameClient
    {
        private readonly ActorScope scope = new(Guid.NewGuid().ToString("N"), "session", "actor", 1, 1);
        private string? worn;
        private bool submachineLoaded;
        public bool Enemy { get; init; }
        public bool MountedSubmachineGun { get; init; }
        public List<string> Calls { get; } = [];

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            object data = request.Action switch
            {
                "production_catalog" => catalog with { Scope = scope, CollectedTick = 100 },
                "observe" => Observation(),
                "submit" => Equip(request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!),
                _ => throw new InvalidOperationException(request.Action)
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(data)));
        }

        private object Observation()
        {
            var inventory = new Dictionary<string, long>();
            var carried = new List<object>();
            if (worn is null)
            {
                inventory["light-armor"] = 1;
                carried.Add(new { slot = 4, name = "light-armor", kind = "armor", count = 1, bullet = false, range = 0d, rounds = 0 });
            }
            if (MountedSubmachineGun)
            {
                int magazines = submachineLoaded ? 20 : 30;
                inventory["firearm-magazine"] = magazines;
                carried.Add(new { slot = 6, name = "firearm-magazine", kind = "ammo", count = magazines, bullet = true,
                    range = 0d, rounds = magazines * 10, damage = 5d });
            }
            var slots = new List<object> { new { index = 1, gun = "pistol", bulletGun = true, range = 15d,
                ammo = "firearm-magazine", rounds = 100, ready = true } };
            if (MountedSubmachineGun) slots.Add(new { index = 2, gun = "submachine-gun", bulletGun = true, range = 18d,
                ammo = submachineLoaded ? "firearm-magazine" : null, rounds = submachineLoaded ? 100 : 0, ready = submachineLoaded });
            return new
        {
            scope, collectedTick = 100L, snapshotId = 1,
            coverage = new { atomic = true, collectionStartTick = 100L, collectionEndTick = 100L, knownInventoriesComplete = true,
                enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility", enemiesTruncated = false },
            agent = new
            {
                alive = true, controlMode = "ai", stopUnconfirmed = false, position = new MapPosition(0, 0), health = 250d, maxHealth = 250d,
                inventory,
                weapon = new { ready = true, rounds = 100, range = submachineLoaded ? 18d : 15d },
                loadout = new
                {
                    complete = true, armor = worn,
                    slots, carried
                }
            },
            enemies = Enemy ? new object[] { new { id = "biter", position = new MapPosition(10, 0), collectedTick = 100L } } : [],
            entities = Array.Empty<object>(), defenses = Array.Empty<object>()
        };
        }

        private object Equip(OperationSubmission submission)
        {
            var args = submission.Args;
            if (args.GetProperty("compartment").GetString() == "ammo")
            {
                Assert.True(MountedSubmachineGun);
                Assert.Equal((2, 6, 10), (args.GetProperty("slot").GetInt32(), args.GetProperty("sourceSlot").GetInt32(), args.GetProperty("count").GetInt32()));
                submachineLoaded = true;
                return new
                {
                    operationId = submission.OperationId, kind = "equip", status = "completed", acceptedTick = 100L, updatedTick = 100L,
                    effects = new
                    {
                        compartment = "ammo", slot = 2, sourceSlot = 6, item = "firearm-magazine", requested = 10, transferred = 10,
                        equipmentBefore = new { main = new Dictionary<string, long> { ["firearm-magazine"] = 30 },
                            ammo = new Dictionary<string, long> { ["firearm-magazine"] = 10 }, mainRounds = 300, loadedRounds = 100, selectedSlot = 1 },
                        equipmentAfter = new { main = new Dictionary<string, long> { ["firearm-magazine"] = 20 },
                            ammo = new Dictionary<string, long> { ["firearm-magazine"] = 20 }, mainRounds = 200, loadedRounds = 200, selectedSlot = 2 }
                    }
                };
            }
            Assert.Equal(("equip", "armor", 4), (submission.Kind, args.GetProperty("compartment").GetString(), args.GetProperty("sourceSlot").GetInt32()));
            worn = "light-armor";
            return new
            {
                operationId = submission.OperationId, kind = "equip", status = "completed", acceptedTick = 100L, updatedTick = 100L,
                effects = new
                {
                    compartment = "armor", slot = 1, sourceSlot = 4, item = "light-armor", requested = 1, transferred = 1,
                    equipmentBefore = new { main = new Dictionary<string, long> { ["light-armor"] = 1 }, armor = new { }, ammo = new { }, mainRounds = 0, loadedRounds = 100, selectedSlot = 1 },
                    equipmentAfter = new { main = new { }, armor = new Dictionary<string, long> { ["light-armor"] = 1 }, ammo = new { }, mainRounds = 0, loadedRounds = 100, selectedSlot = 1 }
                }
            };
        }
    }

    [Theory]
    [InlineData("valid-empty")]
    [InlineData("valid-swap")]
    [InlineData("replaced-lost")]
    [InlineData("armor-created")]
    [InlineData("wrong-replaced")]
    public void ArmorTransferProvesTheWornArmorAndKeepsTheReplacedOne(string variant)
    {
        bool swap = variant != "valid-empty";
        var request = OperationSubmission.Create(new("world", "session", "actor", 1, 2), "equip",
            new { compartment = "armor", slot = 1, sourceSlot = 4, item = "heavy-armor", count = 1, replaces = swap ? "light-armor" : null }, 1000);
        var effect = JsonNode.Parse(swap ? """
            {"compartment":"armor","slot":1,"sourceSlot":4,"item":"heavy-armor","requested":1,"transferred":1,"replaced":"light-armor",
             "equipmentBefore":{"main":{"heavy-armor":1},"armor":{"light-armor":1},"ammo":{},"mainRounds":0,"loadedRounds":100,"selectedSlot":1},
             "equipmentAfter":{"main":{"light-armor":1},"armor":{"heavy-armor":1},"ammo":{},"mainRounds":0,"loadedRounds":100,"selectedSlot":1}}
            """ : """
            {"compartment":"armor","slot":1,"sourceSlot":4,"item":"heavy-armor","requested":1,"transferred":1,
             "equipmentBefore":{"main":{"heavy-armor":1},"armor":{},"ammo":{},"mainRounds":0,"loadedRounds":100,"selectedSlot":1},
             "equipmentAfter":{"main":{},"armor":{"heavy-armor":1},"ammo":{},"mainRounds":0,"loadedRounds":100,"selectedSlot":1}}
            """)!;
        if (variant == "replaced-lost") effect["equipmentAfter"]!["main"] = new JsonObject();
        if (variant == "armor-created") effect["equipmentBefore"]!["main"] = new JsonObject();
        if (variant == "wrong-replaced") effect["replaced"] = "modular-armor";
        var receipt = OperationReceipt.Parse(Protocol.ToElement(new { operationId = request.OperationId,
            kind = "equip", status = "completed", acceptedTick = 10, updatedTick = 10, effects = effect }), request.OperationId);
        if (variant.StartsWith("valid", StringComparison.Ordinal)) EquipmentReceipt.Validate(request, receipt);
        else Assert.Throws<InvalidDataException>(() => EquipmentReceipt.Validate(request, receipt));
    }
}

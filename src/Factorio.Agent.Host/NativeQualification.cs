using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Explicit synthetic fixture. Its material injections disqualify it as an autonomous campaign.</summary>
public sealed class NativeQualification(RuntimeSession session)
{
    private readonly List<object> evidence = [];
    private readonly IGameClient game = session.CreateClient();

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Native qualification requires a session started with --fixture.");
        string report = Path.Combine(session.Directory, "native-qualification-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await InitializeFixtureAsync(token);
            NativeState before = await ReadStateAsync(token);
            Require(!before.Peaceful && before.Pollution && before.Expansion, "Hostile world settings must stay active.");
            Require(before.PlayersConnected == 0, "Headless qualification must start without a connected player.");

            var move = await ExecuteAsync("move", new { position = new MapPosition(0, 4), tolerance = 0.2 }, 600, token);
            NativeState moved = await ReadStateAsync(token);
            Require(move.Receipt.Status == "completed", "Native movement did not complete.");
            Require(moved.Position.DistanceTo(new(0, 4)) <= 0.3, "Receipt reported arrival without native position evidence.");
            Require(moved.Tick - before.Tick >= 15, "Movement completed implausibly fast.");
            evidence.Add(new { check = "native-walk", before, after = moved, receipt = move.Receipt.Evidence });

            var craft = await ExecuteAsync("craft", new { recipe = "iron-gear-wheel", count = 2 }, 600, token);
            NativeState crafted = await ReadStateAsync(token);
            Require(craft.Receipt.Status == "completed", "Native crafting did not complete.");
            Require(crafted.Count("iron-gear-wheel") - moved.Count("iron-gear-wheel") == 2, "Crafted products missing.");
            Require(moved.Count("iron-plate") - crafted.Count("iron-plate") == 4, "Crafting ingredient cost is incorrect.");
            evidence.Add(new { check = "native-craft", before = moved, after = crafted, receipt = craft.Receipt.Evidence });

            var client = new OperationClient(game);
            OperationReceipt repeated = await client.SubmitAsync(craft.Submission, token);
            NativeState replayed = await ReadStateAsync(token);
            Require(repeated.Status == "completed" && replayed.Count("iron-gear-wheel") == crafted.Count("iron-gear-wheel"), "Duplicate id replayed a mutation.");
            GameResponse conflict = await game.ExecuteAsync(GameRequest.Create("submit", craft.Submission with { Args = Protocol.ToElement(new { recipe = "iron-gear-wheel", count = 3 }) }), token);
            Require(!conflict.Ok && conflict.Error?.Code == "operation_conflict", "Changed payload reused an operation id.");
            evidence.Add(new { check = "deduplication-and-conflict", repeated = repeated.Evidence, conflict });

            await ExecuteAsync("move", new { position = new MapPosition(0, 0), tolerance = 0.2 }, 600, token);
            var tooFar = await ExecuteAsync("mine", new { position = new MapPosition(3.5, 0.5), name = "iron-ore", count = 1 }, 600, token);
            Require(tooFar.Receipt.Status == "failed" && tooFar.Receipt.Error?.Code == "out_of_reach", "Mining outside the native resource reach was not refused.");
            await ExecuteAsync("move", new { position = new MapPosition(1.5, 0), tolerance = 0.2 }, 600, token);
            NativeState miningBefore = await ReadStateAsync(token);
            var mine = await ExecuteAsync("mine", new { position = new MapPosition(3.5, 0.5), name = "iron-ore", count = 3 }, 1800, token);
            NativeState mined = await ReadStateAsync(token);
            Require(mine.Receipt.Status == "completed", "Native mining did not complete.");
            Require(mined.Count("iron-ore") - miningBefore.Count("iron-ore") == 3, "Mining output does not match count.");
            Require(mined.Tick - miningBefore.Tick >= 60, "Mining bypassed normal elapsed time.");
            evidence.Add(new { check = "native-mine", before = miningBefore, after = mined, receipt = mine.Receipt.Evidence });

            GameResponse hello = await session.HelloAsync(token);
            ActorScope scope = hello.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
            var wait = OperationSubmission.Create(scope, "wait", new { ticks = 600 }, hello.Tick + 1200);
            OperationReceipt waiting = await client.SubmitAsync(wait, token);
            Require(!waiting.IsTerminal, "Long wait ended before cancellation.");
            OperationReceipt cancelled = await client.CancelAsync(wait.OperationId, token);
            Require(cancelled.Status == "cancelled", "Cancellation did not stop the game operation.");
            OperationReceipt queried = await client.QueryAsync(wait.OperationId, token);
            Require(queried.Status == "cancelled", "Cancelled state was not retained.");
            evidence.Add(new { check = "explicit-cancellation", receipt = queried.Evidence });
            await QualifyFactoryAsync(token);
            await QualifyCombatAsync(token);
            await SaveReportAsync(report, true, null, token);
            return report;
        }
        catch (Exception error)
        {
            await SaveReportAsync(report, false, error.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task<(OperationSubmission Submission, OperationReceipt Receipt)> ExecuteAsync(string kind, object args, long ticks, CancellationToken token)
    {
        GameResponse hello = await session.HelloAsync(token);
        ActorScope scope = hello.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        var submission = OperationSubmission.Create(scope, kind, args, hello.Tick + ticks);
        string journal = Path.Combine(session.Directory, "qualification-operations.jsonl");
        await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(submission, Protocol.Json) + "\n", token);
        var client = new OperationClient(game);
        OperationReceipt receipt = await client.SubmitAsync(submission, token);
        if (!receipt.IsTerminal) receipt = await client.WaitAsync(submission.OperationId, TimeSpan.FromSeconds(ticks / 60.0 + 10), token);
        evidence.Add(new { operation = submission, receipt = receipt.Evidence });
        return (submission, receipt);
    }

    private async Task InitializeFixtureAsync(CancellationToken token)
    {
        GameResponse marker = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new { reason = "Synthetic native qualification; not an autonomous campaign" }), token);
        Require(marker.Ok && marker.Data.GetProperty("fixture").GetBoolean(), "Fixture marker was not persisted before setup.");
        const string command = """
            /silent-command local s=game.surfaces["nauvis"]; local chars=s.find_entities_filtered{type="character"}; assert(#chars==1,"Expected one agent character"); local c=chars[1]; c.teleport({0,0}); for _,e in pairs(s.find_entities_filtered{area={{-8,-8},{12,8}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-8,12 do for y=-8,8 do tiles[#tiles+1]={name="grass-1",position={x,y}} end end; s.set_tiles(tiles); c.get_inventory(defines.inventory.character_main).clear(); c.insert{name="iron-plate",count=12}; s.create_entity{name="iron-ore",position={3.5,0.5},amount=1000}; rcon.print("fixture-initialized");
            """;
        string output = await session.CreateRcon().ExecuteAsync(command, token);
        Require(output.Trim() == "fixture-initialized", "Fixture initialization was not acknowledged.");
        evidence.Add(new { check = "synthetic-fixture-initialized", disqualifiedAsCampaign = true, setup = "Cleared test area, positioned actor, inserted 12 iron plates and ore patch. Actions after initialization use the public mod protocol." });
    }

    private async Task QualifyFactoryAsync(CancellationToken token)
    {
        const string setup = """
            /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type="character",force="factorio_agent"}[1]; c.insert{name="wooden-chest",count=2}; c.insert{name="stone-furnace",count=1}; c.insert{name="assembling-machine-1",count=1}; c.insert{name="transport-belt",count=1}; c.insert{name="iron-plate",count=102}; c.insert{name="iron-ore",count=2}; c.insert{name="coal",count=2}; rcon.print("factory-fixture-ready");
            """;
        Require((await session.CreateRcon().ExecuteAsync(setup, token)).Trim() == "factory-fixture-ready", "Factory fixture setup not acknowledged.");
        evidence.Add(new { check = "synthetic-factory-fixture", disqualifiedAsCampaign = true,
            setup = "Added two chests, a furnace, an assembler, a belt, 102 plates, two ore and two coal after the initial native checks." });
        FactoryNativeState initial = await ReadFactoryStateAsync(token);
        Require(initial.ActorCount("iron-plate") == 110, "Unexpected initial fixture plate stock.");
        var chest = await ExecuteAsync("build", new { item = "wooden-chest", position = new MapPosition(2.5, -2.5) }, 600, token);
        FactoryNativeState built = await ReadFactoryStateAsync(token);
        Require(chest.Receipt.Status == "completed" && built.ChestCount == 1 && built.ActorCount("wooden-chest") == 1,
            "Chest construction did not consume exactly one item and create one entity.");
        string chestId = chest.Receipt.Effects.GetProperty("entityId").GetString()!;
        var blocked = await ExecuteAsync("build", new { item = "wooden-chest", position = new MapPosition(2.5, -2.5) }, 600, token);
        FactoryNativeState stillBuilt = await ReadFactoryStateAsync(token);
        Require(blocked.Receipt.Status == "failed" && blocked.Receipt.Error?.Code == "placement_blocked"
            && stillBuilt.ChestCount == 1 && stillBuilt.ActorCount("wooden-chest") == 1,
            "Blocked placement consumed an item or created another chest.");
        evidence.Add(new { check = "native-build-and-collision", before = initial, after = stillBuilt });

        var inserted = await ExecuteAsync("insert", new { entityId = chestId, inventory = "chest", item = "iron-plate", count = 17 }, 600, token);
        FactoryNativeState stocked = await ReadFactoryStateAsync(token);
        Require(inserted.Receipt.Status == "completed" && stocked.ChestPlates == 17 && stocked.ActorCount("iron-plate") == 93,
            "Chest insertion stock conservation failed.");
        var taken = await ExecuteAsync("take", new { entityId = chestId, inventory = "chest", item = "iron-plate", count = 7 }, 600, token);
        FactoryNativeState withdrawn = await ReadFactoryStateAsync(token);
        Require(taken.Receipt.Status == "completed" && withdrawn.ChestPlates == 10 && withdrawn.ActorCount("iron-plate") == 100,
            "Chest withdrawal stock conservation failed.");
        const string restrictChest = """
            /silent-command local c=game.surfaces.nauvis.find_entities_filtered{name="wooden-chest",force="factorio_agent"}[1]; c.get_inventory(defines.inventory.chest).set_bar(2); rcon.print("one-slot-fixture");
            """;
        Require((await session.CreateRcon().ExecuteAsync(restrictChest, token)).Trim() == "one-slot-fixture", "Fixture chest capacity not set.");
        var limited = await ExecuteAsync("insert", new { entityId = chestId, inventory = "chest", item = "iron-plate", count = 100 }, 600, token);
        FactoryNativeState filled = await ReadFactoryStateAsync(token);
        Require(limited.Receipt.Status == "partial" && limited.Receipt.Effects.GetProperty("transferred").GetInt32() == 90
            && filled.ChestPlates == 100 && filled.ActorCount("iron-plate") == 10,
            "Capacity-limited transfer was not partial or did not conserve stocks.");
        evidence.Add(new { check = "native-transfers-and-capacity", stocked, withdrawn, filled, receipt = limited.Receipt.Evidence });

        var furnace = await ExecuteAsync("build", new { item = "stone-furnace", position = new MapPosition(6, 0) }, 600, token);
        Require(furnace.Receipt.Status == "completed", "Furnace construction failed.");
        string furnaceId = furnace.Receipt.Effects.GetProperty("entityId").GetString()!;
        var fuel = await ExecuteAsync("insert", new { entityId = furnaceId, inventory = "fuel", item = "coal", count = 1 }, 600, token);
        var ore = await ExecuteAsync("insert", new { entityId = furnaceId, inventory = "input", item = "iron-ore", count = 5 }, 600, token);
        Require(fuel.Receipt.Status == "completed" && ore.Receipt.Status == "completed", "Furnace inputs were not transferred.");
        FactoryNativeState fueled = await ReadFactoryStateAsync(token);
        Require(fueled.ActorCount("coal") == 1 && fueled.ActorCount("iron-ore") == 0 && fueled.ActorCount("stone-furnace") == 0,
            "Furnace or input item cost was not paid.");
        long productionStart = fueled.Tick;
        FactoryNativeState smelted = fueled;
        for (int sample = 0; sample < 30 && smelted.FurnaceOutput < 5; sample++)
        {
            await Task.Delay(1000, token);
            smelted = await ReadFactoryStateAsync(token);
        }
        Require(smelted.FurnaceOutput == 5 && smelted.FurnaceInput == 0 && smelted.Tick - productionStart >= 600,
            "Native smelting did not complete at a plausible rate.");
        var output = await ExecuteAsync("take", new { entityId = furnaceId, inventory = "output", item = "iron-plate", count = 5 }, 600, token);
        FactoryNativeState collected = await ReadFactoryStateAsync(token);
        Require(output.Receipt.Status == "completed" && collected.FurnaceOutput == 0 && collected.ActorCount("iron-plate") == 15,
            "Smelted plate collection does not match native output.");
        evidence.Add(new { check = "native-smelting-and-collection", fueled, smelted, collected });

        var assembler = await ExecuteAsync("build", new { item = "assembling-machine-1", position = new MapPosition(-3.5, 3.5) }, 600, token);
        Require(assembler.Receipt.Status == "completed", "Assembler construction failed.");
        string assemblerId = assembler.Receipt.Effects.GetProperty("entityId").GetString()!;
        var recipe = await ExecuteAsync("set_recipe", new { entityId = assemblerId, recipe = "iron-gear-wheel" }, 600, token);
        Require(recipe.Receipt.Status == "completed" && (await ReadFactoryStateAsync(token)).AssemblerRecipe == "iron-gear-wheel",
            "Assembler recipe was not set natively.");
        var lockedResearch = await ExecuteAsync("research", new { technology = "automation" }, 600, token);
        Require(lockedResearch.Receipt.Status == "failed" && lockedResearch.Receipt.Error?.Code == "prerequisite_missing",
            "Research skipped its native crafting-trigger prerequisite.");
        FactoryNativeState researching = await ReadFactoryStateAsync(token);
        Require(researching.Research == "" && !researching.AutomationResearched,
            "Refused research must not select or unlock the technology.");
        var belt = await ExecuteAsync("build", new { item = "transport-belt", position = new MapPosition(0.5, 6.5) }, 600, token);
        Require(belt.Receipt.Status == "completed", "Belt construction failed.");
        var rotate = await ExecuteAsync("rotate", new { entityId = belt.Receipt.Effects.GetProperty("entityId").GetString()! }, 600, token);
        FactoryNativeState configured = await ReadFactoryStateAsync(token);
        Require(rotate.Receipt.Status == "completed" && configured.BeltDirection == 4, "Native belt rotation did not face east.");
        evidence.Add(new { check = "native-recipe-research-prerequisite-and-rotation", after = configured });
    }

    private async Task<FactoryNativeState> ReadFactoryStateAsync(CancellationToken token)
    {
        const string command = """
            /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; local items={}; for _,v in pairs(c.get_main_inventory().get_contents()) do items[v.name]=(items[v.name] or 0)+v.count end; local ch=s.find_entities_filtered{name="wooden-chest",force=c.force}; local f=s.find_entities_filtered{name="stone-furnace",force=c.force}[1]; local a=s.find_entities_filtered{name="assembling-machine-1",force=c.force}[1]; local b=s.find_entities_filtered{name="transport-belt",force=c.force}[1]; rcon.print(helpers.table_to_json{tick=game.tick,actorItems=items,chestCount=#ch,chestPlates=ch[1] and ch[1].get_inventory(defines.inventory.chest).get_item_count("iron-plate") or 0,furnaceInput=f and f.get_inventory(defines.inventory.furnace_source).get_item_count("iron-ore") or 0,furnaceOutput=f and f.get_inventory(defines.inventory.furnace_result).get_item_count("iron-plate") or 0,assemblerRecipe=a and a.get_recipe() and a.get_recipe().name or "",research=c.force.current_research and c.force.current_research.name or "",automationResearched=c.force.technologies.automation.researched,beltDirection=b and b.direction or -1});
            """;
        return JsonSerializer.Deserialize<FactoryNativeState>(await session.CreateRcon().ExecuteAsync(command, token), Protocol.Json)
            ?? throw new InvalidDataException("Missing native factory state.");
    }

    private async Task QualifyCombatAsync(CancellationToken token)
    {
        CombatNativeState before = await ReadCombatStateAsync(token);
        Require(before.Enemies == 0 && before.Rounds > 0 && before.Health > 0, "Combat fixture requires a living armed character and no existing nearby enemy.");
        const string setup = """
            /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; local b=s.create_entity{name="small-biter",position={4,4},force="enemy"}; assert(b and b.commandable); b.commandable.set_command{type=defines.command.attack,target=c,distraction=defines.distraction.none}; rcon.print(helpers.table_to_json({entityId=tostring(b.unit_number)}));
            """;
        using JsonDocument target = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(setup, token));
        string id = target.RootElement.GetProperty("entityId").GetString()!;
        evidence.Add(new { check = "synthetic-attacker", disqualifiedAsCampaign = true,
            setup = "Created one normal small biter near the character and issued its native attack command.", entityId = id });
        GameResponse seen = await game.ExecuteAsync(GameRequest.Create("observe"), token);
        Require(seen.Ok && seen.Data.GetProperty("enemies").ValueKind == JsonValueKind.Array
            && seen.Data.GetProperty("enemies").EnumerateArray().Any(e => e.GetProperty("id").GetString() == id),
            "The nearby attacking enemy was not visible to the standalone character.");
        var shot = await ExecuteAsync("shoot", new { entityId = id, ticks = 180 }, 600, token);
        CombatNativeState after = await ReadCombatStateAsync(token);
        Require(shot.Receipt.Status == "completed" && after.Rounds < before.Rounds && after.Health > 0 && after.Enemies == 0,
            "Native shooting lacks ammunition consumption, target removal, or actor survival evidence.");
        Require(shot.Receipt.Effects.GetProperty("roundsConsumed").GetInt32() == before.Rounds - after.Rounds,
            "The receipt does not count partially used magazines correctly.");
        evidence.Add(new { check = "native-visible-target-shooting", before, after, receipt = shot.Receipt.Evidence,
            scope = "One synthetic attacker; not a qualified autonomous defense policy." });
        const string hiddenSetup = """
            /silent-command local s=game.surfaces.nauvis; local p=s.find_non_colliding_position("small-biter",{112,0},4,0.5); assert(p); local b=s.create_entity{name="small-biter",position=p,force="enemy"}; assert(b and b.commandable); b.commandable.set_command{type=defines.command.stop}; rcon.print(helpers.table_to_json({position=b.position}));
            """;
        using JsonDocument hidden = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(hiddenSetup, token));
        MapPosition hiddenPosition = hidden.RootElement.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
        evidence.Add(new { check = "synthetic-hidden-target", disqualifiedAsCampaign = true, position = hiddenPosition });
        var unseen = await ExecuteAsync("shoot", new { position = hiddenPosition, name = "small-biter", ticks = 60 }, 600, token);
        CombatNativeState refused = await ReadCombatStateAsync(token);
        Require(unseen.Receipt.Status == "failed" && unseen.Receipt.Error?.Code == "target_not_visible" && refused.Rounds == after.Rounds,
            "An enemy outside normal character visibility was not refused before consuming ammunition.");
        evidence.Add(new { check = "native-hidden-target-refused", after = refused, receipt = unseen.Receipt.Evidence });
    }

    private async Task<CombatNativeState> ReadCombatStateAsync(CancellationToken token)
    {
        const string command = """
            /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; local rounds=0; if c then local inv=c.get_inventory(defines.inventory.character_ammo); for i=1,#inv do local stack=inv[i]; if stack.valid_for_read then rounds=rounds+(stack.count-1)*stack.prototype.magazine_size+stack.ammo end end end; rcon.print(helpers.table_to_json({tick=game.tick,health=c and c.health or 0,rounds=rounds,enemies=s.count_entities_filtered{area={{-8,-8},{12,8}},type="unit",force="enemy"}}));
            """;
        return JsonSerializer.Deserialize<CombatNativeState>(await session.CreateRcon().ExecuteAsync(command, token), Protocol.Json)
            ?? throw new InvalidDataException("Missing native combat state.");
    }

    public async Task<NativeState> ReadStateAsync(CancellationToken token)
    {
        const string command = """
            /silent-command local s=game.surfaces["nauvis"]; local chars=s.find_entities_filtered{type="character"}; assert(#chars==1,"Expected one agent character"); local c=chars[1]; local items={}; for _,v in pairs(c.get_inventory(defines.inventory.character_main).get_contents()) do items[v.name]=(items[v.name] or 0)+v.count end; rcon.print(helpers.table_to_json{tick=game.tick,position=c.position,items=items,health=c.health,playersConnected=#game.connected_players,peaceful=s.peaceful_mode,pollution=game.map_settings.pollution.enabled,expansion=game.map_settings.enemy_expansion.enabled});
            """;
        string json = await session.CreateRcon().ExecuteAsync(command, token);
        return JsonSerializer.Deserialize<NativeState>(json, Protocol.Json) ?? throw new InvalidDataException("No native evidence returned.");
    }

    private Task SaveReportAsync(string path, bool passed, string? error, CancellationToken token) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { kind = "synthetic-native-qualification", passed,
            error, seed = session.Seed, isAutonomousCampaign = false, recordedUtc = DateTime.UtcNow, evidence },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

public sealed record NativeState(long Tick, MapPosition Position, Dictionary<string, int> Items,
    double Health, int PlayersConnected, bool Peaceful, bool Pollution, bool Expansion)
{
    public int Count(string item) => Items.GetValueOrDefault(item);
}

public sealed record FactoryNativeState(long Tick, Dictionary<string, int> ActorItems, int ChestCount,
    int ChestPlates, int FurnaceInput, int FurnaceOutput, string AssemblerRecipe, string Research,
    bool AutomationResearched, int BeltDirection)
{
    public int ActorCount(string item) => ActorItems.GetValueOrDefault(item);
}

public sealed record CombatNativeState(long Tick, double Health, int Rounds, int Enemies);

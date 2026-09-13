using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Prepared construction and concurrent smelting; never a normal campaign.</summary>
public sealed class FurnaceFleetQualification(RuntimeSession session, string item = "steel-plate")
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Furnace fleet qualification requires an explicit fixture session.");
        if (item is not ("steel-plate" or "stone-brick")) throw new ArgumentException("Fleet fixture supports steel-plate or stone-brick.", nameof(item));
        bool raw = item == "stone-brick";
        string ingredient = raw ? "stone" : "iron-plate";
        int target = raw ? 200 : 50;
        string read = $$"""
            local stats=f.get_item_production_statistics(s); local function snapshot() return {tick=game.tick,stock=c.get_item_count('{{item}}'),ingredient=c.get_item_count('{{ingredient}}'),furnaces=s.count_entities_filtered{type='furnace',force=f},produced=stats.get_input_count('{{item}}'),ingredientConsumed=stats.get_output_count('{{ingredient}}'),stoneConsumed=stats.get_output_count('stone'),players=#game.connected_players,characterId=tostring(c.unit_number),speed=game.speed} end;
            """;
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"furnace-fleet-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = $"Prepared furnace fleet: build a second furnace and produce {target} {item} from supplied inputs." }), token);
            Require(mark.Ok, "Fixture marking rejected.");
            string supply = raw
                ? "assert(c.insert{name='stone',count=395}==395);"
                : "assert(c.insert{name='iron-plate',count=250}==250); assert(c.insert{name='stone',count=5}==5);";
            string engage = raw
                ? "assert(furnace.get_inventory(defines.inventory.furnace_source).insert{name='stone',count=10}==10); assert(furnace.get_fuel_inventory().insert{name='coal',count=1}==1);"
                : "";
            string prepare = "/silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); for _,p in pairs(game.connected_players) do assert(p.character==c) end; game.speed=4; for _,e in pairs(s.find_entities_filtered{type='furnace',force=f}) do e.destroy() end; for _,e in pairs(s.find_entities_filtered{area={{-36,-36},{36,36}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-36,36 do for y=-36,36 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.get_main_inventory().clear(); f.technologies['steel-processing'].researched=true; "
                + supply + " assert(c.insert{name='coal',count=100}==100); local furnace=s.create_entity{name='stone-furnace',position={8,0},force=f}; assert(furnace); "
                + read + " local before=snapshot(); " + engage + " rcon.print(helpers.table_to_json{before=before,furnaceId=tostring(furnace.unit_number)})";
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "prepared-fleet-inputs", native = setup.RootElement.Clone() });
            JsonElement before = setup.RootElement.GetProperty("before").Clone();
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var recipe = catalog.Recipes.Single(r => r.Name == item);
            double cycles = Math.Ceiling(target / recipe.Products.Single().Amount!.Value);
            double singleFurnaceTicks = cycles
                * recipe.EnergySeconds / catalog.Machines["stone-furnace"].CraftingSpeed * 60;
            var result = await new ProductionGoalExecutor(game, new ControllerJournal(journalPath)).RunAsync(item, target, token);
            JsonElement after = await ReadAsync();
            int manual = 0, simultaneous = 0;
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "submission" && data.GetProperty("kind").GetString() == "mine") manual++;
                if (type == "furnace-fleet-observation") simultaneous = Math.Max(simultaneous,
                    data.GetProperty("machines").EnumerateArray().Count(m => m.GetProperty("inProcess").GetBoolean()));
            }
            evidence.Add(new { check = "native-concurrent-smelting", item, target, before, result, after, manual, simultaneous, singleFurnaceTicks });
            Require(result.FinalStock == target && after.GetProperty("stock").GetInt64() == target, "Product target is not in native carried stock.");
            long recipeInput = checked((long)(cycles * recipe.Ingredients.Single().Amount!.Value));
            Require(Delta("produced") == target && Delta("ingredientConsumed") == recipeInput + (raw ? 5 : 0), "Native outputs and costs do not reconcile.");
            Require(Delta("furnaces") == 1 && Delta("stoneConsumed") == (raw ? recipeInput : 0) + 5, "Expansion did not build exactly one furnace from five stone.");
            Require(manual == 0 && simultaneous >= 2, "The batch did not run in concurrent furnaces without manual mining.");
            Require(after.GetProperty("tick").GetInt64() - before.GetProperty("tick").GetInt64() < singleFurnaceTicks,
                "The prepared fleet did not beat the native work time of a single ordinary stone furnace.");
            passed = true;
            return path;
            long Delta(string key) => after.GetProperty(key).GetInt64() - before.GetProperty(key).GetInt64();
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-furnace-fleet-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement> ReadAsync()
        {
            string command = "/silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; "
                + read + " rcon.print(helpers.table_to_json(snapshot()))";
            using var doc = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(command, token));
            return doc.RootElement.Clone();
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed class ArmedCraftingQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Armed crafting requires an explicit fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        var operations = new OperationClient(game);
        string path = Path.Combine(session.Directory, $"armed-crafting-qualification-{Guid.NewGuid():N}.json");
        var journal = new ControllerJournal(Path.ChangeExtension(path, ".jsonl"));
        var evidence = new List<object>();
        string? active = null;
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new { reason = "Prepared armed craft: partial magazines, native delivery, cancellation and capacity. Not a campaign." }), token);
            if (!mark.Ok) throw new GameRpcException(mark.Error!);
            evidence.Add(new { check = "preparation", native = await CommandAsync("""
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-40,-40},{40,40}}}) do if e~=c then e.destroy() end end; assert(c.teleport({0,0})); c.health=c.max_health; local main=c.get_main_inventory(); main.clear(); local guns=c.get_inventory(defines.inventory.character_guns); guns.clear(); guns[1].set_stack{name='pistol',count=1}; local ammo=c.get_inventory(defines.inventory.character_ammo); ammo.clear(); ammo[1].set_stack{name='firearm-magazine',count=2}; ammo[1].ammo=4; c.get_inventory(defines.inventory.character_armor).clear(); main[1].set_stack{name='firearm-magazine',count=2}; main[1].ammo=6; assert(main.insert{name='iron-plate',count=160}==160); c.force.recipes['firearm-magazine'].enabled=true; c.force.recipes.pistol.enabled=true; c.force.recipes['light-armor'].enabled=true; rcon.print(helpers.table_to_json{tick=game.tick,character=tostring(c.unit_number),players=#game.connected_players})
                """) });
            var before = await ReadAsync();
            Require(Number(before, "mainRounds") == 16 && Number(before, "equippedRounds") == 14, "Partial magazines not established.");
            var submitted = await SubmitAsync("firearm-magazine", 5);
            var receipt = await operations.WaitAsync(submitted.OperationId, TimeSpan.FromSeconds(20), token);
            active = null;
            await journal.AppendAsync("receipt", receipt, token);
            var after = await ReadAsync();
            evidence.Add(new { check = "armed-native-craft", before, after, receipt = receipt.Evidence });
            Require(receipt.Status == "completed" && Number(after, "mainAmmo") > Number(before, "mainAmmo"),
                "Native crafted ammunition did not reach the main inventory.");
            CheckAmmo(before, after, 5);
            Require(receipt.Effects.GetProperty("products").GetProperty("firearm-magazine").GetInt32() == 5,
                "The craft receipt does not report the delivered products.");
            await operations.SubmitAsync(submitted, token);
            var replay = await ReadAsync();
            Require(Number(replay, "totalRounds") == Number(after, "totalRounds") && Number(replay, "ammoProduction") == Number(after, "ammoProduction"),
                "Duplicate submission duplicated ammunition or statistics.");
            evidence.Add(new { check = "duplicate-receipt", replay });

            before = await ReadAsync();
            var stockGoal = await new ProductionGoalExecutor(game, journal).RunAsync("firearm-magazine", 12, token);
            after = await ReadAsync();
            long producedForStock = (Number(after, "totalRounds") - Number(before, "totalRounds")) / 10;
            evidence.Add(new { check = "verified-carried-stock-goal", before, after, stockGoal });
            Require(stockGoal.FinalStock >= 12 && Number(after, "mainAmmo") >= 12, "The carried-stock goal accepted a craft count instead of observed stock.");
            CheckAmmo(before, after, producedForStock);

            before = await ReadAsync();
            submitted = await SubmitAsync("firearm-magazine", 20);
            for (int attempt = 0; attempt < 80; attempt++)
            {
                after = await ReadAsync();
                if (Number(after, "mainAmmo") > Number(before, "mainAmmo")) break;
                await Task.Delay(100, token);
            }
            receipt = await operations.CancelAsync(submitted.OperationId, token);
            active = null;
            await journal.AppendAsync("receipt", receipt, token);
            after = await ReadAsync();
            long produced = (Number(after, "totalRounds") - Number(before, "totalRounds")) / 10;
            evidence.Add(new { check = "partial-cancellation", before, after, produced, receipt = receipt.Evidence });
            Require(receipt.Status == "cancelled" && produced is > 0 and < 20 && Number(after, "queue") == 0, "Craft was not cancelled after partial output.");
            CheckAmmo(before, after, produced);
            await operations.CancelAsync(submitted.OperationId, token);
            replay = await ReadAsync();
            Require(Number(replay, "totalRounds") == Number(after, "totalRounds") && Number(replay, "ammoProduction") == Number(after, "ammoProduction"),
                "Repeated cancellation duplicated credit.");

            // Native insertable count can merge partial magazines, which cannot preserve delivered item counts.
            await CommandAsync("""
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; local main=c.get_main_inventory(); main.clear(); main[1].set_stack{name='firearm-magazine',count=2}; main[1].ammo=6; main[2].set_stack{name='iron-plate',count=80}; for i=3,#main do main[i].set_stack{name='stone',count=50} end; local ammo=c.get_inventory(defines.inventory.character_ammo)[1]; ammo.set_stack{name='firearm-magazine',count=2}; ammo.ammo=4; assert(main.get_insertable_count('firearm-magazine')>0); rcon.print(helpers.table_to_json{tick=game.tick})
                """);
            before = await ReadAsync();
            submitted = await SubmitAsync("firearm-magazine", 1);
            receipt = await operations.WaitAsync(submitted.OperationId, TimeSpan.FromSeconds(10), token);
            active = null;
            after = await ReadAsync();
            evidence.Add(new { check = "partial-magazines-without-separate-capacity", before, after, receipt = receipt.Evidence });
            Require(receipt.Status == "failed" && receipt.Error?.Code == "craft_output_capacity" && Number(after, "iron") == Number(before, "iron")
                && Number(after, "totalRounds") == Number(before, "totalRounds"), "Partial-stack delivery capacity was not refused before consuming inputs.");

            // A full bag must be refused before native inputs are charged or equipment is filled.
            await CommandAsync("""
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; local main=c.get_main_inventory(); main.clear(); main[1].set_stack{name='iron-plate',count=80}; for i=2,#main do main[i].set_stack{name='stone',count=50} end; rcon.print(helpers.table_to_json{tick=game.tick,capacity=main.get_insertable_count('firearm-magazine')})
                """);
            before = await ReadAsync();
            submitted = await SubmitAsync("firearm-magazine", 1);
            receipt = await operations.QueryAsync(submitted.OperationId, token);
            after = await ReadAsync();
            evidence.Add(new { check = "full-main-inventory", before, after, receipt = receipt.Evidence });
            Require(receipt.Status == "failed" && receipt.Error?.Code == "craft_output_capacity" && Number(after, "queue") == 0
                && Number(after, "iron") == Number(before, "iron") && Number(after, "totalRounds") == Number(before, "totalRounds"),
                "Insufficient output capacity was not refused before crafting.");
            active = null;

            // Guns and armor may also be equipped automatically; production still requests carried stock.
            await CommandAsync("""
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; c.get_inventory(defines.inventory.character_guns).clear(); c.get_inventory(defines.inventory.character_ammo).clear(); local main=c.get_main_inventory(); main.clear(); assert(main.insert{name='iron-plate',count=100}==100); assert(main.insert{name='copper-plate',count=20}==20); rcon.print(helpers.table_to_json{tick=game.tick})
                """);
            foreach (var item in new[] { "pistol", "light-armor" })
            {
                submitted = await SubmitAsync(item, 1);
                receipt = await operations.WaitAsync(submitted.OperationId, TimeSpan.FromSeconds(20), token);
                active = null;
                after = await ReadAsync();
                evidence.Add(new { check = "carried-equipment-craft", item, after, receipt = receipt.Evidence });
                Require(receipt.Status == "completed" && after.GetProperty("mainItems").GetProperty(item).GetInt32() == 1,
                    "Crafted equipment was not delivered into main stock.");
            }
            await CommandAsync("""
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; c.get_main_inventory().remove{name='pistol',count=1}; local gun=c.get_inventory(defines.inventory.character_guns)[1]; gun.set_stack{name='pistol',count=1}; gun.health=0.4; rcon.print(helpers.table_to_json{tick=game.tick})
                """);
            submitted = await SubmitAsync("pistol", 1);
            receipt = await operations.WaitAsync(submitted.OperationId, TimeSpan.FromSeconds(20), token);
            active = null;
            var preservedGun = await CommandAsync("""
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; local gun=c.get_inventory(defines.inventory.character_guns)[1]; local carried=c.get_main_inventory().find_item_stack('pistol'); rcon.print(helpers.table_to_json{tick=game.tick,equippedHealth=gun.valid_for_read and gun.health or 0,carriedHealth=carried and carried.health or 0,carriedCount=c.get_main_inventory().get_item_count('pistol')})
                """);
            evidence.Add(new { check = "preexisting-gun-preserved", preservedGun, receipt = receipt.Evidence });
            Require(receipt.Status == "completed" && Math.Abs(preservedGun.GetProperty("equippedHealth").GetDouble() - .4) < .0001
                && preservedGun.GetProperty("carriedHealth").GetDouble() == 1 && Number(preservedGun, "carriedCount") == 1,
                "Delivery exchanged the preexisting equipped gun for the newly crafted gun.");
            Require(after.GetProperty("pilotAttached").GetBoolean(), "Connected pilot is not attached to the actor.");
            passed = true;
            return path;
        }
        catch (Exception error) { evidence.Add(new { check = "failure", error = error.Message }); throw; }
        finally
        {
            if (active is not null)
            {
                try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await operations.CancelAsync(active, cleanup.Token); }
                catch (Exception error) { evidence.Add(new { check = "cleanup-unconfirmed", error = error.Message }); }
            }
            await LocalJson.WriteAsync(path, new { kind = "prepared-armed-crafting-qualification", passed, isAutonomousCampaign = false, evidence }, CancellationToken.None);
        }

        async Task<OperationSubmission> SubmitAsync(string recipe, int count)
        {
            var state = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
            if (!state.Ok) throw new GameRpcException(state.Error!);
            var submitted = OperationSubmission.Create(state.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!, "craft", new { recipe, count }, state.Tick + 18000);
            active = submitted.OperationId;
            await journal.AppendAsync("submission", submitted, token);
            await operations.SubmitAsync(submitted, token);
            return submitted;
        }
        async Task<JsonElement> CommandAsync(string command)
        {
            string json = await native.ExecuteAsync(command, token);
            if (!json.TrimStart().StartsWith('{')) throw new InvalidDataException("Native armed craft fixture failed: " + json[..Math.Min(json.Length, 1500)]);
            using var value = JsonDocument.Parse(json);
            return value.RootElement.Clone();
        }
        Task<JsonElement> ReadAsync() => CommandAsync("""
            /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; assert(c); local main=c.get_main_inventory(); local ammo=c.get_inventory(defines.inventory.character_ammo); local function rounds(inv) local n=0; for i=1,#inv do local st=inv[i]; if st.valid_for_read and st.prototype.type=='ammo' then n=n+(st.count-1)*st.prototype.magazine_size+st.ammo end end; return n end; local stats=c.force.get_item_production_statistics(c.surface); local attached=true; for _,p in pairs(game.connected_players) do if p.character~=c then attached=false end end; rcon.print(helpers.table_to_json{tick=game.tick,character=tostring(c.unit_number),players=#game.connected_players,pilotAttached=attached,mainAmmo=main.get_item_count('firearm-magazine'),equippedAmmo=ammo.get_item_count('firearm-magazine'),mainRounds=rounds(main),equippedRounds=rounds(ammo),totalRounds=rounds(main)+rounds(ammo),iron=main.get_item_count('iron-plate'),ammoProduction=stats.get_input_count('firearm-magazine'),ironConsumed=stats.get_output_count('iron-plate'),queue=c.crafting_queue_size,mainItems={pistol=main.get_item_count('pistol'),['light-armor']=main.get_item_count('light-armor')}})
            """);
    }

    private static long Number(JsonElement value, string name) => value.GetProperty(name).GetInt64();
    private static void CheckAmmo(JsonElement before, JsonElement after, long produced)
    {
        Require(Number(after, "equippedAmmo") == Number(before, "equippedAmmo") && Number(after, "equippedRounds") >= Number(before, "equippedRounds"),
            "Preexisting equipment ammunition was removed.");
        Require(Number(after, "totalRounds") - Number(before, "totalRounds") == produced * 10
            && Number(before, "iron") - Number(after, "iron") == produced * 4
            && Number(after, "ammoProduction") - Number(before, "ammoProduction") == produced,
            "Crafted native rounds, ingredient costs or production statistics do not balance.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

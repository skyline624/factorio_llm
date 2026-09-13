using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepared hostile encounters verify carried equipment and native ammunition conservation.</summary>
public sealed class EquipmentQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Equipment qualification requires an explicit fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string path = Path.Combine(session.Directory, $"equipment-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        string? ownedWait = null;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared carried pistol, partially used magazines and three attacks; native equipment transfers. Not a campaign." }), token);
            if (!marked.Ok) throw new GameRpcException(marked.Error!);
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-32,-32},{32,32}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-32,32 do for y=-32,32 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_inventory(defines.inventory.character_guns).clear(); c.get_inventory(defines.inventory.character_ammo).clear(); local main=c.get_main_inventory(); main.clear(); assert(main.insert{name='pistol',count=1}==1 and main.insert{name='firearm-magazine',count=3}==3); main.find_item_stack('firearm-magazine').ammo=4; rcon.print(helpers.table_to_json{tick=game.tick,character=tostring(c.unit_number),players=#game.connected_players})
                """;
            evidence.Add(new { check = "explicit-preparation", native = await NativeAsync(prepare) });
            JsonElement baseline = await ReadAsync();
            Require(baseline.GetProperty("rounds").GetInt32() == 24, "Prepared partial magazines were not measured exactly.");
            for (int phase = 0; phase < 3; phase++)
            {
                if (phase == 1)
                {
                    // Move existing stacks back into the bag, preserving partially used magazines and gun identity.
                    const string unload = """
                        /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; local main=c.get_main_inventory(); for _,inv in ipairs{c.get_inventory(defines.inventory.character_guns),c.get_inventory(defines.inventory.character_ammo)} do for i=1,#inv do for j=1,#main do if inv[i].valid_for_read then main[j].transfer_stack(inv[i]) end end; assert(not inv[i].valid_for_read) end end; rcon.print(helpers.table_to_json{tick=game.tick})
                        """;
                    evidence.Add(new { check = "prepared-unload-existing-equipment", native = await NativeAsync(unload) });
                }
                if (phase == 2)
                {
                    const string deselect = """
                        /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; assert(not c.get_inventory(defines.inventory.character_guns)[3].valid_for_read); c.selected_gun_index=3; rcon.print(helpers.table_to_json{tick=game.tick})
                        """;
                    evidence.Add(new { check = "prepared-selection-of-empty-slot", native = await NativeAsync(deselect) });
                }
                JsonElement before = await ReadAsync();
                var observed = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe"), token));
                var wait = OperationSubmission.Create(observed.Scope, "wait", new { ticks = 3600 }, observed.Tick + 4000);
                await journal.AppendAsync("submission", wait, token);
                ownedWait = wait.OperationId;
                var waiting = await new OperationClient(game).SubmitAsync(wait, token);
                await journal.AppendAsync("receipt", waiting, token);
                Require(!waiting.IsTerminal, "Prepared production wait did not begin.");
                const string attack = """
                    /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; local e=c.surface.create_entity{name='small-biter',position={c.position.x+8,c.position.y},force=game.forces.enemy}; assert(e); e.commandable.set_command{type=defines.command.attack,target=c,distraction=defines.distraction.none}; rcon.print(helpers.table_to_json{tick=game.tick,enemyId=tostring(e.unit_number)})
                    """;
                JsonElement spawned = await NativeAsync(attack);
                string enemyId = spawned.GetProperty("enemyId").GetString()!;
                var defense = new DefenseController(game, journal);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    while (true)
                    {
                        await defense.StepAsync(deadline.Token);
                        JsonElement state = await ReadAsync(enemyId);
                        Require(state.GetProperty("health").GetDouble() > 0, "The actor died during equipment qualification.");
                        if (!state.GetProperty("enemyAlive").GetBoolean()) break;
                        await Task.Delay(100, deadline.Token);
                    }
                }
                finally { await defense.StopOwnedActionAsync(token); }
                var stopped = await new OperationClient(game).QueryAsync(wait.OperationId, token);
                ownedWait = null;
                JsonElement after = await ReadAsync(enemyId);
                Require(stopped.Status == "cancelled", "The attack did not preempt the previous work.");
                Require(after.GetProperty("rounds").GetInt32() < before.GetProperty("rounds").GetInt32(), "No real ammunition was consumed.");
                Require(after.GetProperty("guns").GetInt32() == 1 && after.GetProperty("incarnation").GetInt64() == baseline.GetProperty("incarnation").GetInt64()
                    && after.GetProperty("character").GetString() == baseline.GetProperty("character").GetString()
                    && after.GetProperty("pilotAttached").GetBoolean() && after.GetProperty("players").GetInt32() == baseline.GetProperty("players").GetInt32(),
                    "Equipment or pilot identity changed during the encounter.");
                evidence.Add(new { check = "prepared-attack", phase, before, spawned, after, interrupted = stopped });
            }
            int transfers = 0, selections = 0, shotRounds = 0;
            OperationSubmission? firstEquipment = null;
            var receipts = new Dictionary<string, OperationReceipt>();
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                if (row.RootElement.GetProperty("type").GetString() == "submission"
                    && row.RootElement.GetProperty("data").GetProperty("kind").GetString() == "equip")
                    firstEquipment ??= row.RootElement.GetProperty("data").Deserialize<OperationSubmission>(Protocol.Json);
                if (row.RootElement.GetProperty("type").GetString() is not ("receipt" or "final-receipt")) continue;
                var data = row.RootElement.GetProperty("data");
                var receipt = OperationReceipt.Parse(data, data.GetProperty("operationId").GetString()!);
                if (receipt.IsTerminal) receipts[receipt.OperationId] = receipt;
            }
            foreach (var receipt in receipts.Values)
            {
                if (receipt.Kind == "shoot") shotRounds += receipt.Effects.GetProperty("roundsConsumed").GetInt32();
                if (receipt.Kind == "select_weapon" && receipt.Status == "completed") selections++;
                if (receipt.Kind != "equip") continue;
                Require(receipt.Status == "completed" && receipt.Effects.GetProperty("transferred").GetInt32() == receipt.Effects.GetProperty("requested").GetInt32(),
                    "An equipment transfer was incomplete.");
                var before = receipt.Effects.GetProperty("equipmentBefore");
                var after = receipt.Effects.GetProperty("equipmentAfter");
                Require(before.GetProperty("mainRounds").GetInt32() + before.GetProperty("loadedRounds").GetInt32()
                    == after.GetProperty("mainRounds").GetInt32() + after.GetProperty("loadedRounds").GetInt32(), "Equipment transfer changed ammunition rounds.");
                transfers++;
            }
            JsonElement final = await ReadAsync();
            Require(transfers == 4 && selections == 1 && shotRounds > 0
                && final.GetProperty("rounds").GetInt32() + shotRounds == 24, "Global native ammunition accounting did not close.");
            evidence.Add(new { check = "equipment-and-ammunition-conservation", transfers, selections, shotRounds, baseline, final });
            var duplicate = await new OperationClient(game).SubmitAsync(firstEquipment!, token);
            EquipmentReceipt.Validate(firstEquipment!, duplicate);
            var observedFinal = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe"), token));
            var stale = OperationSubmission.Create(observedFinal.Scope, "equip", firstEquipment!.Args, observedFinal.Tick + 600);
            var rejected = await new OperationClient(game).SubmitAsync(stale, token);
            JsonElement unchanged = await ReadAsync();
            Require(rejected.IsTerminal && rejected.Error?.Code == "equipment_source_changed"
                && unchanged.GetProperty("rounds").GetInt32() == final.GetProperty("rounds").GetInt32()
                && unchanged.GetProperty("guns").GetInt32() == 1, "Duplicate identity or a stale source changed equipment stock.");
            evidence.Add(new { check = "native-deduplication-and-stale-source", duplicate, rejected, unchanged });
            passed = true;
            return path;
        }
        finally
        {
            if (ownedWait is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var operations = new OperationClient(game);
                    var receipt = await operations.QueryAsync(ownedWait, cleanup.Token);
                    if (!receipt.IsTerminal) receipt = await operations.CancelAsync(ownedWait, cleanup.Token);
                    await journal.AppendAsync("final-receipt", receipt, cleanup.Token);
                }
                catch (Exception error) { evidence.Add(new { check = "wait-cleanup-unconfirmed", error = error.Message }); }
            }
            await LocalJson.WriteAsync(path, new { kind = "prepared-equipment-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement> NativeAsync(string command)
        {
            string response = await native.ExecuteAsync(command, token);
            if (!response.TrimStart().StartsWith('{')) throw new InvalidDataException("Native fixture command failed: " + response[..Math.Min(response.Length, 1500)]);
            using var document = JsonDocument.Parse(response);
            return document.RootElement.Clone();
        }
        Task<JsonElement> ReadAsync(string enemyId = "0") => NativeAsync("""
            /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; assert(c); local rounds,guns=0,0; for _,inv in ipairs{c.get_main_inventory(),c.get_inventory(defines.inventory.character_ammo),c.get_inventory(defines.inventory.character_guns)} do for i=1,#inv do local a=inv[i]; if a.valid_for_read then if a.prototype.type=='ammo' then rounds=rounds+(a.count-1)*a.prototype.magazine_size+a.ammo elseif a.prototype.type=='gun' then guns=guns+a.count end end end end; local attached=true; for _,p in pairs(game.connected_players) do if p.character~=c then attached=false end end; local o=helpers.json_to_table(remote.call('factorio_agent','execute',helpers.table_to_json{protocolVersion=1,requestId='equipment-native-proof',action='observe',arguments={radius=1,limit=1}})); assert(o.ok); local enemy; for _,candidate in ipairs(c.surface.find_entities_filtered{type='unit',force=game.forces.enemy}) do if candidate.unit_number==ENEMY_ID then enemy=candidate; break end end; rcon.print(helpers.table_to_json{tick=game.tick,character=tostring(c.unit_number),incarnation=o.data.scope.incarnation,health=c.health,rounds=rounds,guns=guns,enemyAlive=enemy and enemy.valid or false,players=#game.connected_players,pilotAttached=attached,weapon=o.data.agent.weapon,loadout=o.data.agent.loadout})
            """.Replace("ENEMY_ID", long.Parse(enemyId, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

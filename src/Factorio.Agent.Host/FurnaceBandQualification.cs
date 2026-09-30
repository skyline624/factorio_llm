using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: an injected power source, steel research and supplied stone furnaces, inserters, chests, poles,
/// iron plates and coal. Proves that steel automation plans and builds a furnace band cell, that logistics fuels it only
/// through its input chest and collects steel from its output chest, with no hand crafting, and that only a chest supply
/// below a quarter stack reports coal short once the actor carries none; not a campaign.
/// </summary>
public sealed class FurnaceBandQualification(RuntimeSession session)
{
    private const string Steel = "steel-plate";
    private const int BufferCrafts = 40;

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Furnace band qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"furnace-band-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            // The mod accepts at most 128 bytes of reason.
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Injected energy interface, steel research, cell items, 250 plates, 50 coal. Furnace band test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); for _,t in pairs{'steam-power','electronics','automation','steel-processing'} do f.technologies[t].researched=true end; for name,count in pairs{['stone-furnace']=2,inserter=4,['iron-chest']=4,['small-electric-pole']=12,['iron-plate']=250,coal=50} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=2000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,steel=f.recipes['steel-plate'].enabled})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            evidence.Add(new { check = "explicit-furnace-band-preparation", native = setup.RootElement.Clone() });

            var plan = await new FactoryDirector(game, journal, session.Directory).AutomateAsync(Steel, 3, token);
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var registered = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            var cells = registered.Cells;
            evidence.Add(new { check = "steel-automation", plan, cells });
            var stage = plan.Stages.SingleOrDefault();
            Require(stage is { Kind: FurnaceCellPlanner.Kind, Recipe: Steel, MachineItem: "stone-furnace", Machines: 1 }
                && plan.RawPerMinute.Keys.SequenceEqual(["iron-plate"]), "Steel was not planned as one stone furnace band stage fed with plates.");
            var cell = cells.SingleOrDefault(c => c.Kind == FurnaceCellPlanner.Kind && c.Recipe == Steel && c.Status == "ready")
                ?? throw new InvalidDataException("No ready steel furnace cell was built.");
            // Link poles are recorded as link-n roles so maintenance can rebuild them; they are not cell parts.
            Require(cell.Entities.Keys.Count(role => !role.StartsWith("link-", StringComparison.Ordinal)) == 6,
                "The furnace cell was not completed with its native entities.");
            string furnace = cell.Entities["machine"], inputChest = cell.Entities["input-chest"];

            var logistics = new FactoryLogistics(game, journal, session.Directory);
            var reserve = (await FurnaceBandFuel.ReservesAsync(game, catalog, [cell], BufferCrafts, FactoryLogistics.Fuel, token))[cell.Id];
            var first = await logistics.ServiceAsync(BufferCrafts, token);
            // The steel target (3 a minute on one cell) buffers ten minutes of crafts, below the caller's 40-craft maximum.
            int buffered = FactoryLogistics.BufferCrafts(FactoryLogistics.CellShares(catalog, registered), Steel, BufferCrafts);
            evidence.Add(new { check = "planned-buffer", buffered, maximum = BufferCrafts });
            Require(buffered == 30 && first.Supplied.GetValueOrDefault("iron-plate") == 5 * buffered,
                "The input chest was not stocked with plates for ten minutes of the planned steel rate.");
            Require(first.Supplied.GetValueOrDefault(FactoryLogistics.Fuel) == reserve, "The input chest was not stocked with the native fuel reserve.");
            await using (var controller = new SpatialController(game, journal))
                Require((await controller.WorkAsync("wait", new { ticks = 4800 }, 5100, token: token)).Status == "completed", "Wait failed.");
            var loaded = Loaded(await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token), furnace, inputChest);
            var second = await logistics.ServiceAsync(BufferCrafts, token);
            evidence.Add(new { check = "furnace-chest-logistics", reserve, first, loaded, second });
            // 16 s steel crafts at crafting speed 1: about four in 80 seconds once the first five plates are in.
            Require(second.Collected.GetValueOrDefault(Steel) >= 3, "The furnace cell did not deliver steel to its output chest.");
            Require(loaded.ChestFuel < reserve && loaded.FurnaceFuel + loaded.BurningJoules > 0,
                "The input inserter did not load the furnace's fuel slot from the chest.");

            // With no coal carried, a chest below its reserve but above a quarter stack is not worth a trip; an emptied one is.
            long quarter = catalog.Items[FactoryLogistics.Fuel].StackSize / 4;
            Require(reserve - 2 > quarter, "The steel fuel reserve leaves no margin above a quarter stack.");
            var kept = await DrainAsync(inputChest, reserve - 2, token);
            var third = await logistics.ServiceAsync(BufferCrafts, token);
            var emptied = await DrainAsync(inputChest, 0, token);
            var fourth = await logistics.ServiceAsync(BufferCrafts, token);
            evidence.Add(new { check = "furnace-fuel-shortfall", quarter, kept, third, emptied, fourth });
            Require(kept.GetProperty("carried").GetInt64() == 0 && kept.GetProperty("chest").GetInt64() < reserve
                && third.Shortfall.GetValueOrDefault(FactoryLogistics.Fuel) == 0, "A band furnace chest above a quarter stack asked for coal.");
            Require(fourth.Shortfall.GetValueOrDefault(FactoryLogistics.Fuel) > 0, "An emptied band furnace chest did not report coal short.");

            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var submissions = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data")).ToArray();
                string? Target(JsonElement data) => data.GetProperty("args").TryGetProperty("entityId", out var id) ? id.GetString() : null;
                string? Item(JsonElement data) => data.GetProperty("args").TryGetProperty("item", out var item) ? item.GetString() : null;
                var inserts = submissions.Where(s => s.GetProperty("kind").GetString() == "insert").ToArray();
                int crafts = submissions.Count(s => s.GetProperty("kind").GetString() == "craft");
                int mines = submissions.Count(s => s.GetProperty("kind").GetString() == "mine");
                int handFuel = inserts.Count(s => Target(s) == furnace);
                int chestFuel = inserts.Count(s => Target(s) == inputChest && Item(s) == FactoryLogistics.Fuel);
                evidence.Add(new { check = "no-manual-production", crafts, mines, handFuel, chestFuel });
                Require(crafts == 0 && mines == 0, "Supplied construction triggered manual production.");
                Require(handFuel == 0 && chestFuel >= 1, "The furnace was fuelled by hand instead of through its input chest.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-furnace-band-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }
    }

    /// <summary>Fixture step: the actor drops its coal and the chest keeps at most <paramref name="keep"/> of it.</summary>
    private async Task<JsonElement> DrainAsync(string chest, long keep, CancellationToken token)
    {
        long id = long.Parse(chest, NumberStyles.None, CultureInfo.InvariantCulture);
        string response = await session.CreateRcon().ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $$"""
            /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; local bag=c.get_main_inventory(); local carried=bag.get_item_count('coal'); if carried>0 then bag.remove{name='coal',count=carried} end; local chest; for _,e in pairs(c.surface.find_entities_filtered{force=c.force,type='container'}) do if e.unit_number=={{id}} then chest=e end end; assert(chest); local i=chest.get_inventory(defines.inventory.chest); local n=i.get_item_count('coal'); if n>{{keep}} then i.remove{name='coal',count=n-{{keep}}} end; rcon.print(helpers.table_to_json{tick=game.tick,carried=bag.get_item_count('coal'),chest=i.get_item_count('coal')})
            """), token);
        if (!response.TrimStart().StartsWith('{')) throw new InvalidDataException("Native furnace fuel fixture failed: " + response[..Math.Min(response.Length, 1500)]);
        using var value = JsonDocument.Parse(response);
        return value.RootElement.Clone();
    }

    private sealed record FurnaceLoad(long ChestFuel, long FurnaceFuel, double BurningJoules, IReadOnlyDictionary<string, long> Furnace, long Tick);

    /// <summary>Native fuel still in the chest, in the furnace's fuel slot and burning, read from one factory photograph.</summary>
    private static FurnaceLoad Loaded(FactorySnapshot snapshot, string furnace, string chest)
    {
        var entity = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == furnace).Data;
        string fuelInventory = entity.GetProperty("fuelInventoryId").GetString()!;
        long slot = snapshot.Records.Where(r => r.Kind == "inventory" && r.Id == fuelInventory)
            .Sum(r => r.Data.GetProperty("items").EnumerateObject().Sum(p => p.Value.GetInt64()));
        double burning = entity.TryGetProperty("burnerRemainingJoules", out var joules) ? joules.GetDouble() : 0;
        return new(FactoryLogistics.Items(snapshot, chest).GetValueOrDefault(FactoryLogistics.Fuel), slot, burning,
            FactoryLogistics.Items(snapshot, furnace), snapshot.CollectedTick);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

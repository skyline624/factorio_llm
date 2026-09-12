using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Run each phase after the corresponding real client UI action in a disposable fixture.</summary>
public sealed class PilotQualification(RuntimeSession session)
{
    public async Task<string> RunPhaseAsync(string phase, CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Pilot qualification requires --fixture.");
        if (phase is not ("manual" or "ai" or "standalone"))
            throw new ArgumentException("Pilot phase must be manual, ai or standalone.", nameof(phase));
        await using var game = session.CreateClient();
        GameResponse observation = await game.ExecuteAsync(GameRequest.Create("observe"), token);
        Require(observation.Ok && observation.Data.GetProperty("goal").GetProperty("fixture").GetBoolean(),
            "The world must already be marked as a fixture.");
        JsonElement agent = observation.Data.GetProperty("agent");
        Require(agent.GetProperty("controlMode").GetString() == (phase == "manual" ? "manual" : "ai"),
            "Perform the requested UI transition before running this phase.");
        PilotNativeState before = await ReadNativeAsync(token);
        if (phase == "standalone")
            Require(before.ConnectedPlayers == 0 && before.PilotCharacterId == 0 && !before.AssociatedPlayer,
                "The client did not leave a standalone character.");
        else
            Require(before.ConnectedPlayers == 1 && before.PilotCharacterId == before.CharacterId,
                "The pilot is not attached to the agent's native character.");
        Require(before.Destructible, "The agent must remain vulnerable in either control mode.");
        Require(before.TotalCharacters == 1, "A newly created pilot duplicated the character.");

        string identityPath = Path.Combine(session.Directory, "pilot-qualification-identity.json");
        if (File.Exists(identityPath))
        {
            PilotNativeState baseline = JsonSerializer.Deserialize<PilotNativeState>(await File.ReadAllTextAsync(identityPath, token), Protocol.Json)!;
            Require(before.CharacterId == baseline.CharacterId, "The control transition replaced the character.");
        }
        else await File.WriteAllTextAsync(identityPath, JsonSerializer.Serialize(before, Protocol.Json), token);

        ActorScope scope = observation.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        MapPosition destination = new(before.Position.X, before.Position.Y + 3);
        var submission = phase == "manual"
            ? OperationSubmission.Create(scope, "wait", new { ticks = 1 }, observation.Tick + 600)
            : OperationSubmission.Create(scope, "move", new { position = destination, tolerance = 0.2 }, observation.Tick + 600);
        string journal = Path.Combine(session.Directory, "pilot-qualification.jsonl");
        await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(new { phase, type = "submission", submission }, Protocol.Json) + "\n", token);
        var operations = new OperationClient(game);
        OperationReceipt receipt = await operations.SubmitAsync(submission, token);
        if (!receipt.IsTerminal) receipt = await operations.WaitAsync(submission.OperationId, TimeSpan.FromSeconds(20), token);
        PilotNativeState after = await ReadNativeAsync(token);
        Require(after.CharacterId == before.CharacterId, "The operation replaced the native character.");
        if (phase == "manual")
        {
            Require(receipt.Status == "rejected" && receipt.Error?.Code == "manual_control", "AI commands were not refused in manual mode.");
            Require(observation.Data.GetProperty("goal").GetProperty("humanInterventions").GetInt32() > 0,
                "Manual assistance was not counted.");
        }
        else
        {
            Require(receipt.Status == "completed" && after.Position.DistanceTo(destination) <= 0.3,
                "The AI move lacks independent native arrival evidence.");
            Require(after.Tick > before.Tick + 10, "The move did not take native walking time.");
        }
        await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(new
        {
            phase, type = "evidence", passed = true, isAutonomousCampaign = false,
            recordedUtc = DateTime.UtcNow, scope, before, after, receipt = receipt.Evidence
        }, Protocol.Json) + "\n", token);
        return journal;
    }

    private async Task<PilotNativeState> ReadNativeAsync(CancellationToken token)
    {
        const string command = """
            /silent-command local chars=game.surfaces.nauvis.find_entities_filtered{type="character",force="factorio_agent"}; assert(#chars==1,"Expected one agent character"); local c=chars[1]; local p=game.connected_players[1]; rcon.print(helpers.table_to_json{tick=game.tick,characterId=c.unit_number,pilotCharacterId=p and p.character and p.character.unit_number or 0,connectedPlayers=#game.connected_players,position=c.position,associatedPlayer=c.associated_player~=nil,destructible=c.destructible,totalCharacters=c.surface.count_entities_filtered{type="character"}});
            """;
        return JsonSerializer.Deserialize<PilotNativeState>(await session.CreateRcon().ExecuteAsync(command, token), Protocol.Json)
            ?? throw new InvalidDataException("Missing native pilot evidence.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

public sealed record PilotNativeState(long Tick, long CharacterId, long PilotCharacterId, int ConnectedPlayers,
    MapPosition Position, bool AssociatedPlayer, bool Destructible, int TotalCharacters = 0);

using System.Text.Json;

namespace Factorio.Agent.Core;

public sealed record NativeDeathTransition(long Incarnation, long DeathTick, long ActorUnitNumber, int SurfaceIndex, MapPosition Position)
{
    /// <summary>Accepts one engine-recorded death in the same world, never an unexplained actor replacement.</summary>
    public static NativeDeathTransition Read(GameResponse response, ActorScope previous, long earliestTick, bool awaitingRespawn = false)
    {
        if (!response.Ok) throw new GameRpcException(response.Error!);
        var data = response.Data;
        var current = data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        var actor = data.GetProperty("agent");
        var death = data.GetProperty("recovery").GetProperty("lastDeath");
        var result = new NativeDeathTransition(death.GetProperty("incarnation").GetInt64(), death.GetProperty("tick").GetInt64(),
            death.GetProperty("unitNumber").GetInt64(), death.GetProperty("surfaceIndex").GetInt32(),
            death.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!);
        bool alive = actor.GetProperty("alive").GetBoolean();
        long expectedIncarnation = awaitingRespawn && !alive ? previous.Incarnation : checked(previous.Incarnation + 1);
        if (previous.Incarnation < 1 || current.WorldId != previous.WorldId || current.ActorId != previous.ActorId
            || current.Incarnation != expectedIncarnation || current.Generation <= previous.Generation
            || actor.GetProperty("controlMode").GetString() != "ai" || (!alive && !awaitingRespawn)
            || result.Incarnation != previous.Incarnation || result.DeathTick < earliestTick || result.DeathTick > response.Tick
            || result.ActorUnitNumber < 1 || result.SurfaceIndex < 1 || result.Position is null
            || !double.IsFinite(result.Position.X) || !double.IsFinite(result.Position.Y)
            || data.GetProperty("collectedTick").GetInt64() != response.Tick
            || !data.GetProperty("recovery").GetProperty("knownCorpsesComplete").GetBoolean())
            throw new InvalidDataException("The native observation does not prove one death and normal respawn of the previous actor.");
        return result;
    }
}

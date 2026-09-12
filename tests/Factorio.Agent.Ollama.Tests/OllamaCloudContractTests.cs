using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace Factorio.Agent.Ollama.Tests;

public sealed class OllamaCloudContractTests(ITestOutputHelper output)
{
    [OllamaCloudFact]
    [Trait("Category", "CloudContract")]
    public async Task ExactModelReturnsAValidatedGoalForSyntheticContext()
    {
        var context = new StrategicContext("synthetic-contract-fixture-001",
            "Synthetic planning fixture, not a real game observation or campaign. " +
            "Base Factorio 2.0, enemies active. The character is healthy and currently sees no enemies. " +
            "Observed character stock: 8 iron plates, 10 coal, 1 burner mining drill, 1 stone furnace. " +
            "An accessible iron ore patch is known. There is no sustained plate production yet. " +
            "No research is complete. Hidden areas and hidden enemies are unknown.",
            "Launch a rocket using ordinary resources and verified production while surviving attacks.");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var planner = new OllamaStrategicPlanner(http, new OllamaOptions
        {
            Model = OllamaOptions.DefaultModel,
            ThinkingEffort = "low",
            RequestTimeout = TimeSpan.FromSeconds(90),
            MaxAttempts = 1
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        try
        {
            var goal = await planner.ProposeAsync(context, deadline.Token);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
            output.WriteLine("LIVE_CLOUD_CONTRACT " + JsonSerializer.Serialize(new
            {
                model = OllamaOptions.DefaultModel,
                fixture = "synthetic-no-game-connection",
                goal
            }, json));
            Assert.Equal(context.ObservationId, goal.ObservationId);
            Assert.Equal(1, goal.Metrics.Attempts);
            Assert.False(string.IsNullOrWhiteSpace(goal.Description));
        }
        catch (PlannerException error)
        {
            output.WriteLine($"LIVE_CLOUD_CONTRACT_FAILURE kind={error.Kind} attempts={error.Attempts} status={error.StatusCode} message={error.Message}");
            throw;
        }
    }
}

/// <summary>Opt-in only: one billable inference, no automatic retries, and no game connection.</summary>
public sealed class OllamaCloudFactAttribute : FactAttribute
{
    public OllamaCloudFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FACTORIO_OLLAMA_LIVE") != "1")
            Skip = "Set FACTORIO_OLLAMA_LIVE=1 only when the single bounded cloud inference is authorized.";
    }
}

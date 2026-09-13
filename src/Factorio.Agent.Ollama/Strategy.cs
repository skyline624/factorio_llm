using System.Net;

namespace Factorio.Agent.Ollama;

public interface IStrategicPlanner
{
    Task<GoalProposal> ProposeAsync(StrategicContext context, CancellationToken cancellationToken = default);
}

/// <summary>Bounded factual context. No secret configuration or executable game commands belong here.</summary>
public sealed record StrategicContext(string ObservationId, string Facts,
    string? CurrentGoal = null, string? PreviousResult = null);

public enum GoalCategory { Production, Research, Defense, Recovery, Exploration, Logistics, Other, Launch }
public enum GoalUnit { Items, ItemsPerMinute, FluidUnits, FluidUnitsPerMinute, Completion }
public enum GoalPriority { Low, Normal, High, Critical }

/// <summary>A semantic proposal, not an executable operation. A grounder must still establish feasibility.</summary>
public sealed record GoalProposal(string ObservationId, string Description, GoalCategory Category,
    string Target, decimal Quantity, GoalUnit Unit, GoalPriority Priority, PlannerMetrics Metrics);

/// <summary>Token metrics describe the successful response only; failed attempts may also consume tokens.</summary>
public sealed record PlannerMetrics(TimeSpan Elapsed, int Attempts, long? PromptTokens,
    long? CompletionTokens, TimeSpan? ProviderTotalDuration);

public enum PlannerErrorKind
{
    Authentication, MissingModel, Quota, Network, Timeout, InvalidResponse, RequestRejected
}

public sealed class PlannerException : Exception
{
    public PlannerException(PlannerErrorKind kind, string message, int attempts,
        HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null) : base(message)
    {
        Kind = kind;
        Attempts = attempts;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public PlannerErrorKind Kind { get; }
    public int Attempts { get; }
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
}

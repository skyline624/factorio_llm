using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record ResearchGoalResult(string Target, bool Researched, long StartTick, long EndTick,
    IReadOnlyList<string> CompletedTechnologies);

public interface IResearchStepExecutor
{
    Task ExecuteAsync(TechnologyStep step, CancellationToken token);
}

/// <summary>Completes the native dependency closure, requiring engine evidence after every stage.</summary>
public sealed class ResearchGoalExecutor(IGameClient game, IControllerJournal journal, IResearchStepExecutor? stages = null)
{
    public async Task<ResearchGoalResult> RunAsync(string target, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        token = deadline.Token;
        var reader = new TechnologyClient(game);
        var planner = new TechnologyPlanner();
        var executor = stages ?? new NativeResearchSteps(game, journal);
        TechnologyObservation initial = await reader.ReadDependenciesAsync(target, token);
        var completed = new List<string>();
        for (int index = 0; index < 256; index++)
        {
            var current = index == 0 ? initial : await reader.ReadDependenciesAsync(target, token);
            RequireScope(current);
            TechnologyStep next = planner.Next(target, current.Technologies);
            await journal.AppendAsync("research-goal-step", new { target, current.StartTick, current.EndTick, current.Scope, next }, token);
            if (next.Kind == "completed")
            {
                var result = new ResearchGoalResult(target, true, initial.StartTick, current.EndTick, completed.ToArray());
                await journal.AppendAsync("research-goal-result", result, token);
                return result;
            }
            if (next.Kind is not ("craft-trigger" or "mine-trigger" or "research"))
                throw new InvalidOperationException(next.Reason ?? "The native research prerequisite is not executable.");
            await executor.ExecuteAsync(next, token);
            // Never interpret a successful method return or a research-selection receipt as completion.
            var proof = await reader.ReadDependenciesAsync(next.Technology, token);
            RequireScope(proof);
            if (!proof.Technologies[next.Technology].Researched)
                throw new InvalidOperationException("The research stage returned without native completion. Reconcile before any new stage.");
            completed.Add(next.Technology);
            await journal.AppendAsync("research-stage-verified", new { next, proof }, token);
        }
        throw new InvalidOperationException("Research exhausted its dependency-stage budget.");

        void RequireScope(TechnologyObservation value)
        {
            if (value.Scope != initial.Scope) throw new InvalidDataException("Actor identity changed during research progression; reconcile native effects.");
        }
    }

    private sealed class NativeResearchSteps(IGameClient game, IControllerJournal journal) : IResearchStepExecutor
    {
        public async Task ExecuteAsync(TechnologyStep step, CancellationToken token)
        {
            if (step.Kind == "craft-trigger")
                await new ResearchPrerequisiteController(game, new ProductionGoalExecutor(game, journal), journal).RunAsync(step.Technology, token);
            else if (step.Kind == "mine-trigger")
                await new ResourceResearchController(game, journal).RunAsync(step.Technology, token);
            else if (step.Kind == "research")
                await new LaboratoryController(game, journal).RunAsync(step.Technology, token);
            else throw new InvalidOperationException("Unsupported research stage.");
        }
    }
}

namespace Factorio.Agent.Core;

/// <summary>Bounds direct handcrafting lots using native recipe duration at normal character speed.</summary>
public static class HandcraftTiming
{
    public const long MaximumTicks = 180000;
    private const long SubmissionAllowance = 600;

    public static int Limit(NativeRecipe recipe, int requested)
    {
        if (requested < 1) throw new ArgumentOutOfRangeException(nameof(requested));
        double cycles = Math.Floor((MaximumTicks - SubmissionAllowance) / TicksPerCycle(recipe));
        return (int)Math.Min(Math.Min(requested, 1000), cycles);
    }

    public static long DeadlineTicks(NativeRecipe recipe, int count)
    {
        if (count < 1 || Limit(recipe, count) != count)
            throw new ArgumentOutOfRangeException(nameof(count), "Craft lot exceeds the operation time budget.");
        return checked(SubmissionAllowance + (long)(TicksPerCycle(recipe) * count));
    }

    private static double TicksPerCycle(NativeRecipe recipe)
    {
        if (!double.IsFinite(recipe.EnergySeconds) || recipe.EnergySeconds <= 0)
            throw new InvalidDataException("Native handcraft duration must be positive and finite.");
        // Native queue transitions cost ticks; margin also covers ordinary observation/submission latency.
        return Math.Ceiling(recipe.EnergySeconds * 60 * 1.25) + 2;
    }
}

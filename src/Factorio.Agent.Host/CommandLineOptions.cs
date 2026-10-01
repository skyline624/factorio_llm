namespace Factorio.Agent.Host;

/// <summary>Strict CLI options: a misspelled budget or flag must stop the command instead of being ignored.</summary>
public static class CommandLineOptions
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "fixture", "stationary-threat", "reuse", "recovery-death", "danger-zone", "ollama-cloud", "radar", "visible"
    };

    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "action", "boiler", "capacity-items", "config", "distance", "fluid", "installation", "item", "items",
        "journal", "json", "json-file", "kind", "layers", "machine", "max-goals", "minutes", "phase", "plan", "quantity", "recipe", "reserve",
        "root", "seconds", "seed", "session", "source", "target", "technology", "ticks", "x", "y"
    };

    public static Dictionary<string, string> Parse(string[] values)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < values.Length; index++)
        {
            string value = values[index];
            if (!value.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected an option starting with --.");
            string name = value[2..];
            bool flag = Flags.Contains(name);
            if (!flag && !Values.Contains(name)) throw new ArgumentException($"Unknown option {value}.");
            string contents = flag ? "true" : ++index < values.Length ? values[index] : throw new ArgumentException($"Missing value for {value}.");
            if (!options.TryAdd(name, contents)) throw new ArgumentException($"Duplicate option {value}.");
        }
        return options;
    }
}

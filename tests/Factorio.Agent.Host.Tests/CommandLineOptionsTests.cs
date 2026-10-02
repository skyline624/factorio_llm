using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void ParsesValuesAndFlags()
    {
        var options = CommandLineOptions.Parse(["--session", "s.json", "--ollama-cloud", "--minutes", "30"]);
        Assert.Equal("s.json", options["session"]);
        Assert.Equal("true", options["ollama-cloud"]);
        Assert.Equal("30", options["minutes"]);
        Assert.Equal("1", CommandLineOptions.Parse(["--session", "s.json", "--layers", "1"])["layers"]);
        Assert.Equal("true", CommandLineOptions.Parse(["--session", "s.json", "--from-materials"])["from-materials"]);
        Assert.Equal("true", CommandLineOptions.Parse(["--session", "s.json", "--burner"])["burner"]);
    }

    [Theory]
    [InlineData("--minute")]
    [InlineData("--max-goal")]
    [InlineData("--sesion")]
    public void RejectsUnknownOptionsBeforeAnyCommandRuns(string misspelled)
    {
        var error = Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--session", "s.json", misspelled, "30"]));
        Assert.Contains(misspelled, error.Message);
    }

    [Fact]
    public void RejectsDuplicatesAndMissingValues()
    {
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--seed", "1", "--seed", "2"]));
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--session"]));
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["positional"]));
    }
}

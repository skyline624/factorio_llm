using System.Text.Json;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ControllerJournalTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "controller-journal-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private const string Valid = "{\"type\":\"strategic-context\",\"data\":{\"facts\":\"{\\\"observedTick\\\":100}\"}}";

    public void Dispose() => File.Delete(path);

    [Fact]
    public async Task AppendAfterATornWriteStartsANewParseableLine()
    {
        await File.WriteAllTextAsync(path, Valid + "\n{\"type\":\"torn");
        await new ControllerJournal(path).AppendAsync("next", new { value = 1 }, CancellationToken.None);
        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(3, lines.Length);
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal("next", last.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task AppendCreatesAndExtendsAWellFormedJournal()
    {
        var journal = new ControllerJournal(path);
        await journal.AppendAsync("a", new { big = new string('x', 10000) }, CancellationToken.None);
        await journal.AppendAsync("b", new { }, CancellationToken.None);
        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        foreach (string line in lines) JsonDocument.Parse(line).Dispose();
    }
}

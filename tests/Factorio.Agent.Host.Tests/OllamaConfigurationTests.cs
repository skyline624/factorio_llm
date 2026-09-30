using System.Text.Json;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class OllamaConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "factorio-config-" + Guid.NewGuid().ToString("N"));

    private async Task<string> WriteAsync(string json)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "appsettings.local.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task CloudFileSelectsDirectAccessAndFileKeyBeforeEnvironment()
    {
        string path = await WriteAsync("""{"Ollama":{"BaseUrl":"https://ollama.com/","ApiKey":"synthetic-file-key","ThinkingEffort":"high"}}""");
        var options = await OllamaConfiguration.LoadAsync(path, false, "synthetic-environment-key");
        Assert.True(options.IsDirectCloud);
        Assert.Equal("synthetic-file-key", options.ApiKey);
        Assert.Equal("high", options.ThinkingEffort);
        Assert.Equal(1, options.MaxAttempts);
        Assert.DoesNotContain("synthetic-file-key", JsonSerializer.Serialize(options));
        Assert.DoesNotContain("synthetic-file-key", options.ToString());
        using var http = new HttpClient();
        _ = new OllamaStrategicPlanner(http, options);
    }

    [Fact]
    public async Task CloudFlagOverridesLocalUrlAndUsesEnvironmentWhenFileKeyIsEmpty()
    {
        string path = await WriteAsync("""{"Ollama":{"BaseUrl":"http://localhost:11434","ApiKey":""}}""");
        var options = await OllamaConfiguration.LoadAsync(path, true, "synthetic-environment-key");
        Assert.True(options.IsDirectCloud);
        Assert.Equal("synthetic-environment-key", options.ApiKey);
    }

    [Fact]
    public async Task LocalProfileNeverForwardsEitherKey()
    {
        string path = await WriteAsync("""{"Ollama":{"ApiKey":"synthetic-file-key","Stream":false}}""");
        var options = await OllamaConfiguration.LoadAsync(path, false, "synthetic-environment-key");
        Assert.False(options.IsDirectCloud);
        Assert.Null(options.ApiKey);
        Assert.Equal(OllamaOptions.DefaultModel, options.RequestModel);
    }

    [Theory]
    [InlineData("{\"Ollama\":{\"ApiKey\":\"synthetic-secret", "synthetic-secret")]
    [InlineData("{\"Ollama\":{\"synthetic-secret\":true}}", "synthetic-secret")]
    [InlineData("{\"Ollama\":{\"ApiKey\":42}}", "42")]
    [InlineData("{\"Ollama\":{\"ApiKey\":\"synthetic-secret\",\"ApiKey\":\"other\"}}", "synthetic-secret")]
    public async Task MalformedConfigurationNeverEchoesItsContents(string json, string secret)
    {
        string path = await WriteAsync(json);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => OllamaConfiguration.LoadAsync(path, false, null));
        Assert.DoesNotContain(secret, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Ollama\":null}")]
    [InlineData("{\"Ollama\":{\"Stream\":true}}")]
    [InlineData("{\"Ollama\":{\"BaseUrl\":\"not a URL\"}}")]
    public async Task InvalidSettingsAreRejected(string json)
    {
        string path = await WriteAsync(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => OllamaConfiguration.LoadAsync(path, false, null));
    }

    [Fact]
    public async Task DefaultProfileIsFoundFromTheRepositoryRootRatherThanTheWorkingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        string nested = Path.Combine(directory, "src", "Host", "bin", "Release");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(directory, "Factorio.Agent.sln"), "");
        await File.WriteAllTextAsync(Path.Combine(directory, "config", "appsettings.local.json"),
            """{"Ollama":{"BaseUrl":"https://ollama.com/","ApiKey":"synthetic-file-key"}}""");
        Assert.Equal(Path.Combine(directory, "config", "appsettings.local.json"), OllamaConfiguration.ResolveDefaultPath(nested));
        var options = await OllamaConfiguration.LoadAsync(null, false, null, searchFrom: nested);
        Assert.True(options.IsDirectCloud);
    }

    [Fact]
    public async Task ExplicitMissingFileDoesNotSilentlySelectTheGateway()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => OllamaConfiguration.LoadAsync(
            Path.Combine(directory, "missing.json"), false, null));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

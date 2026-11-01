using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class GestionConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "factorio-gestion-" + Guid.NewGuid().ToString("N"));

    private async Task<string> WriteAsync(string json)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "appsettings.gestion.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private static string Profile(string gestion) =>
        """{"Planner":{"Transport":"gestion-local"},"Gestion":{""" + gestion.Replace("'", "\"") + "}}";

    private const string Minimal = "'BaseUrl':'https://192.168.1.33/','Model':'synthetic-model','CaCertificate':'ca.pem'";

    [Fact]
    public async Task TransportIsAcceptedAndKeyComesFromTheProfileBeforeTheEnvironment()
    {
        string path = await WriteAsync(Profile(Minimal + ",'ApiKey':'synthetic-file-key','RequestTimeoutSeconds':30,'MaxTokens':512"));
        Assert.Equal("gestion-local", await OllamaConfiguration.LoadPlannerTransportAsync(path));
        var (options, ca) = await OllamaConfiguration.LoadGestionAsync(path, "synthetic-environment-key");
        Assert.Equal("synthetic-file-key", options.ApiKey);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestTimeout);
        Assert.Equal(512, options.MaxTokens);
        Assert.DoesNotContain("synthetic-file-key", options.ToString());
        Assert.Equal(Path.Combine(directory, "ca.pem"), ca);
    }

    [Fact]
    public async Task EnvironmentKeyIsUsedWhenTheProfileKeyIsBlank()
    {
        string path = await WriteAsync(Profile(Minimal + ",'ApiKey':' '"));
        var (options, _) = await OllamaConfiguration.LoadGestionAsync(path, "synthetic-environment-key");
        Assert.Equal("synthetic-environment-key", options.ApiKey);
    }

    [Fact]
    public async Task MissingKeyNamesBothSourcesWithoutEchoingAnything()
    {
        string path = await WriteAsync(Profile(Minimal));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => OllamaConfiguration.LoadGestionAsync(path, null));
        Assert.Contains("Gestion.ApiKey", error.Message);
        Assert.Contains("GESTION_API_KEY", error.Message);
    }

    [Theory]
    [InlineData("""{"Planner":{"Transport":"gestion-local"}}""")]
    [InlineData("""{"Gestion":{"BaseUrl":"https://192.168.1.33/","Model":"m","CaCertificate":"ca.pem"}}""")]
    [InlineData("""{"Planner":{"Transport":"ollama"},"Gestion":{"BaseUrl":"https://192.168.1.33/","Model":"m","CaCertificate":"ca.pem"}}""")]
    public async Task SectionAndExplicitTransportAreBothRequired(string json)
    {
        string path = await WriteAsync(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => OllamaConfiguration.LoadGestionAsync(path, "synthetic-environment-key"));
    }

    [Fact]
    public async Task ActiveEngineAliasLoadsAndMissingCaIsRejected()
    {
        string alias = await WriteAsync(Profile("'BaseUrl':'https://192.168.1.33/','Model':'llm-actif','CaCertificate':'ca.pem'"));
        Assert.Equal("llm-actif", (await OllamaConfiguration.LoadGestionAsync(alias, "synthetic-environment-key")).Options.Model);
        string noCa = await WriteAsync(Profile("'BaseUrl':'https://192.168.1.33/','Model':'m'"));
        await Assert.ThrowsAsync<InvalidDataException>(() => OllamaConfiguration.LoadGestionAsync(noCa, "synthetic-environment-key"));
    }

    [Fact]
    public async Task CaPathExpandsEnvironmentVariablesAndKeepsAbsolutePaths()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "somewhere");
        Environment.SetEnvironmentVariable("FACTORIO_TEST_GESTION_DIR", absolute);
        try
        {
            string path = await WriteAsync(Profile("'BaseUrl':'https://192.168.1.33/','Model':'m','CaCertificate':'%FACTORIO_TEST_GESTION_DIR%\\\\ca.pem'"));
            Assert.Equal(Path.Combine(absolute, "ca.pem"),
                (await OllamaConfiguration.LoadGestionAsync(path, "synthetic-environment-key")).CaCertificatePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FACTORIO_TEST_GESTION_DIR", null);
        }
    }
    [Fact]
    public void MissingOrInvalidCaFileFailsClearlyWithoutEchoingContents()
    {
        Assert.Throws<InvalidDataException>(() => GestionHttp.Create(Path.Combine(directory, "absent.pem")));
        Directory.CreateDirectory(directory);
        string bad = Path.Combine(directory, "bad.pem");
        File.WriteAllText(bad, "synthetic-secret-content");
        var error = Assert.Throws<InvalidDataException>(() => GestionHttp.Create(bad));
        Assert.DoesNotContain("synthetic-secret-content", error.ToString());
    }

    [Fact]
    public void CertificateIsAcceptedOnlyWhenIssuedByTheConfiguredCaForTheExpectedName()
    {
        using var ca = Authority("Synthetic CA");
        using var other = Authority("Another CA");
        using var leaf = Leaf(ca);
        var chainError = SslPolicyErrors.RemoteCertificateChainErrors;
        Assert.True(GestionHttp.Validate([ca], leaf, null, chainError));
        Assert.False(GestionHttp.Validate([other], leaf, null, chainError));
        Assert.False(GestionHttp.Validate([ca], leaf, null, chainError | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(GestionHttp.Validate([ca], leaf, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.False(GestionHttp.Validate([ca], null, null, chainError));
    }

    private static X509Certificate2 Authority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 Leaf(X509Certificate2 ca)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=192.168.1.33", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(System.Net.IPAddress.Parse("192.168.1.33"));
        request.CertificateExtensions.Add(names.Build());
        return request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(8));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Factorio.Agent.Ollama;

/// <summary>A closed question for a typed decision model. Choice and score questions carry 2 to 26 named options.</summary>
public sealed record DecisionQuestion(string Type, string Instructions, IReadOnlyDictionary<string, string?>? Criteria = null);
public sealed record DecisionAnswer(string Type, string? Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence);
public sealed record DecisionResult(string Model, IReadOnlyDictionary<string, DecisionAnswer> Answers, long InputTokens, TimeSpan Elapsed);

public sealed record DecisionModelOptions
{
    public Uri BaseUrl { get; init; } = new("http://localhost:11434/");
    public string Model { get; init; } = "nimble:9b-q4_K_M";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public string KeepAlive { get; init; } = "30m";

    internal void Validate()
    {
        // System One models are served only by a local Ollama; nothing is sent to a remote host.
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || !BaseUrl.IsLoopback || BaseUrl.Scheme != "http"
            || !string.IsNullOrEmpty(BaseUrl.UserInfo) || !string.IsNullOrEmpty(BaseUrl.Query) || !string.IsNullOrEmpty(BaseUrl.Fragment))
            throw new ArgumentException("The decision model must be served by a local HTTP Ollama without credentials, query or fragment.");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 128) throw new ArgumentException("A decision model tag is required.");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
    }
}

public sealed class DecisionModelException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// Calls Ollama's POST /v1/systemone. Every answer must pick one of the supplied options with coherent probabilities;
/// a failed or malformed call is reported once and never retried. Answers are advice, never game actions.
/// </summary>
public sealed class DecisionModelClient
{
    private const int MaxRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly DecisionModelOptions _options;
    private readonly Uri _url;

    public DecisionModelClient(HttpClient http, DecisionModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        options.Validate();
        _http = http;
        _options = options;
        _url = new Uri(options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/v1/systemone");
    }

    public async Task<DecisionResult> DecideAsync(string state, IReadOnlyDictionary<string, DecisionQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state)) throw new ArgumentException("A decision state is required.", nameof(state));
        if (questions.Count is < 1 or > 64) throw new ArgumentException("Ask 1 to 64 questions.", nameof(questions));
        foreach (var (name, question) in questions)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(question.Instructions))
                throw new ArgumentException("Questions need a name and instructions.", nameof(questions));
            if (question.Type is not ("choice" or "score" or "noul")) throw new ArgumentException("Unsupported question type.", nameof(questions));
            if (question.Type != "noul" && question.Criteria?.Count is not (>= 2 and <= 26))
                throw new ArgumentException("Choice and score questions need 2 to 26 options.", nameof(questions));
        }
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = _options.Model,
            state,
            questions = questions.ToDictionary(q => q.Key, q => q.Value.Type == "noul"
                ? (object)new { type = q.Value.Type, instructions = q.Value.Instructions }
                : new { type = q.Value.Type, instructions = q.Value.Instructions, criteria = q.Value.Criteria }),
            keep_alive = _options.KeepAlive
        }, Json);
        if (body.Length > MaxRequestBytes) throw new DecisionModelException("The decision request exceeds the 64 KiB server limit.");

        var started = TimeProvider.System.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);
        JsonDocument document;
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new("application/json");
            using var response = await _http.PostAsync(_url, content, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DecisionModelException($"The decision model returned HTTP {(int)response.StatusCode}.", response.StatusCode);
            document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false),
                new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DecisionModelException("The decision model did not answer within its deadline.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
        {
            throw new DecisionModelException("The decision model is unavailable or returned malformed JSON.");
        }
        using (document)
            return Parse(document.RootElement, questions, TimeProvider.System.GetElapsedTime(started));
    }

    private static DecisionResult Parse(JsonElement root, IReadOnlyDictionary<string, DecisionQuestion> questions, TimeSpan elapsed)
    {
        try
        {
            var answers = root.GetProperty("answers");
            if (answers.EnumerateObject().Count() != questions.Count) throw new DecisionModelException("The decision answers do not match the questions.");
            var parsed = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            foreach (var (name, question) in questions)
            {
                var answer = answers.GetProperty(name);
                if (question.Type == "noul")
                {
                    // Ollama returns the probability of "yes" as the noul value itself.
                    double yes = answer.GetProperty("noul").GetDouble();
                    if (!double.IsFinite(yes) || yes is < 0 or > 1)
                        throw new DecisionModelException($"The decision answer to {name} is not a probability.");
                    parsed[name] = new("noul", yes >= 0.5 ? "true" : "false",
                        new Dictionary<string, double>(StringComparer.Ordinal) { ["true"] = yes, ["false"] = 1 - yes }, double.NaN);
                    continue;
                }
                var probabilities = answer.GetProperty("probabilities").EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal);
                string? choice = answer.GetProperty(question.Type).GetString();
                var options = question.Criteria!.Keys.ToHashSet(StringComparer.Ordinal);
                double sum = probabilities.Values.Sum();
                if (choice is null || !options.Contains(choice) || !probabilities.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(options)
                    || probabilities.Values.Any(p => !double.IsFinite(p) || p is < 0 or > 1) || Math.Abs(sum - 1) > 0.02)
                    throw new DecisionModelException($"The decision answer to {name} is outside the supplied options or incoherent.");
                double confidence = answer.TryGetProperty("confidence", out var value) ? value.GetDouble() : double.NaN;
                parsed[name] = new(question.Type, choice, probabilities, confidence);
            }
            long tokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var input) ? input.GetInt64() : 0;
            return new(root.GetProperty("model").GetString() ?? "", parsed, tokens, elapsed);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new DecisionModelException("The decision model response does not follow the System One schema.");
        }
    }
}

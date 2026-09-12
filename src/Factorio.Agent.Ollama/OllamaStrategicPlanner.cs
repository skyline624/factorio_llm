using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Factorio.Agent.Ollama;

/// <summary>Validated strategic proposals through the local Ollama gateway. Never executes game actions.</summary>
public sealed class OllamaStrategicPlanner : IStrategicPlanner
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly TimeProvider _clock;
    private readonly Uri _chatUrl;

    public OllamaStrategicPlanner(HttpClient httpClient, OllamaOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
        _options = options ?? new OllamaOptions();
        _options.Validate();
        _clock = timeProvider ?? TimeProvider.System;
        _chatUrl = new Uri(_options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/api/chat");
    }

    public async Task<GoalProposal> ProposeAsync(StrategicContext context, CancellationToken cancellationToken = default)
    {
        GoalContract.ValidateContext(context);
        cancellationToken.ThrowIfCancellationRequested();
        var started = _clock.GetTimestamp();
        // Each request is independent: previous goals/results are factual summaries, not fabricated tool messages.
        var contextJson = JsonSerializer.Serialize(context, Json);
        for (var attempt = 1; ; attempt++)
        {
            PlannerException failure;
            using var deadline = new CancellationTokenSource(_options.RequestTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _chatUrl)
                {
                    Content = JsonContent.Create(new
                    {
                        model = _options.Model,
                        stream = false,
                        think = _options.ThinkingEffort,
                        messages = new[]
                        {
                            new { role = "system", content = GoalContract.SystemPrompt },
                            new { role = "user", content = contextJson }
                        },
                        tools = new[] { GoalContract.ToolSchema }
                    }, options: Json)
                };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw HttpFailure(response, attempt);
                using var document = await ReadResponseAsync(response.Content, attempt, linked.Token).ConfigureAwait(false);
                var root = document.RootElement;
                RejectDuplicateProperties(root, attempt);
                var metrics = new PlannerMetrics(_clock.GetElapsedTime(started), attempt,
                    ReadMetric(root, "prompt_eval_count", attempt), ReadMetric(root, "eval_count", attempt),
                    ReadMetric(root, "total_duration", attempt) is { } nanoseconds
                        ? TimeSpan.FromTicks(nanoseconds / 100) : null);
                linked.Token.ThrowIfCancellationRequested();
                return GoalContract.Parse(root, context.ObservationId, metrics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failure = new PlannerException(PlannerErrorKind.Timeout,
                    "The inference deadline expired. Remote processing and token consumption may still have occurred.", attempt);
            }
            catch (HttpRequestException)
            {
                failure = new PlannerException(PlannerErrorKind.Network,
                    "The Ollama gateway or its cloud connection is unavailable.", attempt);
            }
            catch (IOException)
            {
                failure = new PlannerException(PlannerErrorKind.Network,
                    "The inference response was interrupted before completion.", attempt);
            }
            catch (JsonException)
            {
                failure = GoalContract.Invalid("Ollama returned malformed JSON.", attempt);
            }
            catch (PlannerException exception)
            {
                failure = exception;
            }

            if (attempt >= _options.MaxAttempts ||
                failure.Kind is not (PlannerErrorKind.Quota or PlannerErrorKind.Network or PlannerErrorKind.Timeout))
                throw failure;
            var delay = failure.RetryAfter ?? TimeSpan.FromTicks(Math.Min(_options.MaxRetryDelay.Ticks,
                _options.InitialRetryDelay.Ticks * (1L << (attempt - 1))));
            // A long Retry-After is a suspension, not permission to retry earlier than the provider requested.
            if (delay > _options.MaxRetryDelay)
                throw failure;
            await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private PlannerException HttpFailure(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate)
            retryAfter = retryDate - _clock.GetUtcNow();
        if (retryAfter < TimeSpan.Zero)
            retryAfter = TimeSpan.Zero;

        var (kind, message) = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                (PlannerErrorKind.Authentication, "Ollama Cloud authentication was refused. Check the account signed in to the Ollama gateway."),
            HttpStatusCode.NotFound =>
                (PlannerErrorKind.MissingModel, $"The selected model {_options.Model} is unavailable; no substitute was selected."),
            HttpStatusCode.TooManyRequests =>
                (PlannerErrorKind.Quota, "Ollama Cloud rejected the request due to a quota or rate limit."),
            >= HttpStatusCode.InternalServerError =>
                (PlannerErrorKind.Network, "The Ollama gateway or cloud service reported a server failure."),
            _ => (PlannerErrorKind.RequestRejected, "Ollama rejected the inference request.")
        };
        // Provider response bodies can echo inputs. Keep exception messages independent of those bodies.
        return new PlannerException(kind, message, attempt, response.StatusCode, retryAfter);
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpContent content, int attempt, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw GoalContract.Invalid("Ollama response exceeds the two MiB limit.", attempt);
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw GoalContract.Invalid("Ollama response exceeds the two MiB limit.", attempt);
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static long? ReadMetric(JsonElement root, string name, int attempt)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw GoalContract.Invalid("Expected an Ollama response object.", attempt);
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var metric) || metric < 0)
            throw GoalContract.Invalid("Ollama returned an invalid usage metric.", attempt);
        return metric;
    }

    private static void RejectDuplicateProperties(JsonElement element, int attempt)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw GoalContract.Invalid("Ollama returned duplicate JSON fields.", attempt);
                RejectDuplicateProperties(property.Value, attempt);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray())
                RejectDuplicateProperties(value, attempt);
    }
}

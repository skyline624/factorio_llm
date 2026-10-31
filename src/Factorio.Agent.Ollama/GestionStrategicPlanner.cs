using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Gestion;

/// <summary>Validated proposals through the local OpenAI-compatible Gestion server. One attempt per call; the campaign controller retries.</summary>
public sealed class GestionStrategicPlanner : IStrategicPlanner
{
    private const string Provider = "Gestion";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly GestionOptions _options;
    private readonly TimeProvider _clock;
    private readonly Uri _chatUrl;

    public GestionStrategicPlanner(HttpClient httpClient, GestionOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _http = httpClient;
        _options = options;
        _clock = timeProvider ?? TimeProvider.System;
        _chatUrl = new Uri(options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/v1/chat/completions");
    }

    public async Task<GoalProposal> ProposeAsync(StrategicContext context, CancellationToken cancellationToken = default)
    {
        GoalContract.ValidateContext(context);
        cancellationToken.ThrowIfCancellationRequested();
        var started = _clock.GetTimestamp();
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
                    max_tokens = _options.MaxTokens,
                    messages = new[]
                    {
                        new { role = "system", content = GoalContract.SystemPrompt },
                        new { role = "user", content = JsonSerializer.Serialize(context, Json) }
                    },
                    tools = new[] { GoalContract.ToolSchema }
                }, options: Json)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw HttpFailure(response);
            using var document = await PlannerHttp.ReadResponseAsync(response.Content, Provider, 1, linked.Token).ConfigureAwait(false);
            PlannerHttp.RejectDuplicateProperties(document.RootElement, Provider, 1);
            return Parse(document.RootElement, context.ObservationId, _clock.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new PlannerException(PlannerErrorKind.Timeout,
                "The inference deadline expired. Remote processing may still have occurred.", 1);
        }
        catch (HttpRequestException)
        {
            throw new PlannerException(PlannerErrorKind.Network, "The Gestion server is unreachable.", 1);
        }
        catch (IOException)
        {
            throw new PlannerException(PlannerErrorKind.Network, "The inference response was interrupted before completion.", 1);
        }
        catch (JsonException)
        {
            throw GoalContract.Invalid("Gestion returned malformed JSON.", 1);
        }
    }

    private GoalProposal Parse(JsonElement root, string observationId, TimeSpan elapsed)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw GoalContract.Invalid("Expected a Gestion response object.", 1);
        if (!root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String ||
            model.GetString() != _options.Model)
            throw new PlannerException(PlannerErrorKind.MissingModel,
                "The server answered with another model; no substitute accepted.", 1);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() != 1 || choices[0].ValueKind != JsonValueKind.Object)
            throw GoalContract.Invalid("Expected exactly one completion choice.", 1);
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var reason) &&
            reason.ValueKind == JsonValueKind.String && reason.GetString() == "length")
            throw GoalContract.Invalid("The model response was truncated.", 1);
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != "assistant" ||
            !message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() != 1)
            throw GoalContract.Invalid("Expected an assistant response with exactly one proposed goal.", 1);

        var call = calls[0];
        if (call.ValueKind != JsonValueKind.Object ||
            (call.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "function")) ||
            !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object ||
            !function.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != GoalContract.ToolName ||
            !function.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.String)
            throw GoalContract.Invalid("Expected propose_goal with JSON string arguments.", 1);

        var metrics = new PlannerMetrics(elapsed, 1, ReadUsage(root, "prompt_tokens"), ReadUsage(root, "completion_tokens"), null);
        using var parsed = JsonDocument.Parse(arguments.GetString()!, new JsonDocumentOptions { MaxDepth = 32 });
        PlannerHttp.RejectDuplicateProperties(parsed.RootElement, Provider, 1);
        return GoalContract.ParseArguments(parsed.RootElement, observationId, metrics);
    }

    private static long? ReadUsage(JsonElement root, string name)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
            return null;
        if (usage.ValueKind != JsonValueKind.Object)
            throw GoalContract.Invalid("Gestion returned an invalid usage object.", 1);
        if (!usage.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var tokens) || tokens < 0)
            throw GoalContract.Invalid("Gestion returned an invalid usage metric.", 1);
        return tokens;
    }

    private PlannerException HttpFailure(HttpResponseMessage response)
    {
        var (kind, message) = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                (PlannerErrorKind.Authentication, "Gestion authentication was refused. Check Gestion.ApiKey or GESTION_API_KEY."),
            HttpStatusCode.NotFound =>
                (PlannerErrorKind.MissingModel, $"The selected model {_options.Model} is unavailable; no substitute was selected."),
            HttpStatusCode.TooManyRequests =>
                (PlannerErrorKind.Quota, "Gestion rejected the request due to a rate limit."),
            >= HttpStatusCode.InternalServerError =>
                (PlannerErrorKind.Network, "The Gestion server reported a failure."),
            _ => (PlannerErrorKind.RequestRejected, "Gestion rejected the inference request.")
        };
        // Server response bodies can echo inputs. Keep exception messages independent of those bodies.
        return new PlannerException(kind, message, 1, response.StatusCode, PlannerHttp.RetryAfter(response, _clock));
    }
}

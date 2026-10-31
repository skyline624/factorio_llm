using System.Text.Json;

namespace Factorio.Agent.Ollama;

/// <summary>Response hygiene shared by the HTTP planners: bounded size and unambiguous JSON.</summary>
internal static class PlannerHttp
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    public static async Task<JsonDocument> ReadResponseAsync(HttpContent content, string provider, int attempt, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw GoalContract.Invalid($"{provider} response exceeds the two MiB limit.", attempt);
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw GoalContract.Invalid($"{provider} response exceeds the two MiB limit.", attempt);
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    public static TimeSpan? RetryAfter(HttpResponseMessage response, TimeProvider clock)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate)
            retryAfter = retryDate - clock.GetUtcNow();
        return retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
    }

    public static void RejectDuplicateProperties(JsonElement element, string provider, int attempt)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw GoalContract.Invalid($"{provider} returned duplicate JSON fields.", attempt);
                RejectDuplicateProperties(property.Value, provider, attempt);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray())
                RejectDuplicateProperties(value, provider, attempt);
    }
}

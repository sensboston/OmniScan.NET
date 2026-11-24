using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OmniScan.Net.Assist;

public enum AssistFailure
{
    /// <summary>The key does not look like a key of any supported service.</summary>
    UnknownKey,

    /// <summary>The service refused the key.</summary>
    BadKey,

    /// <summary>The key works, but no model that reads images is available to it.</summary>
    NoModel,

    /// <summary>No connection, a timeout, or the service is overloaded.</summary>
    Unavailable,

    /// <summary>The service answered with something we could not use.</summary>
    BadAnswer,
}

public sealed class AssistException(AssistFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AssistFailure Failure { get; } = failure;
}

/// <summary>
/// A connection to one online service that can look at images: the service is found from the key,
/// the key is checked by listing the models it can use, and the model is chosen from that list.
/// Requests go one at a time. When a model is busy (common for the newest ones on free tiers) the request
/// goes to the next model of the list, and the busy one is left alone for a while; when all are busy,
/// it is retried a couple of times.
/// </summary>
public sealed class AssistClient : IDisposable
{
    private const string AnthropicVersion = "2026-10-21";
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];
    private static readonly TimeSpan BusyPause = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly IReadOnlyList<string> _models;
    private readonly Dictionary<string, DateTime> _busyUntil = [];

    private AssistClient(AssistProvider provider, string key, IReadOnlyList<string> models, HttpClient http)
    {
        Provider = provider;
        _models = models;
        _key = key;
        _http = http;
    }

    /// <summary>A client over a given handler, for tests (no key check, no model list).</summary>
    internal static AssistClient Create(AssistProvider provider, IReadOnlyList<string> models, HttpMessageHandler handler) =>
        new(provider, "test-key", models, new HttpClient(handler) { BaseAddress = AssistProviders.BaseUri(provider) });

    public AssistProvider Provider { get; }

    /// <summary>The model requests go to now: the first one of the list that is not resting after being busy.</summary>
    public string Model
    {
        get
        {
            lock (_busyUntil)
            {
                DateTime now = DateTime.UtcNow;
                return _models.FirstOrDefault(m => !_busyUntil.TryGetValue(m, out DateTime until) || until <= now) ?? _models[0];
            }
        }
    }

    /// <summary>All the models this client may use, in order.</summary>
    public IReadOnlyList<string> Models => _models;

    /// <summary>
    /// Finds the service of <paramref name="key"/>, checks the key and picks a model
    /// (<paramref name="model"/> overrides the choice when the key can use it).
    /// </summary>
    public static async Task<AssistClient> ConnectAsync(string key, string? model = null, CancellationToken cancellationToken = default)
    {
        key = key.Trim();
        AssistProvider provider = AssistProviders.Detect(key)
            ?? throw new AssistException(AssistFailure.UnknownKey, "The key does not belong to a supported service.");

        HttpClient http = new() { BaseAddress = AssistProviders.BaseUri(provider), Timeout = TimeSpan.FromSeconds(90) };
        try
        {
            IReadOnlyList<string> models = await ListModelsAsync(http, provider, key, cancellationToken).ConfigureAwait(false);
            if (provider == AssistProvider.OpenRouter)
                await CheckOpenRouterKeyAsync(http, key, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> candidates = AssistProviders.Candidates(provider, models, model);
            if (candidates.Count == 0)
                throw new AssistException(AssistFailure.NoModel, "No model that reads images is available to this key.");
            return new AssistClient(provider, key, candidates, http);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends one prompt with images (JPEG) and returns the text of the answer, or null when the service
    /// declined to answer.
    /// </summary>
    public async Task<string?> AskAsync(string prompt, IReadOnlyList<byte[]> jpegImages, int maxTokens = 1024,
        CancellationToken cancellationToken = default)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0, switches = 0; ; attempt++)
            {
                string model = Model;
                using HttpRequestMessage request = Provider == AssistProvider.Anthropic
                    ? AnthropicRequest(model, prompt, jpegImages, maxTokens)
                    : ChatRequest(model, prompt, jpegImages, maxTokens);
                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    if (attempt < RetryDelays.Length)
                    {
                        await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new AssistException(AssistFailure.Unavailable, e.Message, e);
                }

                using (response)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return Provider == AssistProvider.Anthropic ? AnthropicText(body) : ChatText(body);

                    if (IsBusy(response.StatusCode))
                    {
                        // Another model, at once, if there is one that is not resting; otherwise wait and retry.
                        RestModel(model, response);
                        if (Model != model && switches < _models.Count)
                        {
                            switches++;
                            attempt--;
                            continue;
                        }

                        if (attempt < RetryDelays.Length)
                        {
                            await Task.Delay(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }

                    throw Failure(response.StatusCode, body);
                }
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _oneAtATime.Dispose();
    }

    /// <summary>Leaves a busy model alone for a while (longer, when the service says so).</summary>
    private void RestModel(string model, HttpResponseMessage response)
    {
        TimeSpan pause = response.Headers.RetryAfter?.Delta is { } asked && asked > BusyPause ? asked : BusyPause;
        lock (_busyUntil)
            _busyUntil[model] = DateTime.UtcNow + pause;
    }

    private static bool IsBusy(HttpStatusCode code) =>
        code is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout || (int)code == 529;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan? asked = response.Headers.RetryAfter?.Delta;
        return asked is { } a && a > TimeSpan.Zero && a <= TimeSpan.FromSeconds(20) ? a : RetryDelays[attempt];
    }

    private static AssistException Failure(HttpStatusCode code, string body) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new AssistException(AssistFailure.BadKey, $"The service refused the key ({(int)code})."),
        _ when IsBusy(code) => new AssistException(AssistFailure.Unavailable, $"The service is busy ({(int)code})."),
        _ => new AssistException(AssistFailure.BadAnswer, $"The service answered {(int)code}: {Shorten(body)}"),
    };

    private static string Shorten(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private static void Authorize(HttpRequestMessage request, AssistProvider provider, string key)
    {
        if (provider == AssistProvider.Anthropic)
        {
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", AnthropicVersion);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private static async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient http, AssistProvider provider, string key,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, provider == AssistProvider.Anthropic ? "models?limit=1000" : "models");
        Authorize(request, provider, key);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new AssistException(AssistFailure.Unavailable, e.Message, e);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw Failure(response.StatusCode, body);
            return ParseModelIds(body);
        }
    }

    /// <summary>Model ids from an OpenAI- or Anthropic-style list ({"data":[{"id":…}]}); "models/" is dropped (Google).</summary>
    internal static IReadOnlyList<string> ParseModelIds(string body)
    {
        JsonNode? root = ParseJson(body);
        if (root?["data"] is not JsonArray data)
            throw new AssistException(AssistFailure.BadAnswer, "The model list could not be read.");
        return data.Select(m => m?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id)
            .ToList();
    }

    /// <summary>OpenRouter shows its model list to anyone; the key itself is checked here.</summary>
    private static async Task CheckOpenRouterKeyAsync(HttpClient http, string key, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "key");
        Authorize(request, AssistProvider.OpenRouter, key);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>OpenAI-style chat completion with images as data URLs.</summary>
    private HttpRequestMessage ChatRequest(string model, string prompt, IReadOnlyList<byte[]> images, int maxTokens)
    {
        JsonArray content = [new JsonObject { ["type"] = "text", ["text"] = prompt }];
        foreach (byte[] jpeg in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) },
            });
        }

        JsonObject body = new()
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };

        // OpenAI's newer models take only max_completion_tokens; the compatible services take max_tokens.
        body[Provider == AssistProvider.OpenAI ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        // DeepSeek thinks before answering by default, and the thinking counts against max_tokens:
        // a short answer then never comes. These questions need no thinking.
        if (Provider == AssistProvider.DeepSeek)
            body["thinking"] = new JsonObject { ["type"] = "disabled" };

        HttpRequestMessage request = new(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request, Provider, _key);
        return request;
    }

    private static string? ChatText(string body)
    {
        JsonNode? root = ParseJson(body);
        JsonNode? message = root?["choices"]?[0]?["message"];
        if (message?["refusal"] is JsonValue refusal && refusal.TryGetValue(out string? r) && !string.IsNullOrEmpty(r))
            return null;
        return message?["content"]?.GetValue<string>();
    }

    /// <summary>Anthropic Messages API: images first, then the question; little thinking for these small tasks.</summary>
    private HttpRequestMessage AnthropicRequest(string model, string prompt, IReadOnlyList<byte[]> images, int maxTokens)
    {
        JsonArray content = [];
        foreach (byte[] jpeg in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"] = Convert.ToBase64String(jpeg),
                },
            });
        }

        content.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });
        JsonObject body = new()
        {
            ["model"] = model,
            ["max_tokens"] = Math.Max(maxTokens, 2048),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };

        // Effort is not accepted by the 4.5 models.
        if (!model.Contains("-4-", StringComparison.Ordinal))
            body["output_config"] = new JsonObject { ["effort"] = "low" };

        HttpRequestMessage request = new(HttpMethod.Post, "messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request, AssistProvider.Anthropic, _key);
        return request;
    }

    private static string? AnthropicText(string body)
    {
        JsonNode? root = ParseJson(body);
        if (root?["stop_reason"]?.GetValue<string>() == "refusal" || root?["content"] is not JsonArray blocks)
            return null;
        return string.Concat(blocks
            .Where(b => b?["type"]?.GetValue<string>() == "text")
            .Select(b => b!["text"]?.GetValue<string>()));
    }

    internal static JsonNode? ParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

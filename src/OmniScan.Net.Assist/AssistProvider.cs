using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniScan.Net.Assist;

/// <summary>The online services that can look at images. Only services with image input are listed.</summary>
public enum AssistProvider
{
    OpenAI,
    Google,
    Anthropic,
    XAi,
    OpenRouter,
    DeepSeek,
}

/// <summary>What is known about each service: how its keys look, where it lives, which models to prefer.</summary>
public static class AssistProviders
{
    /// <summary>
    /// The service a key belongs to, by its prefix, or null when the key looks like none of them.
    /// The key is then confirmed by asking the service for its model list.
    /// </summary>
    public static AssistProvider? Detect(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key))
            return null;
        if (key.StartsWith("sk-ant-", StringComparison.Ordinal))
            return AssistProvider.Anthropic;
        if (key.StartsWith("sk-or-", StringComparison.Ordinal))
            return AssistProvider.OpenRouter;
        if (key.StartsWith("xai-", StringComparison.Ordinal))
            return AssistProvider.XAi;
        // Google AI Studio: the older "AIza" keys (39 characters) and the newer "AQ." ones.
        if ((key.StartsWith("AIza", StringComparison.Ordinal) && key.Length == 39) || key.StartsWith("AQ.", StringComparison.Ordinal))
            return AssistProvider.Google;
        // DeepSeek keys are "sk-" and 32 lowercase hex digits; OpenAI keys are longer and mixed-case.
        if (key.StartsWith("sk-", StringComparison.Ordinal) && key.Length == 35 && key.Skip(3).All(char.IsAsciiHexDigitLower))
            return AssistProvider.DeepSeek;
        if (key.StartsWith("sk-", StringComparison.Ordinal))
            return AssistProvider.OpenAI;
        return null;
    }

    public static string Name(AssistProvider p) => p switch
    {
        AssistProvider.OpenAI => "OpenAI",
        AssistProvider.Google => "Google AI Studio",
        AssistProvider.Anthropic => "Anthropic",
        AssistProvider.XAi => "xAI",
        AssistProvider.OpenRouter => "OpenRouter",
        AssistProvider.DeepSeek => "DeepSeek",
        _ => p.ToString(),
    };

    /// <summary>Base address of the API (OpenAI-style chat completions for all but Anthropic).</summary>
    internal static Uri BaseUri(AssistProvider p) => new(p switch
    {
        AssistProvider.OpenAI => "https://api.openai.com/v1/",
        AssistProvider.Google => "https://generativelanguage.googleapis.com/v1beta/openai/",
        AssistProvider.Anthropic => "https://api.anthropic.com/v1/",
        AssistProvider.XAi => "https://api.x.ai/v1/",
        AssistProvider.OpenRouter => "https://openrouter.ai/api/v1/",
        AssistProvider.DeepSeek => "https://api.deepseek.com/",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    });

    /// <summary>
    /// Models to use, best first: cheap ones that read images well. For OpenAI gpt-6-luna, the replacement of the
    /// 4.1 and 4o models (closed in January 2027); the larger gpt-6.1-terra also gets the photos where only a sign
    /// tells which way is up, at ten times the price: it can be set by hand, [Assist] Model. For Google the newest light
    /// model comes first: it reads turned photos best (and a listed older model may be closed to new keys). The first one the key can use is taken; when none is listed,
    /// <see cref="Fallback"/> picks from the list by name.
    /// </summary>
    internal static IReadOnlyList<string> Preferred(AssistProvider p) => p switch
    {
        AssistProvider.OpenAI => ["gpt-6-luna", "gpt-4.1-mini", "gpt-4o-mini"],
        AssistProvider.Google => ["gemini-3.5-flash-lite", "gemini-flash-lite-latest", "gemini-3.1-flash-lite", "gemini-2.5-flash-lite"],
        AssistProvider.Anthropic => ["claude-haiku-5-5", "claude-haiku-4-5"],
        AssistProvider.XAi => ["grok-4-fast", "grok-2-vision-1212", "grok-2-vision"],
        AssistProvider.DeepSeek => ["deepseek-flash"],
        AssistProvider.OpenRouter => ["google/gemini-2.5-flash-lite", "openai/gpt-4.1-mini", "openai/gpt-4o-mini", "anthropic/claude-haiku-4.5"],
        _ => [],
    };

    /// <summary>A usable model from the list when none of <see cref="Preferred"/> is there, or null.</summary>
    internal static string? Fallback(AssistProvider p, IReadOnlyList<string> models)
    {
        IEnumerable<string> Matching(params string[] parts) =>
            models.Where(m => parts.All(part => m.Contains(part, StringComparison.OrdinalIgnoreCase)))
                  .Where(m => !NotForImages(m))
                  .OrderBy(m => m.Length);

        return p switch
        {
            AssistProvider.OpenAI => Matching("gpt-", "luna").FirstOrDefault() ?? Matching("gpt-", "mini").FirstOrDefault(),
            AssistProvider.Google => Matching("gemini", "flash-lite").FirstOrDefault() ?? Matching("gemini", "flash").FirstOrDefault(),
            AssistProvider.Anthropic => Matching("haiku").FirstOrDefault() ?? Matching("sonnet").FirstOrDefault(),
            AssistProvider.XAi => Matching("grok", "vision").FirstOrDefault() ?? Matching("grok").FirstOrDefault(),
            // Only the Flash models read images; Pro does not.
            AssistProvider.DeepSeek => Matching("deepseek", "flash").FirstOrDefault(),
            _ => null,
        };
    }

    private static bool NotForImages(string model) =>
        new[] { "audio", "realtime", "tts", "transcribe", "search", "embedding", "image-generation", "-image" }
            .Any(x => model.Contains(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>Picks the model to use from the ones the key can see.</summary>
    public static string? ChooseModel(AssistProvider p, IReadOnlyList<string> available) =>
        Candidates(p, available, null).FirstOrDefault();

    /// <summary>
    /// The models to use, in order, from the ones the key can see: the one set by hand (when the key can
    /// use it), then the preferred ones, then one picked by name. A busy model gives way to the next.
    /// </summary>
    public static IReadOnlyList<string> Candidates(AssistProvider p, IReadOnlyList<string> available, string? wanted)
    {
        List<string> list = [];
        void Add(string? model)
        {
            string? known = model is null ? null : available.FirstOrDefault(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
            if (known is not null && !list.Contains(known, StringComparer.OrdinalIgnoreCase))
                list.Add(known);
        }

        Add(wanted?.Trim());
        foreach (string model in Preferred(p))
            Add(model);
        Add(Fallback(p, available));
        return list;
    }
}

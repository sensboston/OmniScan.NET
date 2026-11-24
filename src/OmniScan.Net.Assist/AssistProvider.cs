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
        if (key.StartsWith("AIza", StringComparison.Ordinal) && key.Length == 39)
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
    /// Models to use, best first: cheap ones that read images well. (The full gpt-4.1 also gets the photos where
    /// only a sign tells which way is up, at several times the price: it can be set by hand, [Assist] Model.) For the free Google tier
    /// the previous-generation light model comes first: it is the least crowded, so the least often
    /// refused for load. The first one the key can use is taken; when none is listed,
    /// <see cref="Fallback"/> picks from the list by name.
    /// </summary>
    internal static IReadOnlyList<string> Preferred(AssistProvider p) => p switch
    {
        AssistProvider.OpenAI => ["gpt-4.1-mini", "gpt-4o-mini", "gpt-4.1", "gpt-4o"],
        AssistProvider.Google => ["gemini-2.5-flash-lite", "gemini-2.0-flash-lite", "gemini-2.0-flash", "gemini-2.5-flash", "gemini-flash-lite-latest"],
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
            AssistProvider.OpenAI => Matching("gpt-", "mini").FirstOrDefault(),
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
    public static string? ChooseModel(AssistProvider p, IReadOnlyList<string> available)
    {
        foreach (string want in Preferred(p))
        {
            if (available.Contains(want, StringComparer.OrdinalIgnoreCase))
                return want;
        }

        return Fallback(p, available);
    }
}

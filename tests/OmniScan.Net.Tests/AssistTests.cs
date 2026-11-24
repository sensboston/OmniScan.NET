using System.Collections.Generic;
using OmniScan.Net.Assist;

namespace OmniScan.Net.Tests;

/// <summary>The offline part of the online services: keys, model choice, reading answers.</summary>
public sealed class AssistTests
{
    [Theory]
    [InlineData("sk-ant-api03-abcdef", AssistProvider.Anthropic)]
    [InlineData("sk-or-v1-abcdef", AssistProvider.OpenRouter)]
    [InlineData("sk-proj-abcdef", AssistProvider.OpenAI)]
    [InlineData("sk-abcdef", AssistProvider.OpenAI)]
    [InlineData("xai-abcdef", AssistProvider.XAi)]
    [InlineData("  AIzaSyA12345678901234567890123456789012  ", AssistProvider.Google)]
    public void Detect_ByPrefix(string key, AssistProvider expected) =>
        Assert.Equal(expected, AssistProviders.Detect(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("AIzaTooShort")]
    public void Detect_UnknownKey_IsNull(string? key) => Assert.Null(AssistProviders.Detect(key));

    [Fact]
    public void ChooseModel_TakesTheFirstPreferredThatIsAvailable()
    {
        List<string> google = ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.0-flash-lite", "embedding-001"];
        Assert.Equal("gemini-2.0-flash-lite", AssistProviders.ChooseModel(AssistProvider.Google, google));

        List<string> anthropic = ["claude-opus-5-5", "claude-haiku-5-5", "claude-sonnet-5-5"];
        Assert.Equal("claude-haiku-5-5", AssistProviders.ChooseModel(AssistProvider.Anthropic, anthropic));
    }

    [Fact]
    public void ChooseModel_FallsBackByName_SkippingNonImageModels()
    {
        List<string> openai = ["gpt-9-mini-realtime", "gpt-9-mini-tts", "gpt-9-mini", "text-embedding-3-small"];
        Assert.Equal("gpt-9-mini", AssistProviders.ChooseModel(AssistProvider.OpenAI, openai));

        List<string> google = ["gemini-9-pro", "gemini-9-flash-lite-preview"];
        Assert.Equal("gemini-9-flash-lite-preview", AssistProviders.ChooseModel(AssistProvider.Google, google));
    }

    [Fact]
    public void ChooseModel_NothingUsable_IsNull() =>
        Assert.Null(AssistProviders.ChooseModel(AssistProvider.OpenAI, ["text-embedding-3-small", "whisper-1"]));

    [Fact]
    public void ParseModelIds_StripsGooglePrefix()
    {
        IReadOnlyList<string> ids = AssistClient.ParseModelIds(
            """{"object":"list","data":[{"id":"models/gemini-2.5-flash"},{"id":"gpt-4.1-mini"},{"x":1}]}""");
        Assert.Equal(["gemini-2.5-flash", "gpt-4.1-mini"], ids);
    }

    [Fact]
    public void ParseModelIds_Garbage_Throws() =>
        Assert.Throws<AssistException>(() => AssistClient.ParseModelIds("<html>nope</html>"));

    [Theory]
    [InlineData("""{"upright": 3, "caption": "Two children on a beach."}""", 180, "Two children on a beach")]
    [InlineData("```json\n{\"upright\": \"2\", \"caption\": \"Кот на диване\"}\n```", 90, "Кот на диване")]
    [InlineData("""{"upright": 1, "caption": "космический шаттл в музее."}""", 0, "Космический шаттл в музее")]
    [InlineData("""Sure! {"upright": null, "caption": ""}""", null, null)]
    [InlineData("""{"upright": 7}""", null, null)]
    [InlineData("no json here", null, null)]
    public void Parse_IsTolerant(string answer, int? rotation, string? caption)
    {
        PhotoReport report = PhotoAssist.Parse(answer, orient: true, describe: true);
        Assert.Equal(rotation, report.Rotation);
        Assert.Equal(caption, report.Description);
    }

    [Fact]
    public void Prompt_AsksOnlyForWhatIsWanted()
    {
        Assert.DoesNotContain("caption", PhotoAssist.Prompt(orient: true, describe: false, "Russian"));
        string both = PhotoAssist.Prompt(orient: true, describe: true, "Russian");
        Assert.Contains("upright", both);
        Assert.Contains("in Russian", both);
    }

    [Theory]
    [InlineData("ru", "Russian")]
    [InlineData("de", "German")]
    [InlineData("pt-BR", "Portuguese")]
    public void LanguageName_FromUiCode(string code, string expected) =>
        Assert.Equal(expected, PhotoAssist.LanguageName(code));
}

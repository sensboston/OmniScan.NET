using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
    [InlineData("sk-0123456789abcdef0123456789abcdef", AssistProvider.DeepSeek)]
    [InlineData("sk-0123456789ABCDEF0123456789abcdef", AssistProvider.OpenAI)]
    public void Detect_ByPrefix(string key, AssistProvider expected) =>
        Assert.Equal(expected, AssistProviders.Detect(key));

    // Google keys are put together at run time: written out in full they look like real keys to scanners.
    [Theory]
    [InlineData("  AIza", 35, "  ")]
    [InlineData("AQ.", 48, "")]
    public void Detect_GoogleKeys(string prefix, int length, string suffix) =>
        Assert.Equal(AssistProvider.Google, AssistProviders.Detect(prefix + new string('x', length) + suffix));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("AIzaTooShort")]
    public void Detect_UnknownKey_IsNull(string? key) => Assert.Null(AssistProviders.Detect(key));

    [Fact]
    public void ChooseModel_TakesTheFirstPreferredThatIsAvailable()
    {
        List<string> google = ["gemini-2.5-pro", "gemini-2.5-flash-lite", "gemini-3.1-flash-lite", "embedding-001"];
        Assert.Equal("gemini-3.1-flash-lite", AssistProviders.ChooseModel(AssistProvider.Google, google));

        List<string> anthropic = ["claude-opus-5-5", "claude-haiku-5-5", "claude-sonnet-5-5"];
        Assert.Equal("claude-haiku-5-5", AssistProviders.ChooseModel(AssistProvider.Anthropic, anthropic));
    }

    [Fact]
    public void ChooseModel_FallsBackByName_SkippingNonImageModels()
    {
        List<string> openai = ["gpt-9-mini-realtime", "gpt-9-mini-tts", "gpt-9-mini", "text-embedding-3-small"];
        Assert.Equal("gpt-9-mini", AssistProviders.ChooseModel(AssistProvider.OpenAI, openai));
        Assert.Equal("gpt-7-luna", AssistProviders.ChooseModel(AssistProvider.OpenAI, ["gpt-9-mini", "gpt-7-luna-tts", "gpt-7-luna"]));

        List<string> google = ["gemini-9-pro", "gemini-9-flash-lite-preview"];
        Assert.Equal("gemini-9-flash-lite-preview", AssistProviders.ChooseModel(AssistProvider.Google, google));
    }

    [Fact]
    public void Candidates_PutTheModelSetByHandFirst_ThenThePreferred_WithoutRepeats()
    {
        List<string> openai = ["gpt-6.1-terra", "gpt-4o-mini", "gpt-6-luna", "gpt-4.1-mini", "whisper-1"];
        Assert.Equal(["gpt-6.1-terra", "gpt-6-luna", "gpt-4.1-mini", "gpt-4o-mini"], AssistProviders.Candidates(AssistProvider.OpenAI, openai, " GPT-6.1-TERRA "));
        Assert.Equal(["gpt-6-luna", "gpt-4.1-mini", "gpt-4o-mini"], AssistProviders.Candidates(AssistProvider.OpenAI, openai, "gpt-unknown"));
    }

    /// <summary>Answers by model: busy for the ones listed, a normal answer naming the model for the rest.</summary>
    private sealed class FakeService(params string[] busy) : HttpMessageHandler
    {
        public string[] Gone { get; init; } = [];

        public List<string> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string model = System.Text.Json.Nodes.JsonNode.Parse(body)!["model"]!.GetValue<string>();
            Asked.Add(model);
            if (System.Array.IndexOf(Gone, model) >= 0)
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no longer available") };
            if (System.Array.IndexOf(busy, model) >= 0)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("overloaded") };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""{"choices":[{"message":{"content":"from {{{model}}}"}}]}"""),
            };
        }
    }

    [Fact]
    public async Task BusyModel_GivesWayToTheNext_AndRestsForTheNextRequests()
    {
        FakeService service = new("gemini-2.5-flash-lite");
        using AssistClient client = AssistClient.Create(AssistProvider.Google, ["gemini-2.5-flash-lite", "gemini-2.0-flash-lite"], service);

        Assert.Equal("from gemini-2.0-flash-lite", await client.AskAsync("q", []));
        Assert.Equal("from gemini-2.0-flash-lite", await client.AskAsync("q", []));

        Assert.Equal(["gemini-2.5-flash-lite", "gemini-2.0-flash-lite", "gemini-2.0-flash-lite"], service.Asked);
        Assert.Equal("gemini-2.0-flash-lite", client.Model);
    }

    [Fact]
    public async Task ClosedModel_IsSkipped_AndTheLastOneClosed_MeansNoModel()
    {
        FakeService service = new() { Gone = ["gemini-2.5-flash-lite"] };
        using AssistClient client = AssistClient.Create(AssistProvider.Google, ["gemini-2.5-flash-lite", "gemini-3.5-flash-lite"], service);

        Assert.Equal("from gemini-3.5-flash-lite", await client.AskAsync("q", []));
        Assert.Equal("gemini-3.5-flash-lite", client.Model);

        FakeService none = new() { Gone = ["gemini-2.5-flash-lite"] };
        using AssistClient only = AssistClient.Create(AssistProvider.Google, ["gemini-2.5-flash-lite"], none);
        AssistException e = await Assert.ThrowsAsync<AssistException>(() => only.AskAsync("q", []));
        Assert.Equal(AssistFailure.NoModel, e.Failure);
    }

    [Fact]
    public void Captions_AreAskedWithoutNumbersOrAddresses_AndWithSearchWords()
    {
        // The caption ends up in the file's properties, descript.ion and the search index.
        foreach (string prompt in new[] { PhotoAssist.DocumentPrompt("Russian"), PhotoAssist.Prompt(orient: true, describe: true, "English") })
        {
            Assert.Contains("Never write numbers of accounts, cards, documents", prompt);
            Assert.Contains("dates of birth", prompt);
            Assert.Contains("search", prompt);
        }

        Assert.Contains("in Russian", PhotoAssist.DocumentPrompt("Russian"));
        Assert.Equal("Massachusetts ID card (Mass ID, driver's license)",
            PhotoAssist.Parse("""{"caption": "massachusetts ID card (Mass ID, driver's license)."}""", orient: false, describe: true).Description);
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

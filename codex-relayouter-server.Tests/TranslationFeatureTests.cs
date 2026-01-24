using System.Text.Json;
using codex_bridge_server.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace codex_bridge_server.Tests;

public sealed class TranslationFeatureTests
{
    private static JsonSerializerOptions WebJsonOptions { get; } = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://example.com/custom", "https://example.com/custom/v1/chat/completions")]
    [InlineData("https://example.com/custom/v1", "https://example.com/custom/v1/chat/completions")]
    public void BuildChatCompletionsEndpoint_normalizesBaseUrl(string baseUrl, string expected)
    {
        var uri = OpenAiChatCompletionsTranslationClient.BuildChatCompletionsEndpoint(baseUrl);
        Assert.NotNull(uri);
        Assert.Equal(expected, uri!.ToString());
    }

    [Fact]
    public void ApplyRunReasoningTranslation_updatesTextAndAddsMetadata()
    {
        var envelope = new BridgeEnvelope
        {
            Type = "event",
            Name = "run.reasoning",
            Data = JsonSerializer.SerializeToElement(new { runId = "r1", itemId = "i1", text = "orig", sessionId = "s1" }, WebJsonOptions),
        };

        var updated = WebSocketHub.ApplyRunReasoningTranslation(envelope, translatedText: "译文", locale: "zh-CN");

        Assert.True(updated.Data.TryGetProperty("runId", out var runId));
        Assert.Equal("r1", runId.GetString());
        Assert.True(updated.Data.TryGetProperty("itemId", out var itemId));
        Assert.Equal("i1", itemId.GetString());
        Assert.True(updated.Data.TryGetProperty("sessionId", out var sessionId));
        Assert.Equal("s1", sessionId.GetString());
        Assert.True(updated.Data.TryGetProperty("text", out var text));
        Assert.Equal("译文", text.GetString());
        Assert.True(updated.Data.TryGetProperty("translated", out var translated));
        Assert.True(translated.GetBoolean());
        Assert.True(updated.Data.TryGetProperty("translationLocale", out var locale));
        Assert.Equal("zh-CN", locale.GetString());
    }

    [Fact]
    public async Task TranslationService_cachesByCanonicalReasoningContent()
    {
        var filePath = GetTempFilePath();
        var cache = new TranslationCacheStore(NullLogger<TranslationCacheStore>.Instance, filePath);
        var fake = new FakeTranslator("**已翻译标题**\n\n已翻译正文");

        var options = Options.Create(
            new BridgeTranslationOptions
            {
                Enabled = true,
                BaseUrl = "https://api.openai.com",
                ApiKey = "k",
                TargetLocale = "zh-CN",
                Model = "gpt-4.1-mini",
                MaxRequestsPerSecond = 100,
                MaxConcurrency = 1,
            });

        using var svc = new BridgeTranslationService(options, cache, fake, NullLogger<BridgeTranslationService>.Instance);

        var first = await svc.TranslateReasoningAsync("**Title**\n\nDetail", CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("zh-CN", first!.Locale);
        Assert.Equal("已翻译标题", first.Title);
        Assert.Equal("已翻译正文", first.Text);
        Assert.Equal("**已翻译标题**\n\n已翻译正文", first.RawText);
        Assert.Equal(1, fake.Calls);

        var second = await svc.TranslateReasoningAsync("**Title**\n\nDetail", CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("**已翻译标题**\n\n已翻译正文", second!.RawText);
        Assert.Equal(1, fake.Calls);

        var trace = new CodexSessionTraceEntry { Kind = "reasoning", Title = "Title", Text = "Detail" };
        Assert.True(svc.TryApplyCachedTranslationToReasoningTrace(trace));
        Assert.Equal("已翻译标题", trace.Title);
        Assert.Equal("已翻译正文", trace.Text);
    }

    [Fact]
    public async Task TranslationService_disabled_returnsNullAndDoesNotCallTranslator()
    {
        var filePath = GetTempFilePath();
        var cache = new TranslationCacheStore(NullLogger<TranslationCacheStore>.Instance, filePath);
        var fake = new FakeTranslator("ignored");

        var options = Options.Create(
            new BridgeTranslationOptions
            {
                Enabled = false,
                BaseUrl = "https://api.openai.com",
                ApiKey = "k",
                TargetLocale = "zh-CN",
                Model = "gpt-4.1-mini",
            });

        using var svc = new BridgeTranslationService(options, cache, fake, NullLogger<BridgeTranslationService>.Instance);

        var translated = await svc.TranslateReasoningAsync("**Title**\n\nDetail", CancellationToken.None);
        Assert.Null(translated);
        Assert.Equal(0, fake.Calls);
    }

    private static string GetTempFilePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codex-relayouter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "translations.json");
    }

    private sealed class FakeTranslator : ITextTranslator
    {
        private readonly string _output;
        public int Calls { get; private set; }

        public FakeTranslator(string output)
        {
            _output = output;
        }

        public Task<string?> TranslateToZhCnAsync(string input, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<string?>(_output);
        }
    }
}


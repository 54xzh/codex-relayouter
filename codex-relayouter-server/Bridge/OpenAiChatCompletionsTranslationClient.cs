// OpenAiChatCompletionsTranslationClient：OpenAI 兼容 /v1/chat/completions 翻译客户端。
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace codex_bridge_server.Bridge;

public sealed class OpenAiChatCompletionsTranslationClient : ITextTranslator
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<BridgeTranslationOptions> _options;
    private readonly ILogger<OpenAiChatCompletionsTranslationClient> _logger;

    public OpenAiChatCompletionsTranslationClient(
        IHttpClientFactory httpClientFactory,
        IOptions<BridgeTranslationOptions> options,
        ILogger<OpenAiChatCompletionsTranslationClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<string?> TranslateToZhCnAsync(string input, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return null;
        }

        var baseUrl = options.BaseUrl?.Trim();
        var apiKey = options.ApiKey?.Trim();
        var model = options.Model?.Trim();

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var endpoint = BuildChatCompletionsEndpoint(baseUrl);
        if (endpoint is null)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.TimeoutMs > 0)
        {
            cts.CancelAfter(options.TimeoutMs);
        }

        var system = """
你是一个翻译器。请将用户提供的文本翻译为简体中文（zh-CN），并遵守：
- 尽量保留原始 Markdown 结构（标题、列表、空行、换行、缩进、代码块、引用等）
- 不要翻译代码块与行内代码（```...``` 与 `...` 内的内容保持不变）
- 不要改写命令、文件路径、URL、API 名称、标识符、JSON key、错误码等技术文本
- 只输出译文，不要添加额外解释
""";

        var payload = new
        {
            model,
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = input },
            },
        };

        var json = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Utf8NoBom, "application/json");

        var client = _httpClientFactory.CreateClient();

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("翻译请求失败: status={StatusCode}", (int)response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var first = choices.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!first.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var translated = content.GetString();
            return string.IsNullOrWhiteSpace(translated) ? null : translated.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static Uri? BuildChatCompletionsEndpoint(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var trimmed = baseUrl.Trim();
        if (!trimmed.EndsWith("/", StringComparison.Ordinal))
        {
            trimmed += "/";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var normalized = uri.ToString().TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(0, normalized.Length - 3);
        }

        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        return new Uri(baseUri, "v1/chat/completions");
    }
}

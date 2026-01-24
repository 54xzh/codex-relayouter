// BridgeTranslationService：负责翻译缓存、去重、限流/并发控制，并提供“思考摘要”翻译能力。
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace codex_bridge_server.Bridge;

public sealed class BridgeTranslationService : IDisposable
{
    private const string CacheKeyVersion = "v1";

    private readonly IOptions<BridgeTranslationOptions> _options;
    private readonly TranslationCacheStore _cache;
    private readonly ITextTranslator _client;
    private readonly ILogger<BridgeTranslationService> _logger;

    private readonly SemaphoreSlim _concurrencyGate;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly ConcurrentDictionary<string, Task<TranslationCacheEntry?>> _inflight = new(StringComparer.Ordinal);

    public BridgeTranslationService(
        IOptions<BridgeTranslationOptions> options,
        TranslationCacheStore cache,
        ITextTranslator client,
        ILogger<BridgeTranslationService> logger)
    {
        _options = options;
        _cache = cache;
        _client = client;
        _logger = logger;

        var cfg = _options.Value;
        var maxConcurrency = cfg.MaxConcurrency <= 0 ? 1 : cfg.MaxConcurrency;
        _concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var rps = cfg.MaxRequestsPerSecond <= 0 ? 1 : cfg.MaxRequestsPerSecond;
        _rateLimiter = new TokenBucketRateLimiter(
            new TokenBucketRateLimiterOptions
            {
                TokenLimit = rps,
                TokensPerPeriod = rps,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                AutoReplenishment = true,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 256,
            });
    }

    public bool IsEnabled => IsConfigEnabled(_options.Value);

    public bool TryGetReasoningTranslation(string sourceRawText, out TranslationCacheEntry entry)
    {
        entry = default!;

        var options = _options.Value;
        if (!IsConfigEnabled(options))
        {
            return false;
        }

        if (!TryBuildSourceKey(options, sourceRawText, out var key, out _))
        {
            return false;
        }

        return _cache.TryGet(key, out entry!);
    }

    public Task<TranslationCacheEntry?> TranslateReasoningAsync(string sourceRawText, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!IsConfigEnabled(options))
        {
            return Task.FromResult<TranslationCacheEntry?>(null);
        }

        if (!TryBuildSourceKey(options, sourceRawText, out var key, out var sourceHash))
        {
            return Task.FromResult<TranslationCacheEntry?>(null);
        }

        if (_cache.TryGet(key, out var cached))
        {
            return Task.FromResult<TranslationCacheEntry?>(cached);
        }

        var task = _inflight.GetOrAdd(key, _ => TranslateAndCacheAsync(key, sourceHash, sourceRawText, cancellationToken));
        return AwaitInflightAsync(key, task);
    }

    public bool TryApplyCachedTranslationToReasoningTrace(CodexSessionTraceEntry trace)
    {
        if (trace is null || !string.Equals(trace.Kind, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var title = trace.Title;
        var text = trace.Text;
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var sourceRaw = BuildReasoningRawText(title, text);
        if (!TryGetReasoningTranslation(sourceRaw, out var cached))
        {
            return false;
        }

        trace.Title = cached.Title;
        trace.Text = cached.Text;
        return true;
    }

    private async Task<TranslationCacheEntry?> AwaitInflightAsync(string key, Task<TranslationCacheEntry?> task)
    {
        try
        {
            return await task;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private async Task<TranslationCacheEntry?> TranslateAndCacheAsync(
        string key,
        string sourceHash,
        string sourceRawText,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = _options.Value;
            if (!IsConfigEnabled(options))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(sourceRawText))
            {
                return null;
            }

            if (options.MaxInputChars > 0 && sourceRawText.Length > options.MaxInputChars)
            {
                return null;
            }

            await _concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
                if (!lease.IsAcquired)
                {
                    return null;
                }

                var translatedCandidate = await _client.TranslateToZhCnAsync(sourceRawText, cancellationToken);
                if (string.IsNullOrWhiteSpace(translatedCandidate))
                {
                    return null;
                }

                SplitReasoningTitle(translatedCandidate, out var translatedTitle, out var translatedDetail);
                var normalizedRaw = BuildReasoningRawText(translatedTitle, translatedDetail);

                var entry = new TranslationCacheEntry
                {
                    Locale = options.TargetLocale.Trim(),
                    SourceHash = sourceHash,
                    Title = translatedTitle,
                    Text = translatedDetail,
                    RawText = normalizedRaw,
                    Model = options.Model.Trim(),
                    CreatedAt = DateTimeOffset.UtcNow,
                };

                _cache.Upsert(key, entry);
                return entry;
            }
            finally
            {
                _concurrencyGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "翻译失败（将保留原文）");
            return null;
        }
    }

    private static bool IsConfigEnabled(BridgeTranslationOptions options)
    {
        if (!options.Enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || string.IsNullOrWhiteSpace(options.ApiKey)
            || string.IsNullOrWhiteSpace(options.Model))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.TargetLocale))
        {
            return false;
        }

        return true;
    }

    private static bool TryBuildSourceKey(BridgeTranslationOptions options, string sourceRawText, out string key, out string sourceHash)
    {
        key = string.Empty;
        sourceHash = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceRawText))
        {
            return false;
        }

        SplitReasoningTitle(sourceRawText, out var title, out var detail);
        var normalizedTitle = title?.Trim() ?? string.Empty;
        var normalizedDetail = detail?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedDetail))
        {
            return false;
        }

        var locale = options.TargetLocale.Trim();
        if (string.IsNullOrWhiteSpace(locale))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes($"{normalizedTitle}\n\n{normalizedDetail}");
        var hashBytes = SHA256.HashData(bytes);
        var hex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        sourceHash = hex;
        key = $"{CacheKeyVersion}:{locale}:{hex}";
        return true;
    }

    private static string BuildReasoningRawText(string? title, string? text)
    {
        var detail = text?.Trim() ?? string.Empty;
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();

        if (resolvedTitle is null)
        {
            return detail;
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            return $"**{resolvedTitle}**";
        }

        return $"**{resolvedTitle}**\n\n{detail}";
    }

    private static void SplitReasoningTitle(string text, out string? title, out string detail)
    {
        title = null;
        detail = text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        if (detail.StartsWith("**", StringComparison.Ordinal))
        {
            var end = detail.IndexOf("**", startIndex: 2, StringComparison.Ordinal);
            if (end > 2)
            {
                title = detail.Substring(2, end - 2).Trim();
                var rest = detail.Substring(end + 2).Trim();
                detail = string.IsNullOrWhiteSpace(rest) ? detail : rest;
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = null;
                }

                return;
            }
        }

        using var reader = new StringReader(detail);
        var firstLine = reader.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            title = firstLine.Length <= 80 ? firstLine : TruncateWithEllipsis(firstLine, 80);
        }
    }

    private static string TruncateWithEllipsis(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        if (maxChars <= 1)
        {
            return "…";
        }

        return string.Concat(text.AsSpan(0, maxChars - 1), "…");
    }

    public void Dispose()
    {
        _rateLimiter.Dispose();
        _concurrencyGate.Dispose();
    }
}

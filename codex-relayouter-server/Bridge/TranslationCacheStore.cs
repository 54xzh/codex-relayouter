// TranslationCacheStore：自动翻译缓存（独立于 ~/.codex/sessions，避免污染会话文件）。
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace codex_bridge_server.Bridge;

public sealed class TranslationCacheStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ILogger<TranslationCacheStore> _logger;
    private readonly string _filePath;
    private readonly object _gate = new();
    private TranslationCacheFile _file = new();

    public TranslationCacheStore(ILogger<TranslationCacheStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = string.IsNullOrWhiteSpace(filePath) ? GetDefaultFilePath() : filePath;
        Load();
    }

    public bool TryGet(string key, out TranslationCacheEntry entry)
    {
        entry = default!;

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (_gate)
        {
            return _file.Entries.TryGetValue(key, out entry!);
        }
    }

    public void Upsert(string key, TranslationCacheEntry entry)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_gate)
        {
            _file.Entries[key] = entry;
            Save();
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _file = new TranslationCacheFile();
                    return;
                }

                var json = File.ReadAllText(_filePath, Utf8NoBom);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _file = new TranslationCacheFile();
                    return;
                }

                var parsed = JsonSerializer.Deserialize<TranslationCacheFile>(json, JsonOptions);
                _file = parsed ?? new TranslationCacheFile();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取翻译缓存失败，将使用空缓存: {Path}", _filePath);
                _file = new TranslationCacheFile();
            }
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_file, JsonOptions);
            File.WriteAllText(_filePath, json, Utf8NoBom);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存翻译缓存失败: {Path}", _filePath);
        }
    }

    private static string GetDefaultFilePath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
        }

        return Path.Combine(baseDir, "codex-relayouter", "translations.json");
    }

    private sealed class TranslationCacheFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("entries")]
        public Dictionary<string, TranslationCacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
    }
}

public sealed class TranslationCacheEntry
{
    [JsonPropertyName("locale")]
    public required string Locale { get; init; }

    [JsonPropertyName("sourceHash")]
    public required string SourceHash { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("rawText")]
    public required string RawText { get; init; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}


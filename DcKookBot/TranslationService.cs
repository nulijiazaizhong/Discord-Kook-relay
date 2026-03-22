using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DcKookBot;

public sealed record TranslationResult(string SourceText, string TranslatedText, string SourceLang, string TargetLang, string ProviderName, string ProviderUrl);

public interface ITranslationProvider
{
    string Name { get; }
    string SourceUrl { get; }
    bool IsConfigured { get; }
    Task<TranslationResult?> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken token);
}

public sealed class TranslationService
{
    private readonly RelayConfig _config;
    private readonly List<ITranslationProvider> _providers;

    public TranslationService(RelayConfig config)
    {
        _config = config;
        _providers = BuildProviders(config);
    }

    public bool Enabled => _providers.Count > 0;

    public async Task<TranslationResult?> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text) || !Enabled)
        {
            return null;
        }

        var normalizedTarget = NormalizeLanguageCode(targetLang, "zh");
        var normalizedSource = NormalizeLanguageCode(sourceLang, "auto");

        if (_config.TranslationAutoDetectSource && string.Equals(normalizedSource, "auto", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSource = DetectLanguage(text);
        }

        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var provider in _providers)
        {
            try
            {
                var result = await provider.TranslateAsync(text, normalizedSource, normalizedTarget, token);
                if (result is not null && !string.IsNullOrWhiteSpace(result.TranslatedText))
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Translate] Provider {provider.Name} failed: {ex.Message}");
            }
        }

        return null;
    }

    private static string NormalizeLanguageCode(string? languageCode, string defaultValue)
    {
        var normalized = (languageCode ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return defaultValue;
        }

        return normalized switch
        {
            "zh-cn" or "zh-hans" => "zh",
            "zh-tw" or "zh-hant" => "cht",
            _ => normalized
        };
    }

    private static string DetectLanguage(string text)
    {
        var hasChinese = false;
        var hasJapanese = false;
        var hasKorean = false;
        var hasLatin = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || char.IsDigit(ch))
            {
                continue;
            }

            if (ch >= '\u4e00' && ch <= '\u9fff')
            {
                hasChinese = true;
                continue;
            }

            if ((ch >= '\u3040' && ch <= '\u30ff') || (ch >= '\u31f0' && ch <= '\u31ff'))
            {
                hasJapanese = true;
                continue;
            }

            if (ch >= '\uac00' && ch <= '\ud7af')
            {
                hasKorean = true;
                continue;
            }

            if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
            {
                hasLatin = true;
            }
        }

        if (hasJapanese)
        {
            return "jp";
        }

        if (hasKorean)
        {
            return "kor";
        }

        if (hasChinese)
        {
            return "zh";
        }

        if (hasLatin)
        {
            return "en";
        }

        return "auto";
    }

    private static List<ITranslationProvider> BuildProviders(RelayConfig config)
    {
        var all = new List<ITranslationProvider>
        {
            new TencentTranslationProvider(config),
            new BaiduTranslationProvider(config)
        };

        var configured = all.Where(p => p.IsConfigured).ToList();
        if (configured.Count == 0 || !config.TranslationEnabled)
        {
            return new List<ITranslationProvider>();
        }

        var provider = (config.TranslationProvider ?? "auto").Trim().ToLowerInvariant();
        if (provider == "auto")
        {
            return configured;
        }

        var selected = configured.Where(p =>
            string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.Name.Replace(" ", string.Empty), provider, StringComparison.OrdinalIgnoreCase)).ToList();

        return selected.Count > 0 ? selected : configured;
    }
}

public sealed class TencentTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient Http = new();
    private readonly RelayConfig _config;

    public TencentTranslationProvider(RelayConfig config)
    {
        _config = config;
    }

    public string Name => "tencent";
    public string SourceUrl => "https://cloud.tencent.com/product/tmt";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.TencentSecretId) && !string.IsNullOrWhiteSpace(_config.TencentSecretKey);

    public async Task<TranslationResult?> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken token)
    {
        if (!IsConfigured)
        {
            return null;
        }

        const string service = "tmt";
        const string host = "tmt.tencentcloudapi.com";
        const string action = "TextTranslate";
        const string version = "2018-03-21";
        const string endpoint = "https://tmt.tencentcloudapi.com";

        var requestPayload = new
        {
            SourceText = text,
            Source = MapTencentLanguage(sourceLang),
            Target = MapTencentLanguage(targetLang),
            ProjectId = 0
        };

        var payload = JsonSerializer.Serialize(requestPayload);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd");

        var canonicalHeaders = $"content-type:application/json; charset=utf-8\nhost:{host}\nx-tc-action:{action.ToLowerInvariant()}\n";
        var signedHeaders = "content-type;host;x-tc-action";
        var hashedRequestPayload = HexEncode(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{hashedRequestPayload}";
        var credentialScope = $"{date}/{service}/tc3_request";
        var hashedCanonicalRequest = HexEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        var stringToSign = $"TC3-HMAC-SHA256\n{timestamp}\n{credentialScope}\n{hashedCanonicalRequest}";

        var secretDate = HmacSha256(Encoding.UTF8.GetBytes($"TC3{_config.TencentSecretKey}"), date);
        var secretService = HmacSha256(secretDate, service);
        var secretSigning = HmacSha256(secretService, "tc3_request");
        var signature = HexEncode(HmacSha256(secretSigning, stringToSign));

        var authorization = $"TC3-HMAC-SHA256 Credential={_config.TencentSecretId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("X-TC-Action", action);
        request.Headers.TryAddWithoutValidation("X-TC-Version", version);
        request.Headers.TryAddWithoutValidation("X-TC-Region", _config.TencentRegion);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString());

        using var response = await Http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tencent translate request failed: {(int)response.StatusCode} {body}");
        }

        using var json = JsonDocument.Parse(body);
        var translated = json.RootElement
            .GetProperty("Response")
            .GetProperty("TargetText")
            .GetString();

        if (string.IsNullOrWhiteSpace(translated))
        {
            return null;
        }

        return new TranslationResult(text, translated, sourceLang, targetLang, Name, SourceUrl);
    }

    private static byte[] HmacSha256(byte[] key, string message)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
    }

    private static string HexEncode(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    private static string MapTencentLanguage(string languageCode)
    {
        return languageCode.ToLowerInvariant() switch
        {
            "auto" => "auto",
            "zh" => "zh",
            "cht" => "zh-TW",
            "ja" or "jp" => "ja",
            "ko" or "kor" => "ko",
            _ => languageCode
        };
    }
}

public sealed class BaiduTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient Http = new();
    private readonly RelayConfig _config;

    public BaiduTranslationProvider(RelayConfig config)
    {
        _config = config;
    }

    public string Name => "baidu";
    public string SourceUrl => "https://cloud.baidu.com/product/mt";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.BaiduAppId) && !string.IsNullOrWhiteSpace(_config.BaiduAppKey);

    public async Task<TranslationResult?> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken token)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var salt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var signRaw = $"{_config.BaiduAppId}{text}{salt}{_config.BaiduAppKey}";
        var sign = Md5Hex(signRaw);

        var form = new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = MapBaiduLanguage(sourceLang),
            ["to"] = MapBaiduLanguage(targetLang),
            ["appid"] = _config.BaiduAppId!,
            ["salt"] = salt,
            ["sign"] = sign
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://fanyi-api.baidu.com/api/trans/vip/translate")
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await Http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Baidu translate request failed: {(int)response.StatusCode} {body}");
        }

        using var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("error_code", out var errorCode))
        {
            var errorMsg = json.RootElement.TryGetProperty("error_msg", out var msg) ? msg.GetString() : "unknown";
            throw new InvalidOperationException($"Baidu translate api error: {errorCode.GetString()} {errorMsg}");
        }

        if (!json.RootElement.TryGetProperty("trans_result", out var transResult) || transResult.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var translated = string.Join("\n", transResult.EnumerateArray()
            .Select(x => x.TryGetProperty("dst", out var dst) ? dst.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        if (string.IsNullOrWhiteSpace(translated))
        {
            return null;
        }

        return new TranslationResult(text, translated, sourceLang, targetLang, Name, SourceUrl);
    }

    private static string Md5Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    private static string MapBaiduLanguage(string languageCode)
    {
        return languageCode.ToLowerInvariant() switch
        {
            "auto" => "auto",
            "zh" => "zh",
            "cht" => "cht",
            "ja" or "jp" => "jp",
            "ko" or "kor" => "kor",
            _ => languageCode
        };
    }
}
using System.ComponentModel.DataAnnotations;

namespace DcKookBot;

public sealed class RelayConfig
{
    [Required]
    public string DiscordToken { get; init; } = string.Empty;

    [Required]
    public string KookToken { get; init; } = string.Empty;

    public string? DiscordGuildId { get; init; }
    public string? KookGuildId { get; init; }

    public string? DiscordChannelMap { get; init; }
    public string? KookChannelMap { get; init; }
    public bool DiscordToKookEnabled { get; init; }
    public bool KookToDiscordEnabled { get; init; }

    public bool RelayEveryoneMentionEnabled { get; init; }

    public bool TranslationEnabled { get; init; }
    public string TranslationProvider { get; init; } = "auto";

    public string? TencentSecretId { get; init; }
    public string? TencentSecretKey { get; init; }
    public string TencentRegion { get; init; } = "ap-guangzhou";

    public string? BaiduAppId { get; init; }
    public string? BaiduAppKey { get; init; }

    public string TranslationBotName { get; init; } = "DC-Kook Bot";
    public bool TranslationAutoDetectSource { get; init; } = true;
    public string DiscordToKookSourceLang { get; init; } = "auto";
    public string DiscordToKookTargetLang { get; init; } = "zh";
    public string KookToDiscordSourceLang { get; init; } = "auto";
    public string KookToDiscordTargetLang { get; init; } = "zh";

    public static RelayConfig FromEnvironment()
    {
        var config = new RelayConfig
        {
            DiscordToken = GetEnvRequired("DISCORD_TOKEN"),
            KookToken = GetEnvRequired("KOOK_TOKEN"),
            DiscordGuildId = GetEnvOptional("DISCORD_GUILD_ID"),
            KookGuildId = GetEnvOptional("KOOK_GUILD_ID"),
            DiscordChannelMap = GetEnvOptional("DISCORD_CHANNEL_MAP"),
            KookChannelMap = GetEnvOptional("KOOK_CHANNEL_MAP"),
            DiscordToKookEnabled = GetEnvBool("DISCORD_TO_KOOK_ENABLED", defaultValue: true),
            KookToDiscordEnabled = GetEnvBool("KOOK_TO_DISCORD_ENABLED", defaultValue: true),
            RelayEveryoneMentionEnabled = GetEnvBool("RELAY_EVERYONE_MENTION_ENABLED", defaultValue: false),
            TranslationEnabled = GetEnvBool("TRANSLATION_ENABLED", defaultValue: true),
            TranslationProvider = GetEnvOptional("TRANSLATION_PROVIDER") ?? "auto",
            TencentSecretId = GetEnvOptional("TENCENT_SECRET_ID"),
            TencentSecretKey = GetEnvOptional("TENCENT_SECRET_KEY"),
            TencentRegion = GetEnvOptional("TENCENT_REGION") ?? "ap-guangzhou",
            BaiduAppId = GetEnvOptional("BAIDU_APP_ID"),
            BaiduAppKey = GetEnvOptional("BAIDU_APP_KEY"),
            TranslationBotName = GetEnvOptional("TRANSLATION_BOT_NAME") ?? "DC-Kook Bot",
            TranslationAutoDetectSource = GetEnvBool("TRANSLATION_AUTO_DETECT_SOURCE", defaultValue: true),
            DiscordToKookSourceLang = GetEnvOptional("DISCORD_TO_KOOK_SOURCE_LANG") ?? "auto",
            DiscordToKookTargetLang = GetEnvOptional("DISCORD_TO_KOOK_TARGET_LANG") ?? "zh",
            KookToDiscordSourceLang = GetEnvOptional("KOOK_TO_DISCORD_SOURCE_LANG") ?? "auto",
            KookToDiscordTargetLang = GetEnvOptional("KOOK_TO_DISCORD_TARGET_LANG") ?? "zh"
        };

        Validator.ValidateObject(config, new ValidationContext(config), validateAllProperties: true);
        return config;
    }

    private static string GetEnvRequired(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required environment variable: {key}");
        }

        return NormalizeEnvValue(value);
    }

    private static string? GetEnvOptional(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : NormalizeEnvValue(value);
    }

    private static bool GetEnvBool(string key, bool defaultValue)
    {
        var value = GetEnvOptional(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEnvValue(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length >= 2)
        {
            var startsWithDoubleQuote = normalized.StartsWith('"');
            var endsWithDoubleQuote = normalized.EndsWith('"');
            var startsWithSingleQuote = normalized.StartsWith('\'');
            var endsWithSingleQuote = normalized.EndsWith('\'');
            if ((startsWithDoubleQuote && endsWithDoubleQuote) || (startsWithSingleQuote && endsWithSingleQuote))
            {
                normalized = normalized[1..^1].Trim();
            }
        }

        return normalized;
    }
}

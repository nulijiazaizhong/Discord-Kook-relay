using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Discord;
using DiscordTag = Discord.ITag;
using DiscordMessage = Discord.IMessage;
using DiscordSocketMessage = Discord.WebSocket.SocketMessage;
using DiscordTextChannel = Discord.WebSocket.SocketTextChannel;
using KookMessage = Kook.WebSocket.SocketMessage;
using KookUser = Kook.WebSocket.SocketGuildUser;
using KookAttachment = Kook.Rest.Attachment;

namespace DcKookBot;

public sealed class MessagePayload
{
    public string Content { get; init; } = string.Empty;
    public List<MessageAttachment> Attachments { get; init; } = new();
    public bool MentionEveryone { get; init; }
}

public sealed record MessageAttachment(string FileName, string Url);

public static class MessageFormatter
{
    public static string BuildDiscordBilingualText(string sourceText, TranslationResult translation, string translationBotName, string? sourceMessageUrl)
    {
        var safeSource = EscapeCodeBlockText(sourceText);
        var safeBotName = string.IsNullOrWhiteSpace(translationBotName) ? "DC-Kook Bot" : translationBotName.Trim();
        var footer = string.IsNullOrWhiteSpace(sourceMessageUrl)
            ? $"（由 {safeBotName} 翻译）"
            : $"（由 {safeBotName} 翻译自 [原公告]({sourceMessageUrl})）";

        return $"```\n{safeSource}\n```\n\n翻译：\n\n{translation.TranslatedText}\n\n{footer}";
    }

    public static string BuildKookBilingualCardContent(string sourceText, TranslationResult translation, string? sourceMessageUrl, string translationBotName)
    {
        var safeSource = EscapeInlineCodeText(sourceText);
        var safeTranslated = EscapeKMarkdownText(translation.TranslatedText);
        var safeSourceUrl = EscapeKMarkdownUrl(sourceMessageUrl ?? string.Empty);
        var safeBotName = EscapeKMarkdownText(string.IsNullOrWhiteSpace(translationBotName) ? "DC-Kook Bot" : translationBotName);

        var sourceModuleContent = $"`{safeSource}`";
        var sourceFooter = string.IsNullOrWhiteSpace(safeSourceUrl)
            ? $"（由 {safeBotName} 翻译）"
            : $"（由 {safeBotName} 翻译自 [原公告]({safeSourceUrl})）";

        var card = new
        {
            type = "card",
            theme = "success",
            size = "lg",
            modules = new object[]
            {
                new
                {
                    type = "section",
                    text = new
                    {
                        type = "kmarkdown",
                        content = sourceModuleContent
                    }
                },
                new
                {
                    type = "divider"
                },
                new
                {
                    type = "section",
                    text = new
                    {
                        type = "kmarkdown",
                        content = "翻译："
                    }
                },
                new
                {
                    type = "section",
                    text = new
                    {
                        type = "kmarkdown",
                        content = safeTranslated
                    }
                },
                new
                {
                    type = "section",
                    text = new
                    {
                        type = "kmarkdown",
                        content = sourceFooter
                    }
                }
            }
        };

        return JsonSerializer.Serialize(new[] { card });
    }

    public static MessagePayload FromDiscord(DiscordSocketMessage message, DiscordTextChannel channel)
    {
        var content = new StringBuilder();
        var text = ReplaceDiscordMentions(message.Content ?? string.Empty, message, channel);
        text = FormatKookLinks(text);
        if (!string.IsNullOrWhiteSpace(text))
        {
            content.AppendLine(text);
        }

        var contentUrls = ExtractUrls(message.Content ?? string.Empty);
        foreach (var embed in message.Embeds)
        {
            if (!string.IsNullOrWhiteSpace(embed.Url) && !contentUrls.Contains(embed.Url))
            {
                content.AppendLine(embed.Url);
            }
        }

        var attachments = message.Attachments
            .Select(a => new MessageAttachment(GetDiscordAttachmentFileName(a), a.Url))
            .ToList();

        var mentionEveryone = message.MentionedEveryone
            || Regex.IsMatch(message.Content ?? string.Empty, @"(^|\s)@everyone(\s|$)", RegexOptions.IgnoreCase);

        return new MessagePayload { Content = content.ToString().Trim(), Attachments = attachments, MentionEveryone = mentionEveryone };
    }

    public static MessagePayload FromKook(KookMessage message, KookUser user)
    {
        var content = new StringBuilder();
        var raw = message.Content ?? string.Empty;
        var attachments = GetKookAttachments(message, raw);

        var text = IsKookCardJson(raw) ? string.Empty : ReplaceKookMentions(raw, message);
        text = NormalizeKookLinksForDiscord(text);
        if (string.IsNullOrWhiteSpace(text) && message.MentionedUsers.Count > 0 && attachments.Count == 0)
        {
            var names = message.MentionedUsers
                .Select(mention => mention is Kook.WebSocket.SocketGuildUser gu ? gu.DisplayName : mention.Username)
                .Select(name => $"@{name}");
            text = string.Join(" ", names);
        }

        if (!string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            content.AppendLine(text);
        }
        else if (!string.IsNullOrWhiteSpace(text) && attachments.Count > 0)
        {
            if (!ShouldSuppressTextForAttachments(text, attachments))
            {
                content.AppendLine(text);
            }
        }

        var shouldAppendCards = attachments.Count == 0;
        if (shouldAppendCards && message.Cards is not null && message.Cards.Count > 0)
        {
            content.AppendLine("[Card] " + string.Join(" | ", message.Cards.Select(c => c.ToString())));
        }

        var mentionEveryone = Regex.IsMatch(raw, @"\(met\)all\(met\)", RegexOptions.IgnoreCase)
            || Regex.IsMatch(raw, @"(^|\s)@everyone(\s|$)", RegexOptions.IgnoreCase);

        return new MessagePayload { Content = content.ToString().Trim(), Attachments = attachments, MentionEveryone = mentionEveryone };
    }

    private static string RenderDiscordTag(DiscordTag tag) => tag.ToString() ?? string.Empty;

    private static string GetDiscordAttachmentFileName(IAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.Filename))
        {
            return attachment.Filename;
        }

        if (Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return "attachment";
    }

    private static string GetKookAttachmentFileName(KookAttachment attachment)
    {
        var fileName = string.IsNullOrWhiteSpace(attachment.Filename) ? string.Empty : attachment.Filename;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return EnsureFileNameHasExtension(fileName, attachment.Url);
        }

        if (Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return "attachment";
    }

    private static List<MessageAttachment> GetKookAttachments(KookMessage message, string rawContent)
    {
        var attachments = new List<MessageAttachment>();
        var cardAttachments = IsKookCardJson(rawContent)
            ? ParseKookCardAttachments(rawContent)
            : new List<MessageAttachment>();

        var cardMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in cardAttachments)
        {
            if (!cardMap.ContainsKey(item.Url))
            {
                cardMap[item.Url] = item.FileName;
            }
        }

        if (message.Attachments is not null)
        {
            foreach (var attachment in message.Attachments)
            {
                var fallbackName = GetKookAttachmentFileName(attachment);
                var fileName = cardMap.TryGetValue(attachment.Url, out var title) ? title : fallbackName;
                attachments.Add(new MessageAttachment(fileName, attachment.Url));
            }
        }

        foreach (var cardAttachment in cardAttachments)
        {
            if (attachments.All(a => !string.Equals(a.Url, cardAttachment.Url, StringComparison.OrdinalIgnoreCase)))
            {
                attachments.Add(cardAttachment);
            }
        }

        return attachments;
    }

    private static List<MessageAttachment> ParseKookCardAttachments(string rawContent)
    {
        var results = new List<MessageAttachment>();
        try
        {
            using var doc = JsonDocument.Parse(rawContent);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var card in doc.RootElement.EnumerateArray())
                {
                    ExtractCardModules(card, results);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                ExtractCardModules(doc.RootElement, results);
            }
        }
        catch
        {
            // ignore parse errors
        }

        return results;
    }

    private static void ExtractCardModules(JsonElement card, List<MessageAttachment> results)
    {
        if (!card.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var module in modules.EnumerateArray())
        {
            if (!module.TryGetProperty("type", out var typeProp))
            {
                continue;
            }

            var type = typeProp.GetString();
            if (!string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = module.TryGetProperty("src", out var srcProp) ? srcProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var title = module.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            var fileName = !string.IsNullOrWhiteSpace(title) ? EnsureFileNameHasExtension(title!, url!) : GetFileNameFromUrl(url!);
            results.Add(new MessageAttachment(fileName, url!));
        }
    }

    private static string GetFileNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return "attachment";
    }

    private static string EnsureFileNameHasExtension(string fileName, string url)
    {
        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return fileName;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var urlExt = Path.GetExtension(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(urlExt))
            {
                return fileName + urlExt;
            }
        }

        return fileName;
    }

    private static string ReplaceKookMentions(string text, KookMessage message)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = Regex.Replace(text, @"\(met\)all\(met\)", "@everyone", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\(met\)here\(met\)", "@here", RegexOptions.IgnoreCase);

        foreach (var mention in message.MentionedUsers)
        {
            var name = mention is Kook.WebSocket.SocketGuildUser gu ? gu.DisplayName : mention.Username;
            var pattern = $@"\(met\){mention.Id}\(met\)";
            text = Regex.Replace(text, pattern, $"@{name}");
        }

        return text;
    }

    private static string ReplaceDiscordMentions(string text, DiscordSocketMessage message, DiscordTextChannel channel)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var user in message.MentionedUsers)
        {
            var displayName = user is Discord.WebSocket.SocketGuildUser guildUser
                ? guildUser.DisplayName
                : user.Username;
            var pattern = $@"<@!?{user.Id}>";
            text = Regex.Replace(text, pattern, $"@{displayName}");
        }

        foreach (var roleId in message.MentionedRoleIds)
        {
            var role = channel.Guild.GetRole(roleId);
            if (role is null)
            {
                continue;
            }

            var pattern = $@"<@&{roleId}>";
            text = Regex.Replace(text, pattern, $"@{role.Name}");
        }

        foreach (var mentionedChannel in message.MentionedChannels)
        {
            if (mentionedChannel is null)
            {
                continue;
            }

            var pattern = $@"<#{mentionedChannel.Id}>";
            text = Regex.Replace(text, pattern, $"#{mentionedChannel.Name}");
        }

        return text;
    }

    private static bool IsKookCardJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        return (trimmed.StartsWith("[{", StringComparison.Ordinal) && trimmed.Contains("\"type\":\"card\"", StringComparison.OrdinalIgnoreCase))
            || (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.Contains("\"type\":\"card\"", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatKookLinks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var protectedLinks = new List<string>();
        var protectedText = Regex.Replace(text, @"\[[^\]]+\]\(https?://[^\s)]+\)", match =>
        {
            protectedLinks.Add(match.Value);
            return $"__LINK_{protectedLinks.Count - 1}__";
        });

        var pattern = @"https?://[^\s<>()]+";
        protectedText = Regex.Replace(protectedText, pattern, match => $"[{match.Value}]({match.Value})");

        return Regex.Replace(protectedText, @"__LINK_(\d+)__", match =>
        {
            var index = int.Parse(match.Groups[1].Value);
            return index >= 0 && index < protectedLinks.Count ? protectedLinks[index] : match.Value;
        });
    }

    private static string NormalizeKookLinksForDiscord(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        // KMarkdown 链接 [text](url) 转为纯 url，确保 Discord 可点击
        return Regex.Replace(text, @"\[[^\]]+\]\((https?://[^\s)]+)\)", "$1");
    }

    private static bool ShouldSuppressTextForAttachments(string text, List<MessageAttachment> attachments)
    {
        var trimmed = text.Trim();
        if (IsKookCardJson(trimmed))
        {
            return true;
        }

        foreach (var attachment in attachments)
        {
            if (string.Equals(trimmed, attachment.FileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> ExtractUrls(string text)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return urls;
        }

        foreach (Match match in Regex.Matches(text, @"https?://[^\s<>()]+"))
        {
            urls.Add(match.Value);
        }

        foreach (Match match in Regex.Matches(text, @"\[[^\]]+\]\((https?://[^\s)]+)\)"))
        {
            if (match.Groups.Count > 1)
            {
                urls.Add(match.Groups[1].Value);
            }
        }

        return urls;
    }

    private static string EscapeInlineCodeText(string text)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Trim();
        return normalized.Replace("`", "\\`");
    }

    private static string EscapeCodeBlockText(string text)
    {
        return (text ?? string.Empty).Replace("```", "'''");
    }

    private static string EscapeKMarkdownText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }

    private static string EscapeKMarkdownUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return url.Replace(")", "%29");
    }

    private static string GetProviderDisplayName(string providerName)
    {
        return providerName.ToLowerInvariant() switch
        {
            "tencent" => "腾讯云翻译",
            "baidu" => "百度云翻译",
            _ => providerName
        };
    }
}

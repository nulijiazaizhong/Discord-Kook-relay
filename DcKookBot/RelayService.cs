using Discord;
using Discord.WebSocket;
using Kook;
using Kook.WebSocket;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DiscordSocketMessage = Discord.WebSocket.SocketMessage;
using DiscordTextChannel = Discord.WebSocket.SocketTextChannel;
using DiscordSocketChannel = Discord.WebSocket.ISocketMessageChannel;
using KookSocketMessage = Kook.WebSocket.SocketMessage;
using KookGuildUser = Kook.WebSocket.SocketGuildUser;
using KookTextChannel = Kook.WebSocket.SocketTextChannel;

namespace DcKookBot;

public sealed class RelayService : IAsyncDisposable
{
    private static readonly HttpClient Http = new();
    private readonly RelayConfig _config;
    private readonly TranslationService _translation;
    private readonly DiscordSocketClient _discord;
    private readonly KookSocketClient _kook;
    private readonly ChannelMap _channelMap;

    public RelayService(RelayConfig config)
    {
        _config = config;
        _translation = new TranslationService(config);
        _discord = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = Discord.GatewayIntents.Guilds | Discord.GatewayIntents.GuildMessages | Discord.GatewayIntents.MessageContent,
            AlwaysDownloadUsers = false,
            LogGatewayIntentWarnings = false
        });

        _kook = new KookSocketClient(new KookSocketConfig());

        _channelMap = ChannelMap.Parse(config.DiscordChannelMap, config.KookChannelMap);
    }

    public async Task StartAsync(CancellationToken token)
    {
        _discord.Log += message =>
        {
            Console.WriteLine($"[Discord] {message}");
            return Task.CompletedTask;
        };

        _kook.Log += message =>
        {
            Console.WriteLine($"[Kook] {message}");
            return Task.CompletedTask;
        };

        _discord.MessageReceived += OnDiscordMessageAsync;
        _kook.MessageReceived += OnKookMessageAsync;

        await _discord.LoginAsync(Discord.TokenType.Bot, _config.DiscordToken);
        await _discord.StartAsync();

        await _kook.LoginAsync(Kook.TokenType.Bot, _config.KookToken);
        await _kook.StartAsync();

        Console.WriteLine("Relay started.");
    }

    public async Task WaitForShutdownAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }

        await StopAsync();
    }

    private async Task StopAsync()
    {
        await _discord.StopAsync();
        await _kook.StopAsync();
        await _discord.LogoutAsync();
        await _kook.LogoutAsync();
    }

    private async Task OnDiscordMessageAsync(DiscordSocketMessage message)
    {
        if (!_config.DiscordToKookEnabled)
        {
            return;
        }

        if (message.Author.IsBot && !_config.DiscordBotMessageToKookEnabled)
        {
            return;
        }

        if (message.Channel is not DiscordTextChannel channel)
        {
            return;
        }

        if (!_channelMap.TryGetKookChannel(channel.Id, out var kookChannelId))
        {
            return;
        }

        var target = _kook.GetChannel(kookChannelId) as KookTextChannel;
        if (target is null)
        {
            return;
        }

        var payload = MessageFormatter.FromDiscord(message, channel);
        if (_config.RelayEveryoneMentionEnabled && payload.MentionEveryone)
        {
            await target.SendTextAsync("(met)all(met)");
        }

        if (!string.IsNullOrWhiteSpace(payload.Content))
        {
            var translated = await _translation.TranslateAsync(
                payload.Content,
                _config.DiscordToKookSourceLang,
                _config.DiscordToKookTargetLang,
                CancellationToken.None);
            if (translated is not null)
            {
                var sourceMessageUrl = BuildDiscordMessageUrl(message, channel);
                var cardContent = MessageFormatter.BuildKookBilingualCardContent(payload.Content, translated, sourceMessageUrl, _config.TranslationBotName);
                var sent = await SendKookCardTextMessageAsync(target.Id.ToString(), cardContent, CancellationToken.None);
                if (!sent)
                {
                    await target.SendTextAsync(payload.Content);
                }
            }
            else
            {
                await target.SendTextAsync(payload.Content);
            }
        }

        if (payload.Attachments.Count > 0)
        {
            foreach (var attachment in payload.Attachments)
            {
                var sanitizedFileName = SanitizeFileName(attachment.FileName, "attachment");
                var tempPath = await DownloadToTempFileAsync(attachment.Url, sanitizedFileName, CancellationToken.None);
                try
                {
                    var fileSize = new FileInfo(tempPath).Length;
                    Console.WriteLine($"[Relay] Sending file to Kook. DiscordChannel={channel.Id}, KookChannel={target.Id}, Name={sanitizedFileName}, Size={fileSize} bytes");
                    var assetUrl = await UploadToKookAssetAsync(tempPath, sanitizedFileName, CancellationToken.None);
                    if (string.IsNullOrWhiteSpace(assetUrl))
                    {
                        Console.WriteLine("[Relay] Failed to upload asset to Kook. Fallback to URL.");
                        await target.SendTextAsync(attachment.Url);
                        continue;
                    }

                    var messageType = GetKookMessageTypeFromFileName(sanitizedFileName);
                    var sent = messageType == 4
                        ? await SendKookCardFileMessageAsync(target.Id.ToString(), sanitizedFileName, assetUrl, fileSize, CancellationToken.None)
                        : await SendKookMediaMessageAsync(target.Id.ToString(), messageType, assetUrl, CancellationToken.None);
                    if (!sent)
                    {
                        Console.WriteLine("[Relay] Failed to send Kook media message. Fallback to URL.");
                        await target.SendTextAsync(attachment.Url);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Relay] Failed to send file to Kook: {ex.Message}. Fallback to URL.");
                    await target.SendTextAsync(attachment.Url);
                }
                finally
                {
                    TryDeleteTempFile(tempPath);
                }
            }
        }
    }

    private async Task OnKookMessageAsync(KookSocketMessage message, KookGuildUser user, KookTextChannel channel)
    {
        if (!_config.KookToDiscordEnabled)
        {
            return;
        }

        if (user.IsBot == true)
        {
            return;
        }

        if (!_channelMap.TryGetDiscordChannel(channel.Id, out var discordChannelId))
        {
            return;
        }

        var target = _discord.GetChannel(discordChannelId) as DiscordSocketChannel;
        if (target is null)
        {
            return;
        }

        var payload = MessageFormatter.FromKook(message, user);
        if (_config.RelayEveryoneMentionEnabled && payload.MentionEveryone)
        {
            await target.SendMessageAsync("@everyone");
        }

        if (!string.IsNullOrWhiteSpace(payload.Content))
        {
            var translated = await _translation.TranslateAsync(
                payload.Content,
                _config.KookToDiscordSourceLang,
                _config.KookToDiscordTargetLang,
                CancellationToken.None);
            if (translated is not null)
            {
                var sourceMessageUrl = BuildKookMessageUrl(message, channel);
                var text = MessageFormatter.BuildDiscordBilingualText(payload.Content, translated, _config.TranslationBotName, sourceMessageUrl);
                await target.SendMessageAsync(text);
            }
            else
            {
                await target.SendMessageAsync(payload.Content);
            }
        }

        if (payload.Attachments.Count > 0)
        {
            foreach (var attachment in payload.Attachments)
            {
                var sanitizedFileName = SanitizeFileName(attachment.FileName, "attachment");
                var tempPath = await DownloadToTempFileAsync(attachment.Url, sanitizedFileName, CancellationToken.None);
                try
                {
                    Console.WriteLine($"[Relay] Kook->Discord file: Name={sanitizedFileName}, Url={attachment.Url}");
                    await using var stream = File.OpenRead(tempPath);
                    await target.SendFileAsync(stream, sanitizedFileName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Relay] Failed to send file to Discord: {ex.Message}. Fallback to URL.");
                    await target.SendMessageAsync(attachment.Url);
                }
                finally
                {
                    TryDeleteTempFile(tempPath);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _discord.Dispose();
        _kook.Dispose();
    }

    private static async Task<string> DownloadToTempFileAsync(string url, string fileName, CancellationToken token)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".dat";
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"relay-{Guid.NewGuid():N}{extension}");
        var bytes = await Http.GetByteArrayAsync(url, token);
        await File.WriteAllBytesAsync(tempFile, bytes, token);
        return tempFile;
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup errors
        }
    }

    private async Task<string?> UploadToKookAssetAsync(string filePath, string originalFileName, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.kookapp.cn/api/v3/asset/create");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _config.KookToken);

        await using var fileStream = File.OpenRead(filePath);
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        var safeOriginalName = string.IsNullOrWhiteSpace(originalFileName) ? string.Empty : Path.GetFileName(originalFileName);
        var uploadName = string.IsNullOrWhiteSpace(safeOriginalName) ? Path.GetFileName(filePath) : safeOriginalName;
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"file\"",
            FileName = $"\"{uploadName}\"",
            FileNameStar = uploadName
        };
        form.Add(fileContent, "file", uploadName);
        request.Content = form;

        using var response = await Http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[Relay] Kook asset upload failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var code = root.GetProperty("code").GetInt32();
            if (code != 0)
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown";
                Console.WriteLine($"[Relay] Kook asset upload failed: {message}");
                return null;
            }

            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("url", out var urlProp))
            {
                return urlProp.GetString();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] Failed to parse asset upload response: {ex.Message}");
        }

        return null;
    }

    private async Task<bool> SendKookMediaMessageAsync(string channelId, int type, string content, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.kookapp.cn/api/v3/message/create");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _config.KookToken);

        var payload = new
        {
            type,
            target_id = channelId,
            content
        };

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[Relay] Kook message send failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.GetProperty("code").GetInt32();
            if (code != 0)
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown";
                Console.WriteLine($"[Relay] Kook message send failed: {message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] Failed to parse message send response: {ex.Message}");
            return false;
        }

        return true;
    }

    private async Task<bool> SendKookCardFileMessageAsync(string channelId, string fileName, string assetUrl, long size, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.kookapp.cn/api/v3/message/create");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _config.KookToken);

        var card = new
        {
            type = "card",
            theme = "invisible",
            size = "lg",
            modules = new object[]
            {
                new
                {
                    type = "file",
                    title = fileName,
                    src = assetUrl,
                    external = false,
                    size,
                    canDownload = true,
                    elements = Array.Empty<object>()
                }
            }
        };

        var payload = new
        {
            type = 10,
            target_id = channelId,
            content = JsonSerializer.Serialize(new[] { card })
        };

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[Relay] Kook card send failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.GetProperty("code").GetInt32();
            if (code != 0)
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown";
                Console.WriteLine($"[Relay] Kook card send failed: {message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] Failed to parse card send response: {ex.Message}");
            return false;
        }

        return true;
    }

    private async Task<bool> SendKookCardTextMessageAsync(string channelId, string cardJson, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.kookapp.cn/api/v3/message/create");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _config.KookToken);

        var payload = new
        {
            type = 10,
            target_id = channelId,
            content = cardJson
        };

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[Relay] Kook text card send failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.GetProperty("code").GetInt32();
            if (code != 0)
            {
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown";
                Console.WriteLine($"[Relay] Kook text card send failed: {message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] Failed to parse text card send response: {ex.Message}");
            return false;
        }

        return true;
    }

    private static int GetKookMessageTypeFromFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? string.Empty;
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => 2,
            ".mp4" or ".mov" => 3,
            _ => 4
        };
    }

    private static string SanitizeFileName(string? fileName, string fallbackBaseName)
    {
        var safeName = string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = fallbackBaseName;
        }

        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(ch, '_');
        }

        var extension = Path.GetExtension(safeName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return safeName;
        }

        var baseName = Path.GetFileNameWithoutExtension(safeName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = fallbackBaseName;
        }

        return baseName + extension;
    }

    private static string BuildDiscordMessageUrl(DiscordSocketMessage message, DiscordTextChannel channel)
    {
        var guildId = channel.Guild?.Id.ToString() ?? "@me";
        return $"https://discord.com/channels/{guildId}/{channel.Id}/{message.Id}";
    }

    private static string BuildKookMessageUrl(KookSocketMessage message, KookTextChannel channel)
    {
        var guildId = channel.Guild?.Id.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(guildId))
        {
            return "https://www.kookapp.cn";
        }

        return $"https://www.kookapp.cn/app/channels/{guildId}/{channel.Id}";
    }
}

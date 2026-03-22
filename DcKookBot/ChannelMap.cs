namespace DcKookBot;

public sealed class ChannelMap
{
    private readonly Dictionary<ulong, ulong> _discordToKook;
    private readonly Dictionary<ulong, ulong> _kookToDiscord;

    private ChannelMap(Dictionary<ulong, ulong> discordToKook, Dictionary<ulong, ulong> kookToDiscord)
    {
        _discordToKook = discordToKook;
        _kookToDiscord = kookToDiscord;
    }

    public static ChannelMap Parse(string? discordToKook, string? kookToDiscord)
    {
        var forward = ParseMap(discordToKook);
        var reverse = ParseMap(kookToDiscord);

        if (forward.Count == 0 && reverse.Count == 0)
        {
            throw new InvalidOperationException("Channel mapping is required. Provide DISCORD_CHANNEL_MAP or KOOK_CHANNEL_MAP.");
        }

        if (reverse.Count == 0)
        {
            reverse = forward.ToDictionary(kv => kv.Value, kv => kv.Key);
        }

        if (forward.Count == 0)
        {
            forward = reverse.ToDictionary(kv => kv.Value, kv => kv.Key);
        }

        return new ChannelMap(forward, reverse);
    }

    public bool TryGetKookChannel(ulong discordChannelId, out ulong kookChannelId)
        => _discordToKook.TryGetValue(discordChannelId, out kookChannelId);

    public bool TryGetDiscordChannel(ulong kookChannelId, out ulong discordChannelId)
        => _kookToDiscord.TryGetValue(kookChannelId, out discordChannelId);

    private static Dictionary<ulong, ulong> ParseMap(string? value)
    {
        var map = new Dictionary<ulong, ulong>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return map;
        }

        var pairs = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Invalid channel map entry: {pair}");
            }

            if (!ulong.TryParse(parts[0], out var left) || !ulong.TryParse(parts[1], out var right))
            {
                throw new InvalidOperationException($"Invalid channel map entry: {pair}");
            }

            map[left] = right;
        }

        return map;
    }
}

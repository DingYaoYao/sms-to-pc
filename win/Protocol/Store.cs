using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmsLink.Protocol;

public sealed class Link
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("link_key")] public string LinkKey { get; set; } = "";
    [JsonPropertyName("android_id")] public string AndroidId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("paired_at")] public long PairedAt { get; set; }
    [JsonPropertyName("last_seen")] public long LastSeen { get; set; }
}

public sealed class SmsMessage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("ts")] public long Timestamp { get; set; }
    [JsonPropertyName("received_at")] public long ReceivedAt { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
}

public sealed class AppConfig
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("pc_name")] public string PcName { get; set; } = "";
    [JsonPropertyName("pair_code")] public string PairCode { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = 8789;
    [JsonPropertyName("discovery_port")] public int DiscoveryPort { get; set; } = 8788;
    [JsonPropertyName("auto_copy")] public bool AutoCopy { get; set; } = true;
    [JsonPropertyName("sound")] public bool Sound { get; set; } = true;
    [JsonPropertyName("start_with_windows")] public bool StartWithWindows { get; set; }
    [JsonPropertyName("edge_hide")] public bool EdgeHide { get; set; } = true;
    [JsonPropertyName("window_x")] public int WindowX { get; set; } = int.MinValue;
    [JsonPropertyName("window_y")] public int WindowY { get; set; } = int.MinValue;
    [JsonPropertyName("links")] public List<Link> Links { get; set; } = [];

    [JsonIgnore] public string DataDirectory { get; set; } = "";
    [JsonIgnore] private string ConfigPath => Path.Combine(DataDirectory, "config.json");
    [JsonIgnore] private string MessagesPath => Path.Combine(DataDirectory, "messages.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var configPath = Path.Combine(dataDirectory, "config.json");
        var messagesPath = Path.Combine(dataDirectory, "messages.json");
        AppConfig? config = null;

        if (File.Exists(configPath))
        {
            config = TryRead<AppConfig>(configPath);
        }

        // 从旧版（Node + 浏览器界面）迁移一次：配对码、已配对设备、历史短信都搬过来。
        if (config == null)
        {
            foreach (var legacyDirectory in LegacyDataDirectories())
            {
                var legacyConfig = Path.Combine(legacyDirectory, "config.json");
                if (!File.Exists(legacyConfig)) continue;

                config = TryRead<AppConfig>(legacyConfig);
                if (config == null) continue;

                var legacyMessages = Path.Combine(legacyDirectory, "messages.json");
                if (File.Exists(legacyMessages) && !File.Exists(messagesPath))
                {
                    try
                    {
                        File.Copy(legacyMessages, messagesPath);
                    }
                    catch
                    {
                        // 搬不过来也不影响配对本身
                    }
                }
                break;
            }
        }

        config ??= new AppConfig();
        config.DataDirectory = dataDirectory;

        if (string.IsNullOrWhiteSpace(config.DeviceId)) config.DeviceId = Crypto.NewDeviceId();
        if (string.IsNullOrWhiteSpace(config.PcName)) config.PcName = Environment.MachineName;
        // 配对码统一为 4 位：老配置里的 6 位码会被换成新的（已配对的手不受影响，用的是各自的长密钥）。
        if (config.PairCode.Length != 4 || !config.PairCode.All(char.IsDigit))
        {
            config.PairCode = Crypto.RandomPairCode();
        }
        config.Links ??= [];
        config.Save();
        return config;
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> LegacyDataDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "..", "pc", "data");
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "pc", "data");
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "pc", "data");
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var json = JsonSerializer.Serialize(this, WriteOptions);
        File.WriteAllText(ConfigPath, json);
    }

    public List<SmsMessage> LoadMessages()
    {
        if (!File.Exists(MessagesPath)) return [];
        try
        {
            var messages = JsonSerializer.Deserialize<List<SmsMessage>>(File.ReadAllText(MessagesPath)) ?? [];

            // 用当前规则重算一遍验证码：以前误判（比如把"回复10086"当成验证码）的老记录会被自动纠正。
            var changed = false;
            foreach (var message in messages)
            {
                var code = CodeExtractor.Extract(message.Body);
                if (code != message.Code)
                {
                    message.Code = code;
                    changed = true;
                }
            }
            if (changed) SaveMessages(messages);

            return messages;
        }
        catch
        {
            return [];
        }
    }

    public void SaveMessages(IEnumerable<SmsMessage> messages)
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(MessagesPath, JsonSerializer.Serialize(messages, WriteOptions));
    }

    public static IReadOnlyList<string> LocalIPv4()
    {
        var result = new List<(string Address, int Score)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var text = address.Address.ToString();
                var isPrivate = text.StartsWith("192.168.") || text.StartsWith("10.") ||
                                Regex172.IsMatch(text);
                result.Add((text, isPrivate ? 2 : 0));
            }
        }
        return result.OrderByDescending(item => item.Score)
            .Select(item => item.Address)
            .ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex Regex172 =
        new(@"^172\.(1[6-9]|2\d|3[01])\.", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsLoopback(EndPoint? remote)
    {
        if (remote is not IPEndPoint endpoint) return false;
        return IPAddress.IsLoopback(endpoint.Address);
    }
}

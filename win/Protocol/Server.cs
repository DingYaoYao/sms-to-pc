using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmsLink.Protocol;

/// <summary>
/// 极简 HTTP 服务：只暴露安卓端需要的三个接口。
/// 自己解析 HTTP 是因为 .NET 的 HttpListener 绑定非 localhost 前缀需要管理员权限或 urlacl，
/// 而我们要的只是"读一段 JSON、回一段 JSON"。
/// </summary>
public sealed class SmsServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private const int PairMaxFailures = 5;
    private const long PairLockMs = 60_000;
    private const long PairFreshnessMs = 120_000;

    private readonly AppConfig _config;
    private readonly List<SmsMessage> _messages;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (int Failures, long LockedUntil)> _pairFailures = [];
    private readonly Dictionary<string, long> _seenPairNonces = [];
    private readonly object _sync = new();
    private bool _disposed;

    public event Action<SmsMessage>? MessageReceived;
    public event Action? LinksChanged;
    public event Action<string>? Log;

    public SmsServer(AppConfig config, List<SmsMessage> messages)
    {
        _config = config;
        _messages = messages;
        _listener = new TcpListener(IPAddress.Any, config.Port);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        Log?.Invoke($"服务已启动，监听 0.0.0.0:{_config.Port}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                Log?.Invoke($"accept 失败：{error.Message}");
                continue;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 15_000;
                client.SendTimeout = 15_000;
                using var stream = client.GetStream();

                var buffer = new byte[8192];
                using var received = new MemoryStream();
                var headerEnd = -1;
                while (received.Length <= MaxHeaderBytes)
                {
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read <= 0) return;
                    received.Write(buffer, 0, read);
                    headerEnd = IndexOfHeaderEnd(received);
                    if (headerEnd >= 0) break;
                }
                if (headerEnd < 0) return;

                var headerText = Encoding.UTF8.GetString(received.GetBuffer(), 0, headerEnd);
                var lines = headerText.Split("\r\n");
                var requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2) return;
                var method = requestLine[0].ToUpperInvariant();
                var path = requestLine[1];

                var contentLength = 0;
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line[(colon + 1)..].Trim(), out contentLength);
                    }
                }
                if (contentLength > MaxBodyBytes) return;

                var bodyStart = headerEnd + 4;
                var alreadyHave = (int)received.Length - bodyStart;
                using var body = new MemoryStream();
                if (alreadyHave > 0) body.Write(received.GetBuffer(), bodyStart, alreadyHave);
                while (body.Length < contentLength)
                {
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read <= 0) break;
                    body.Write(buffer, 0, read);
                }
                var bodyText = Encoding.UTF8.GetString(
                    body.GetBuffer(), 0, Math.Min((int)body.Length, contentLength));

                var remote = client.Client.RemoteEndPoint;
                var (status, payload) = Dispatch(method, path, bodyText, remote);
                var bytes = Encoding.UTF8.GetBytes(payload);
                var head = $"HTTP/1.1 {status} {StatusText(status)}\r\n" +
                           "Content-Type: application/json; charset=utf-8\r\n" +
                           $"Content-Length: {bytes.Length}\r\n" +
                           "Cache-Control: no-store\r\n" +
                           "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(head), _cts.Token);
                await stream.WriteAsync(bytes, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch (Exception error)
            {
                Log?.Invoke($"处理请求失败：{error.Message}");
            }
        }
    }

    private static int IndexOfHeaderEnd(MemoryStream stream)
    {
        var buffer = stream.GetBuffer();
        var length = (int)stream.Length;
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                return i - 3;
        }
        return -1;
    }

    private (int Status, string Payload) Dispatch(string method, string path, string body, EndPoint? remote)
    {
        var ip = (remote as IPEndPoint)?.Address.ToString() ?? "unknown";

        if (method == "GET" && path == "/api/info")
        {
            return (200, Json(new JsonObject
            {
                ["ok"] = true,
                ["v"] = 1,
                ["name"] = _config.PcName,
                ["id"] = _config.DeviceId,
                ["port"] = _config.Port,
            }));
        }

        if (method == "POST" && path == "/api/pair")
        {
            return HandlePair(body, ip);
        }

        if (method == "POST" && path == "/api/sms")
        {
            return HandleSms(body, ip);
        }

        return (404, Json(new JsonObject { ["ok"] = false, ["error"] = "未知接口" }));
    }

    private (int, string) HandlePair(string body, string ip)
    {
        lock (_sync)
        {
            var locked = LockRemaining(ip);
            if (locked > 0)
            {
                return (429, Error($"尝试次数过多，请 {Math.Ceiling(locked / 1000.0)} 秒后重试"));
            }
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch
        {
            return (400, Error("请求体不是合法 JSON"));
        }
        if (node is not JsonObject json) return (400, Error("请求格式不正确"));

        var androidId = json["android_id"]?.GetValue<string>() ?? "";
        var name = json["name"]?.GetValue<string>() ?? "Android";
        var nonce = json["nonce"]?.GetValue<string>() ?? "";
        var timestampNode = json["ts"];
        var proofNode = json["proof"]?.GetValue<string>();
        if (string.IsNullOrEmpty(androidId) || androidId.Length > 64 || string.IsNullOrEmpty(nonce) ||
            timestampNode is null || proofNode is null)
        {
            return (400, Error("请求格式不正确"));
        }

        var timestamp = timestampNode.GetValue<long>();
        if (Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - timestamp) > PairFreshnessMs)
        {
            return (401, Error("手机与电脑时间相差过大，请校准时间后重试"));
        }

        byte[] proof;
        try
        {
            proof = Convert.FromBase64String(proofNode);
        }
        catch
        {
            return (400, Error("请求格式不正确"));
        }

        var nonceKey = androidId + "|" + nonce;
        lock (_sync)
        {
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            foreach (var stale in _seenPairNonces.Where(kv => now - kv.Value > PairFreshnessMs).Select(kv => kv.Key).ToList())
            {
                _seenPairNonces.Remove(stale);
            }
            if (_seenPairNonces.ContainsKey(nonceKey))
            {
                return (401, Error("重复的配对请求"));
            }
        }

        var pairKey = Crypto.DerivePairKey(_config.PairCode, _config.DeviceId);
        var expected = Crypto.PairProof(pairKey, androidId, timestamp, nonce);
        lock (_sync)
        {
            _seenPairNonces[nonceKey] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }

        if (!Crypto.FixedTimeEquals(expected, proof))
        {
            lock (_sync)
            {
                var entry = _pairFailures.TryGetValue(ip, out var current) ? current : (Failures: 0, LockedUntil: 0L);
                entry.Failures += 1;
                if (entry.Failures >= PairMaxFailures)
                {
                    entry.LockedUntil = DateTimeOffset.Now.ToUnixTimeMilliseconds() + PairLockMs;
                    entry.Failures = 0;
                    Log?.Invoke($"配对码连续错误，已暂时锁定 {ip}");
                }
                _pairFailures[ip] = entry;
            }
            Log?.Invoke($"配对失败：来自 {ip} 的配对码不正确");
            return (401, Error("配对码不正确"));
        }

        var linkKey = Crypto.RandomKey();
        var token = Crypto.RandomToken();
        lock (_sync)
        {
            _pairFailures.Remove(ip);
            _config.Links.RemoveAll(link => link.AndroidId == androidId);
            _config.Links.Add(new Link
            {
                Token = token,
                LinkKey = Convert.ToBase64String(linkKey),
                AndroidId = androidId,
                Name = name.Length > 32 ? name[..32] : name,
                PairedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                LastSeen = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            });
            _config.Save();
        }

        var secret = new JsonObject
        {
            ["token"] = token,
            ["link_key"] = Convert.ToBase64String(linkKey),
            ["pc_name"] = _config.PcName,
            ["port"] = _config.Port,
        };
        var envelope = Crypto.Encrypt(
            pairKey, Encoding.UTF8.GetBytes(secret.ToJsonString()), Crypto.PairAadPrefix + androidId);

        Log?.Invoke($"已配对：{name}（{ip}）");
        LinksChanged?.Invoke();
        return (200, Json(new JsonObject
        {
            ["ok"] = true,
            ["nonce"] = envelope.Nonce,
            ["ct"] = envelope.CipherText,
        }));
    }

    private (int, string) HandleSms(string body, string ip)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch
        {
            return (400, Error("请求体不是合法 JSON"));
        }
        if (node is not JsonObject json) return (400, Error("请求格式不正确"));

        var token = json["token"]?.GetValue<string>() ?? "";
        var nonce = json["nonce"]?.GetValue<string>();
        var cipherText = json["ct"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token) || nonce is null || cipherText is null)
        {
            return (400, Error("请求格式不正确"));
        }

        Link? link;
        lock (_sync)
        {
            link = _config.Links.FirstOrDefault(item => item.Token == token);
        }
        if (link == null) return (401, Error("未授权的设备"));

        byte[] key;
        try
        {
            key = Convert.FromBase64String(link.LinkKey);
        }
        catch
        {
            return (401, Error("未授权的设备"));
        }
        if (key.Length != 32) return (401, Error("未授权的设备"));

        JsonObject payload;
        try
        {
            var plain = Crypto.Decrypt(key, nonce, cipherText, Crypto.SmsAadPrefix + token);
            payload = JsonNode.Parse(Encoding.UTF8.GetString(plain)) as JsonObject
                      ?? throw new FormatException("payload 不是对象");
        }
        catch (Exception error)
        {
            Log?.Invoke($"丢弃一条来自 {ip} 的报文：{error.Message}");
            return (400, Error("数据校验失败"));
        }

        var id = payload["id"]?.GetValue<string>() ?? "";
        var from = payload["from"]?.GetValue<string>() ?? "未知";
        var text = payload["body"]?.GetValue<string>() ?? "";
        var timestamp = payload["ts"] is { } ts ? ts.GetValue<long>() : DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(text))
        {
            return (400, Error("缺少必要字段"));
        }

        SmsMessage message;
        lock (_sync)
        {
            if (_messages.Any(item => item.Id == id))
            {
                return (200, Json(new JsonObject { ["ok"] = true, ["duplicate"] = true }));
            }

            message = new SmsMessage
            {
                Id = id.Length > 120 ? id[..120] : id,
                From = from.Length > 64 ? from[..64] : from,
                Body = text.Length > 4000 ? text[..4000] : text,
                Timestamp = timestamp,
                ReceivedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Code = CodeExtractor.Extract(text),
                Source = link.Name,
            };
            _messages.Insert(0, message);
            if (_messages.Count > 500) _messages.RemoveRange(500, _messages.Count - 500);
            _config.SaveMessages(_messages);
            link.LastSeen = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            _config.Save();
        }

        Log?.Invoke($"收到短信：{message.From} → {Truncate(message.Body, 30)}");
        MessageReceived?.Invoke(message);
        return (200, Json(new JsonObject { ["ok"] = true, ["id"] = message.Id }));
    }

    private long LockRemaining(string ip)
    {
        if (!_pairFailures.TryGetValue(ip, out var entry)) return 0;
        var left = entry.LockedUntil - DateTimeOffset.Now.ToUnixTimeMilliseconds();
        return left > 0 ? left : 0;
    }

    private static string Json(JsonNode node) => node.ToJsonString();

    private static string Error(string message) =>
        Json(new JsonObject { ["ok"] = false, ["error"] = message });

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        429 => "Too Many Requests",
        _ => "Error",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts.Cancel();
            _listener.Stop();
        }
        catch
        {
            // 关闭时的异常忽略
        }
        _cts.Dispose();
    }
}

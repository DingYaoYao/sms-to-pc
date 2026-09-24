using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SmsLink.Protocol;

/// <summary>手机在同一局域网广播发现请求，这里单播回自己的信息，省去手工输 IP。</summary>
public sealed class Discovery : IDisposable
{
    private const string DiscoverRequest = "SMSLINK-DISCOVER-V1";
    private const string AnnouncePrefix = "SMSLINK-ANNOUNCE-V1 ";

    private readonly AppConfig _config;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public event Action<string>? Log;

    public Discovery(AppConfig config)
    {
        _config = config;
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, config.DiscoveryPort));
        _udp.EnableBroadcast = true;
    }

    public void Start()
    {
        _ = Task.Run(ReceiveLoopAsync);
        Log?.Invoke($"局域网发现服务已就绪（UDP {_config.DiscoveryPort}）");
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                var text = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (text != DiscoverRequest) continue;

                var announce = AnnouncePrefix + JsonSerializer.Serialize(new
                {
                    name = _config.PcName,
                    port = _config.Port,
                    id = _config.DeviceId,
                    v = 1,
                });
                var bytes = Encoding.UTF8.GetBytes(announce);
                await _udp.SendAsync(bytes, result.RemoteEndPoint, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                Log?.Invoke($"发现服务异常：{error.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts.Cancel();
            _udp.Close();
        }
        catch
        {
            // 忽略
        }
        _cts.Dispose();
    }
}

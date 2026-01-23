// BridgeClient：WinUI 侧 WebSocket 客户端，负责连接、收发协议 envelope，并将事件回调给 UI 层。
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace codex_bridge.Bridge;

public sealed class BridgeClient : IAsyncDisposable
{
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    public event EventHandler<BridgeEnvelope>? EnvelopeReceived;
    public event EventHandler<string>? ConnectionClosed;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri uri, string? bearerToken, CancellationToken cancellationToken)
    {
        await DisconnectAsync(CancellationToken.None);

        var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        }

        await socket.ConnectAsync(uri, cancellationToken);

        _socket = socket;
        _receiveCts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, _receiveCts.Token), CancellationToken.None);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        _socket = null;

        try
        {
            _receiveCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", cancellationToken);
            }
        }
        catch
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    public async Task SendCommandAsync(string name, object data, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("尚未连接到 Bridge Server。");
        }

        var envelope = new BridgeEnvelope
        {
            Type = "command",
            Name = name,
            Id = Guid.NewGuid().ToString("N"),
            Ts = DateTimeOffset.UtcNow,
            Data = JsonSerializer.SerializeToElement(data, BridgeJson.SerializerOptions),
        };

        var json = JsonSerializer.Serialize(envelope, BridgeJson.SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult? result;

                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ConnectionClosed?.Invoke(this, "服务器已关闭连接。");
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(ms.ToArray());
                TryRaiseEnvelope(text);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ConnectionClosed?.Invoke(this, ex.Message);
        }
    }

    private void TryRaiseEnvelope(string text)
    {
        BridgeEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<BridgeEnvelope>(text, BridgeJson.SerializerOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (envelope is null)
        {
            return;
        }

        EnvelopeReceived?.Invoke(this, envelope);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None);
        _receiveCts?.Dispose();
    }
}

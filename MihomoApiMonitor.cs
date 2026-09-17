using System.Net.Http;
using System.Net.WebSockets;

namespace MihomoTray;

internal sealed class MihomoApiMonitor
{
    private const int ConnectTimeoutMs = 2000;
    private const int ReconnectIntervalMs = 1000;
    private const int MaxReconnectIntervalMs = 30000;
    private const double ReconnectDecay = 1.5;

    private readonly Action<bool> _onLiveChanged;
    private CancellationTokenSource? _cts;
    private TrafficConnection? _connection;
    private int _reconnectAttempts;
    private bool _live;

    public MihomoApiMonitor(Action<bool> onLiveChanged)
    {
        _onLiveChanged = onLiveChanged;
    }

    public void Start()
    {
        Stop();
        _reconnectAttempts = 0;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void RetryNowIfDisconnected()
    {
        if (_live)
        {
            return;
        }

        Start();
    }

    public void Stop()
    {
        SetLive(false);
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        AbortSocket();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool wasLive = false;
            try
            {
                wasLive = await ConnectAndListenAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }
            finally
            {
                AbortSocket();
                if (wasLive)
                {
                    SetLive(false);
                }
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            int delayMs = (int)Math.Min(
                ReconnectIntervalMs * Math.Pow(ReconnectDecay, _reconnectAttempts),
                MaxReconnectIntervalMs);
            _reconnectAttempts++;
            try
            {
                await Task.Delay(delayMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> ConnectAndListenAsync(CancellationToken token)
    {
        if (!MihomoService.TryGetTrafficWebSocketUri(out Uri uri, out string secret))
        {
            return false;
        }

        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs)
        };
        HttpMessageInvoker invoker = new(handler, disposeHandler: true);
        ClientWebSocket socket = new();
        TrafficConnection connection = new(socket, invoker);
        _connection = connection;
        if (secret.Length > 0)
        {
            try
            {
                socket.Options.SetRequestHeader("Authorization", "Bearer " + secret);
            }
            catch
            {
            }
        }

        using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectCts.CancelAfter(ConnectTimeoutMs);
        await socket.ConnectAsync(uri, invoker, connectCts.Token);
        _reconnectAttempts = 0;
        SetLive(true);

        byte[] buffer = new byte[8192];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
        }

        return true;
    }

    private void SetLive(bool live)
    {
        if (_live == live)
        {
            return;
        }

        _live = live;
        try
        {
            _onLiveChanged(live);
        }
        catch
        {
        }
    }

    private void AbortSocket()
    {
        TrafficConnection? connection = Interlocked.Exchange(ref _connection, null);
        connection?.Dispose();
    }

    private sealed class TrafficConnection : IDisposable
    {
        public TrafficConnection(ClientWebSocket socket, HttpMessageInvoker invoker)
        {
            Socket = socket;
            Invoker = invoker;
        }

        public ClientWebSocket Socket { get; }
        public HttpMessageInvoker Invoker { get; }

        public void Dispose()
        {
            try
            {
                Socket.Abort();
            }
            catch
            {
            }

            try
            {
                Socket.Dispose();
            }
            catch
            {
            }

            try
            {
                Invoker.Dispose();
            }
            catch
            {
            }
        }
    }
}

using System.Buffers;
using System.Net.Sockets;
using NexBot.Protocol;

namespace NexBot.Network;

/// <summary>
/// One Silkroad TCP connection: a socket, its <see cref="SroSecurity"/>, a receive loop and a send loop.
/// </summary>
/// <remarks>
/// Used for both sides of the proxy: <see cref="ConnectAsync"/> creates a client-role connection to a real server,
/// <see cref="Accept"/> wraps a game client that connected to us (server role, the handshake is started here).
/// Received packets are delivered one at a time on the receive loop through <see cref="PacketReceived"/>.
/// </remarks>
public sealed class SroConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly SroSecurity _security = new();
    private readonly SemaphoreSlim _sendSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task _receiveTask = Task.CompletedTask;
    private Task _sendTask = Task.CompletedTask;
    private int _closed;

    private SroConnection(Socket socket, string name)
    {
        _socket = socket;
        _socket.NoDelay = true;
        Name = name;
    }

    /// <summary>Label used in logs ("Gateway", "Agent", "Client").</summary>
    public string Name { get; }

    public bool IsConnected => Volatile.Read(ref _closed) == 0;

    /// <summary>Raised on the receive loop for every packet (handshake packets are handled internally).</summary>
    public event Func<SroConnection, Packet, ValueTask>? PacketReceived;

    /// <summary>Raised once when the connection closes, with the reason (null = closed by us).</summary>
    public event Action<SroConnection, Exception?>? Disconnected;

    /// <summary>Connects to a real gateway / agent server (client role).</summary>
    public static async Task<SroConnection> ConnectAsync(string host, ushort port, string name, TimeSpan timeout, CancellationToken ct = default)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await socket.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new SroConnection(socket, name);
    }

    /// <summary>Wraps a game client that connected to the proxy (server role).</summary>
    public static SroConnection Accept(Socket socket, string name)
    {
        var connection = new SroConnection(socket, name);
        connection._security.GenerateServerHandshake(blowfish: true, securityBytes: true, handshake: true);
        return connection;
    }

    /// <summary>Starts the loops. Subscribe to the events first.</summary>
    public void Start()
    {
        _receiveTask = Task.Run(ReceiveLoopAsync);
        _sendTask = Task.Run(SendLoopAsync);
        _sendSignal.Release(); // flush a queued server handshake
    }

    /// <summary>Queues a packet.</summary>
    public void Send(Packet packet)
    {
        if (!IsConnected)
            return;
        _security.Send(packet);
        _sendSignal.Release();
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var packets = new List<Packet>(16);
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int read = await _socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    error = new SocketException((int)SocketError.ConnectionReset);
                    break;
                }

                packets.Clear();
                if (_security.Receive(buffer.AsSpan(0, read), packets))
                    _sendSignal.Release();

                foreach (var packet in packets)
                {
                    if (PacketReceived is { } handler)
                        await handler(this, packet).ConfigureAwait(false);
                }

                if (_security.HasPendingOutgoing)
                    _sendSignal.Release();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        Close(error);
    }

    private async Task SendLoopAsync()
    {
        var writer = new ArrayBufferWriter<byte>(8192);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _sendSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
                writer.ResetWrittenCount();
                if (_security.WriteOutgoing(writer) == 0)
                    continue;

                var data = writer.WrittenMemory;
                while (!data.IsEmpty)
                {
                    int sent = await _socket.SendAsync(data, SocketFlags.None, _cts.Token).ConfigureAwait(false);
                    data = data[sent..];
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Close(ex);
        }
    }

    /// <summary>Closes the socket. <see cref="Disconnected"/> is raised once.</summary>
    public void Close(Exception? reason = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        try { _socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        _socket.Close();
        _cts.Cancel();
        Disconnected?.Invoke(this, reason);
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try
        {
            await Task.WhenAll(_receiveTask, _sendTask).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // loops already reported their errors through Disconnected
        }
        _cts.Dispose();
        _sendSignal.Dispose();
        _socket.Dispose();
    }
}

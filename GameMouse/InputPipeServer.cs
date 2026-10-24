using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;

namespace VirtualDesktopRDP.GameMouse;

/// <summary>
/// 主实例一侧的命名管道服务端。按 8 字节消息发送相对鼠标增量。
/// </summary>
internal sealed class InputPipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Channel<MouseDelta> _channel = Channel.CreateBounded<MouseDelta>(
        new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private volatile NamedPipeServerStream? _stream;

    public InputPipeServer(string pipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsConnected => _stream?.IsConnected == true;

    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout)
    {
        try
        {
            _stream = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await _stream.WaitForConnectionAsync(_cancellationTokenSource.Token)
                .WaitAsync(timeout, _cancellationTokenSource.Token)
                .ConfigureAwait(false);
            _ = WriteLoopAsync(_cancellationTokenSource.Token);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                              or TimeoutException
                                              or OperationCanceledException
                                              or ObjectDisposedException
                                              or InvalidOperationException
                                              or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Enqueue(int deltaX, int deltaY)
    {
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        _ = _channel.Writer.TryWrite(new MouseDelta(deltaX, deltaY));
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[InputAgentProtocol.DeltaSize];
        try
        {
            await foreach (var delta in _channel.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (_stream is null || !_stream.IsConnected)
                {
                    return;
                }

                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), delta.X);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), delta.Y);
                await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or OperationCanceledException
                                              or ObjectDisposedException)
        {
            // 代理进程退出或主程序关闭。
            try
            {
                _stream?.Dispose();
            }
            catch (IOException)
            {
            }

            _stream = null;
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cancellationTokenSource.Cancel();
        try
        {
            _stream?.Dispose();
        }
        catch (IOException)
        {
        }

        _stream = null;
        _cancellationTokenSource.Dispose();
    }

    private readonly record struct MouseDelta(int X, int Y);
}

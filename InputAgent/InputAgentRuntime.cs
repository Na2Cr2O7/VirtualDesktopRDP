using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using VirtualDesktopRDP.GameMouse;

namespace VirtualDesktopRDP.InputAgent;

/// <summary>
/// 运行在 Child Session 内的轻量输入代理：从命名管道读取相对鼠标增量并调用 SendInput。
/// 不创建窗口，也不依赖 BetterGI。
/// </summary>
internal static class InputAgentRuntime
{
    private const uint InputMouse = 0;
    private const uint MouseEventMove = 0x0001;

    public static async Task RunAsync(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.In,
                PipeOptions.Asynchronous);
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            var buffer = new byte[InputAgentProtocol.DeltaSize];
            while (true)
            {
                await client.ReadExactlyAsync(buffer).ConfigureAwait(false);
                var deltaX = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, 4));
                var deltaY = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4, 4));
                if (deltaX != 0 || deltaY != 0)
                {
                    SendRelativeMouseMove(deltaX, deltaY);
                }
            }
        }
        catch (Exception)
        {
            // 根实例退出、管道断开或启动失败时，代理进程直接结束。
        }
    }

    private static void SendRelativeMouseMove(int deltaX, int deltaY)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    DeltaX = deltaX,
                    DeltaY = deltaY,
                    Flags = MouseEventMove
                }
            }
        };

        _ = SendInput(1, ref input, (uint)Marshal.SizeOf<Input>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        ref Input inputs,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int DeltaX;
        public int DeltaY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}

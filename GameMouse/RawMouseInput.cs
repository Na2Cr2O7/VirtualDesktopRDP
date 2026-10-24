using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VirtualDesktopRDP.GameMouse;

/// <summary>
/// 通过 Raw Input 采集本机鼠标的相对位移。只注册鼠标，不注册键盘，
/// 避免覆盖 RDP ActiveX 的键盘 Raw Input 目标。
/// </summary>
internal sealed class RawMouseInput : IDisposable
{
    private const ushort GenericDesktopUsagePage = 0x01;
    private const ushort MouseUsage = 0x02;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const uint RidInput = 0x10000003;
    private const int WmInput = 0x00FF;
    private const int MouseMoveAbsolute = 0x0001;
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private bool _registered;

    public event Action<int, int>? Moved;

    public void Start()
    {
        if (_source is not null)
        {
            return;
        }

        var source = new HwndSource(new HwndSourceParameters("VirtualDesktopRDP RawInput")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0
        });
        source.AddHook(WindowProc);

        try
        {
            Register(source.Handle, RidevInputSink);
            _registered = true;
            _source = source;
        }
        catch
        {
            source.RemoveHook(WindowProc);
            source.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        if (_source is null)
        {
            return;
        }

        if (_registered)
        {
            try
            {
                Register(IntPtr.Zero, RidevRemove);
            }
            catch (Win32Exception)
            {
                // 窗口销毁期间注销失败不影响后续清理。
            }

            _registered = false;
        }

        _source.RemoveHook(WindowProc);
        _source.Dispose();
        _source = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private static void Register(IntPtr target, uint flags)
    {
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = GenericDesktopUsagePage,
                Usage = MouseUsage,
                Flags = flags,
                Target = target
            }
        };

        if (!RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "注册 Raw Input 鼠标设备失败");
        }
    }

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmInput)
        {
            try
            {
                ProcessRawInput(lParam);
            }
            catch (Exception exception) when (exception is Win32Exception
                                                  or InvalidOperationException
                                                  or ArgumentException)
            {
                Debug.WriteLine(exception.Message);
            }
        }

        // 保持 handled = false，让默认窗口过程完成 WM_INPUT 的必要清理。
        handled = false;
        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr rawInputHandle)
    {
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        if (GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize)
            == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            var readSize = size;
            if (GetRawInputData(rawInputHandle, RidInput, buffer, ref readSize, headerSize)
                == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != 0)
            {
                return;
            }

            var mouse = Marshal.PtrToStructure<RawMouseData>(
                IntPtr.Add(buffer, Marshal.SizeOf<RawInputHeader>()));
            if ((mouse.Flags & MouseMoveAbsolute) != 0)
            {
                return;
            }

            if (mouse.LastX != 0 || mouse.LastY != 0)
            {
                Moved?.Invoke(mouse.LastX, mouse.LastY);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint deviceCount,
        uint deviceSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint headerSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouseData
    {
        public ushort Flags;
        public ushort ButtonFlags;
        public ushort ButtonData;
        public ushort Reserved;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }
}

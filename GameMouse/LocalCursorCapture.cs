using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace VirtualDesktopRDP.GameMouse;

/// <summary>
/// 在本地会话中约束并隐藏鼠标。所有调用应来自同一个 UI 线程，
/// 以便成对恢复 ShowCursor 的显示计数。
/// </summary>
internal sealed class LocalCursorCapture : IDisposable
{
    private const int MaxCursorHideAttempts = 64;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private NativeRect _previousClipRect;
    private Rectangle _appliedBounds;
    private bool _hasPreviousClipRect;
    private bool _isCaptureSessionActive;
    private bool _isRestrictionApplied;
    private bool _isDisposed;
    private int _cursorHideCallCount;

    internal void Capture(Rectangle bounds)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                bounds,
                "鼠标限制区域必须具有有效宽度和高度。");
        }

        if (!_isCaptureSessionActive)
        {
            var clipRectRead = GetClipCursor(out _previousClipRect);
            _hasPreviousClipRect = clipRectRead && !IsVirtualDesktopBounds(_previousClipRect);
            _isCaptureSessionActive = true;
        }

        if (_isRestrictionApplied && _appliedBounds == bounds)
        {
            return;
        }

        var clipRect = NativeRect.FromRectangle(bounds);
        if (!ClipCursor(ref clipRect))
        {
            var exception = new Win32Exception(
                Marshal.GetLastWin32Error(),
                "限制本地鼠标到虚拟桌面窗口失败");
            if (!_isRestrictionApplied)
            {
                Release();
            }

            throw exception;
        }

        _appliedBounds = bounds;
        if (!_isRestrictionApplied)
        {
            HideCursor();
            _isRestrictionApplied = true;
        }
    }

    internal void ReleaseTemporarily()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_isCaptureSessionActive || !_isRestrictionApplied)
        {
            return;
        }

        if (!ReleaseClipCursor(IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "临时解除本地鼠标限制失败");
        }

        var centerX = _appliedBounds.Left + _appliedBounds.Width / 2;
        var centerY = _appliedBounds.Top + _appliedBounds.Height / 2;
        _ = SetCursorPos(centerX, centerY);
        RestoreCursorVisibility();
        _isRestrictionApplied = false;
        _appliedBounds = Rectangle.Empty;
    }

    internal void Release()
    {
        if (!_isCaptureSessionActive)
        {
            return;
        }

        var restored = _hasPreviousClipRect
            ? ClipCursor(ref _previousClipRect)
            : ReleaseClipCursor(IntPtr.Zero);
        if (!restored)
        {
            // 窗口销毁或桌面切换时可能失败，下一帧不再依赖旧的裁剪区域。
        }

        RestoreCursorVisibility();
        _hasPreviousClipRect = false;
        _isCaptureSessionActive = false;
        _isRestrictionApplied = false;
        _appliedBounds = Rectangle.Empty;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Release();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void HideCursor()
    {
        var displayCount = 0;
        do
        {
            displayCount = ShowCursor(false);
            _cursorHideCallCount++;
        }
        while (displayCount >= 0 && _cursorHideCallCount < MaxCursorHideAttempts);
    }

    private void RestoreCursorVisibility()
    {
        while (_cursorHideCallCount > 0)
        {
            _ = ShowCursor(true);
            _cursorHideCallCount--;
        }
    }

    private static bool IsVirtualDesktopBounds(NativeRect rect)
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        return rect.Left == left
               && rect.Top == top
               && rect.Right == left + width
               && rect.Bottom == top + height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        internal static NativeRect FromRectangle(Rectangle rectangle)
        {
            return new NativeRect
            {
                Left = rectangle.Left,
                Top = rectangle.Top,
                Right = rectangle.Right,
                Bottom = rectangle.Bottom
            };
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseClipCursor(IntPtr rect);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(
        [MarshalAs(UnmanagedType.Bool)] bool show);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}

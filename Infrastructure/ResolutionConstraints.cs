namespace VirtualDesktopRDP.Infrastructure;

/// <summary>
/// 虚拟桌面分辨率约束。下限为 800×600；超过 2560×1440 时界面会给出警告。
/// RDP ActiveX 自身可接受的最大尺寸为 8192。
/// </summary>
public static class ResolutionConstraints
{
    public const int DefaultWidth = 1920;
    public const int DefaultHeight = 1080;
    public const int MinWidth = 800;
    public const int MinHeight = 600;
    public const int WarningWidth = 2560;
    public const int WarningHeight = 1440;
    public const int MaxWidth = 8192;
    public const int MaxHeight = 8192;

    public static bool IsBelowMinimum(int width, int height)
    {
        return width < MinWidth || height < MinHeight;
    }

    public static bool IsAboveWarningThreshold(int width, int height)
    {
        return width > WarningWidth || height > WarningHeight;
    }

    public static bool IsAboveMaximum(int width, int height)
    {
        return width > MaxWidth || height > MaxHeight;
    }

    public static (int Width, int Height) Normalize(int width, int height)
    {
        var normalizedWidth = width is >= MinWidth and <= MaxWidth
            ? width
            : DefaultWidth;
        var normalizedHeight = height is >= MinHeight and <= MaxHeight
            ? height
            : DefaultHeight;
        return (normalizedWidth, normalizedHeight);
    }
}

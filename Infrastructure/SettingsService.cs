using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VirtualDesktopRDP.Infrastructure;

/// <summary>
/// 负责读取和保存 <see cref="AppSettings"/>。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();

    public SettingsService()
    {
        SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VirtualDesktopRDP");
        SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
        Current = Load();
    }

    public string SettingsDirectory { get; }

    public string SettingsPath { get; }

    public AppSettings Current { get; private set; }

    /// <summary>
    /// 用新配置整体替换当前配置并落盘。
    /// </summary>
    public void Update(AppSettings settings)
    {
        lock (_syncRoot)
        {
            Current = settings;
            SaveCore();
        }
    }

    /// <summary>
    /// 保存当前配置。
    /// </summary>
    public void Save()
    {
        lock (_syncRoot)
        {
            SaveCore();
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                           ?? new AppSettings();
            settings.AutoStartPrograms ??= [];
            return settings;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException)
        {
            Trace.WriteLine($"读取配置失败：{exception.GetBaseException().Message}");
            return new AppSettings();
        }
    }

    private void SaveCore()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(Current, SerializerOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException)
        {
            Trace.WriteLine($"保存配置失败：{exception.GetBaseException().Message}");
        }
    }
}

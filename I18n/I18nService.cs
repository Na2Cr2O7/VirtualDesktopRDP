using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace VirtualDesktopRDP.I18n;

/// <summary>
/// 轻量界面多语言服务。简体中文原文同时作为资源键，因此 zh-Hans 不需要额外翻译文件。
/// 其他语言从 I18n/&lt;language&gt;.json 读取“中文原文 -> 译文”的映射。
/// </summary>
public sealed class I18nService : INotifyPropertyChanged
{
    public const string DefaultLanguage = "zh-Hans";
    public const string EnglishLanguage = "en-US";

    private static readonly Lazy<I18nService> LazyInstance = new(() => new I18nService());
    private readonly string _i18nDirectory = Path.Combine(AppContext.BaseDirectory, "I18n");
    private IReadOnlyDictionary<string, string> _translations =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private long _revision;

    private I18nService()
    {
    }

    public static I18nService Instance => LazyInstance.Value;

    public static IReadOnlyList<LanguageOption> AvailableLanguages { get; } =
    [
        new(DefaultLanguage, "简体中文"),
        new(EnglishLanguage, "English")
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = DefaultLanguage;

    /// <summary>TExtension 绑定此版本号，语言切换后自动重新取值。</summary>
    public long Revision => _revision;

    public static string T(string key)
    {
        return Instance.Translate(key);
    }

    public static string F(string key, params object?[] args)
    {
        return string.Format(
            CultureInfo.CurrentCulture,
            Instance.Translate(key),
            args);
    }

    public void ChangeLanguage(string? language)
    {
        language = NormalizeLanguage(language);
        CurrentLanguage = language;
        _translations = LoadTranslations(language);

        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // 自定义语言文件也允许使用；找不到 Culture 时不影响界面翻译。
        }

        _revision++;
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(CurrentLanguage));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Translate(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key;
        }

        return _translations.TryGetValue(key, out var translation)
               && !string.IsNullOrWhiteSpace(translation)
            ? translation
            : key;
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                .Equals("en", StringComparison.OrdinalIgnoreCase)
                ? EnglishLanguage
                : DefaultLanguage;
        }

        if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return EnglishLanguage;
        }

        if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultLanguage;
        }

        var known = AvailableLanguages.FirstOrDefault(
            option => string.Equals(
                option.Code,
                language,
                StringComparison.OrdinalIgnoreCase));
        return known?.Code ?? DefaultLanguage;
    }

    private IReadOnlyDictionary<string, string> LoadTranslations(string language)
    {
        if (string.Equals(language, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var filePath = Path.Combine(_i18nDirectory, $"{language}.json");
            if (!File.Exists(filePath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(filePath);
            var dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return dictionary is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(dictionary, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException)
        {
            // 翻译文件损坏时退回中文原文，不影响程序启动。
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>语言选择项。显示名使用该语言自身的名称，不翻译。</summary>
public sealed record LanguageOption(string Code, string DisplayName);

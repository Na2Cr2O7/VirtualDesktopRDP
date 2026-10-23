using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace VirtualDesktopRDP.I18n;

/// <summary>
/// XAML 翻译标记：Text="{i18n:T 启动}"。中文原文同时作为资源键。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    private static readonly IValueConverter TranslationConverter =
        new RevisionToTranslationConverter();

    public TExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (IsInDesignMode(serviceProvider))
        {
            return Key;
        }

        var binding = new System.Windows.Data.Binding(nameof(I18nService.Revision))
        {
            Source = I18nService.Instance,
            Mode = BindingMode.OneWay,
            Converter = TranslationConverter,
            ConverterParameter = Key
        };
        return binding.ProvideValue(serviceProvider);
    }

    private static bool IsInDesignMode(IServiceProvider serviceProvider)
    {
        if ((bool)DesignerProperties.IsInDesignModeProperty
            .GetMetadata(typeof(DependencyObject)).DefaultValue)
        {
            return true;
        }

        var provideValueTarget =
            serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        return provideValueTarget?.TargetObject is DependencyObject dependencyObject
               && DesignerProperties.GetIsInDesignMode(dependencyObject);
    }

    private sealed class RevisionToTranslationConverter : IValueConverter
    {
        public object Convert(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            return I18nService.Instance.Translate(parameter as string ?? string.Empty);
        }

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}

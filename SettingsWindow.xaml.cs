using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using VirtualDesktopRDP.I18n;
using VirtualDesktopRDP.Infrastructure;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace VirtualDesktopRDP;

/// <summary>
/// 编辑“新建子会话后自动启动”的程序列表。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<string> _programs;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = settings;
        _programs = new ObservableCollection<string>(settings.AutoStartPrograms);
        DataContext = this;

        var language = string.IsNullOrWhiteSpace(settings.Language)
            ? I18nService.Instance.CurrentLanguage
            : settings.Language;
        ChineseLanguageRadio.IsChecked =
            !language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        EnglishLanguageRadio.IsChecked =
            language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    }

    public AppSettings Settings { get; }

    public ObservableCollection<string> Programs => _programs;

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Win32OpenFileDialog
        {
            Title = I18nService.T("选择要自动启动的程序"),
            Filter = I18nService.T("Windows 程序 (*.exe)|*.exe"),
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var fileName in dialog.FileNames)
        {
            var fullPath = Path.GetFullPath(fileName);
            if (_programs.Any(path => string.Equals(
                    path,
                    fullPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _programs.Add(fullPath);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ProgramList.SelectedIndex >= 0)
        {
            _programs.RemoveAt(ProgramList.SelectedIndex);
        }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e)
    {
        var index = ProgramList.SelectedIndex;
        if (index <= 0)
        {
            return;
        }

        _programs.Move(index, index - 1);
        ProgramList.SelectedIndex = index - 1;
    }

    private void OnMoveDownClick(object sender, RoutedEventArgs e)
    {
        var index = ProgramList.SelectedIndex;
        if (index < 0 || index >= _programs.Count - 1)
        {
            return;
        }

        _programs.Move(index, index + 1);
        ProgramList.SelectedIndex = index + 1;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Settings.AutoStartPrograms = [.. _programs];
        Settings.Language = EnglishLanguageRadio.IsChecked == true
            ? I18nService.EnglishLanguage
            : I18nService.DefaultLanguage;
        DialogResult = true;
    }
}

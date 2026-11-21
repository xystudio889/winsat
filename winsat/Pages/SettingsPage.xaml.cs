using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using winsat.helpers;
using winsat.widgets;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace winsat.pages;

/// <summary>
/// 设置页：软件主题、导航栏位置以及“关于”信息。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private const string RepositoryUrl = "https://github.com/xystudio889/winsat";
    private const string NewIssueUrl = "https://github.com/xystudio889/winsat/issues/new";

    /// <summary>初始化期间为 true，用于阻止 ComboBox 的 SelectionChanged 回写设置。</summary>
    private bool _isInitializing;

    public SettingsPage()
    {
        InitializeComponent();
        InitializeSettings();
    }

    public string Version
    {
        get
        {
            return ProcessInfoHelper.GetVersion() is Version version
                ? string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision)
                : string.Empty;
        }
    }

    /// <summary>根据已保存的设置初始化各控件的选中状态。</summary>
    private void InitializeSettings()
    {
        _isInitializing = true;
        try
        {
            themeMode.SelectedIndex = AppSettings.Theme switch
            {
                AppSettings.LightTheme => 0,
                AppSettings.DarkTheme => 1,
                _ => 2,
            };

            navigationLocation.SelectedIndex =
                AppSettings.NavigationLocation == AppSettings.TopNavigation ? 1 : 0;

            languageSelector.SelectedIndex = AppSettings.Language switch
            {
                AppSettings.ChineseLanguage => 1,
                AppSettings.EnglishLanguage => 2,
                AppSettings.SystemLanguage => 0,
                _ => 0
            };

            backgroundSelector.SelectedIndex = AppSettings.Background switch
            {
                AppSettings.Mica => 0,
                AppSettings.MicaAlt => 1,
                AppSettings.Arcylic => 2,
                AppSettings.ArcylicThin => 3,
                AppSettings.None => 4,
                _ => 0
            };

            soundToggle.IsOn = AppSettings.Sound;
            spatialSoundToggle.IsOn = AppSettings.SpatialSound;

            SpatialAudioCard.IsEnabled = soundToggle.IsOn;
            ElementSoundPlayer.State = soundToggle.IsOn ? ElementSoundPlayerState.On: ElementSoundPlayerState.Off;
            ElementSoundPlayer.SpatialAudioMode = spatialSoundToggle.IsOn ? ElementSpatialAudioMode.On : ElementSpatialAudioMode.Off;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    // ---- 软件主题 ----
    private void ThemeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (themeMode.SelectedItem is ComboBoxItem { Tag: string theme })
        {
            AppSettings.Theme = theme;
            App.MainWindow?.ApplyTheme(theme);
        }
    }

    // ---- 导航栏位置 ----
    private void NavigationLocation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (navigationLocation.SelectedItem is ComboBoxItem { Tag: string location })
        {
            AppSettings.NavigationLocation = location;
            App.MainWindow?.SetNavigationLocation(location);
        }
    }

    // ---- 显示语言 ----
    private async void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (languageSelector.SelectedItem is not ComboBoxItem { Tag: string language }
            || AppSettings.Language == language)
        {
            return;
        }

        AppSettings.Language = language;
        await PromptRestartAsync();
    }

    // ---- 背景材质 ----
    private void Background_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (backgroundSelector.SelectedItem is ComboBoxItem { Tag: string background })
        {
            AppSettings.Background = background;
            App.MainWindow?.ApplyBackground(background);
        }
    }

    // ---- 音效 ----
    private async void SoundToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ElementSoundPlayer.State = soundToggle.IsOn ? ElementSoundPlayerState.On : ElementSoundPlayerState.Off;
        SpatialAudioCard.IsEnabled = soundToggle.IsOn;
        AppSettings.Sound = soundToggle.IsOn;
    }

    private async void SpatialSoundToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        AppSettings.SpatialSound = spatialSoundToggle.IsOn;
        ElementSoundPlayer.SpatialAudioMode = spatialSoundToggle.IsOn ? ElementSpatialAudioMode.On : ElementSpatialAudioMode.Off;
    }

    /// <summary>语言需要重启后才会生效，询问用户是否立即重启。</summary>
    private async Task PromptRestartAsync()
    {
        ContentDialogResult result = await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loader.GetString("RestartRequiredTitle"),
            Content = Loader.GetString("RestartRequiredText"),
            PrimaryButtonText = Loader.GetString("RestartNow"),
            CloseButtonText = Loader.GetString("RestartLater"),
            DefaultButton = ContentDialogButton.Primary,
            // 对话框不继承窗口内容主题，需显式跟随软件主题
            RequestedTheme = AppTheme.CurrentElementTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"]
        }.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            await AppLanguage.RestartAsync();
        }
    }

    // ---- 克隆仓库：复制克隆命令 ----
    private async void CloneRepoCard_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText($"git clone {RepositoryUrl}");
        Clipboard.SetContent(package);

        await ShowMessageAsync(Loader.GetString("CopiedTitle"), Loader.GetString("CloneCommandCopied"));
    }

    // ---- 提交 Bug：打开新建 Issue 页面 ----
    private async void BugRequestCard_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri(NewIssueUrl));
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        ContentDialog dialog = new ContentDialog()
        {
            XamlRoot = this.Content.XamlRoot,
            Title = Loader.GetString("ExportWizardTitle"),
            CloseButtonText = Loader.GetString("Close"),
            Content = new ExportChoose(),
            // 对话框不继承窗口内容主题，需显式跟随软件主题
            RequestedTheme = AppTheme.CurrentElementTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"]
        };

        await dialog.ShowAsync();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        ContentDialog dialog = new ContentDialog()
        {
            XamlRoot = this.Content.XamlRoot,
            Title = Loader.GetString("ImportWizardTitle"),
            CloseButtonText = Loader.GetString("Close"),
            Content = new ImportChoose(),
            // 对话框不继承窗口内容主题，需显式跟随软件主题
            RequestedTheme = AppTheme.CurrentElementTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"]
        };

        await dialog.ShowAsync();
    }

    // ---- 打印：把报告排成 A4 页面后交给系统打印 ----

    /// <summary>
    /// 打印过程中必须保持存活：文档源和分页都由它持有，
    /// 一旦被回收，打印界面会一直等不到内容。
    /// </summary>
    private PrintHelper? _printHelper;

    /// <summary>
    /// 选择一份跑分文件，按打印排版生成页面，然后打开系统打印界面（预览、选打印机都在系统界面里）。
    /// </summary>
    private async void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<UIElement>? pages;

        try
        {
            pages = await SelectAndBuildPrintPagesAsync();
        }
        catch (Exception ex)
        {
            ShowPrintError(ex);
            return;
        }

        if (pages is null)
        {
            return;
        }

        try
        {
            _printHelper = new PrintHelper(pages, Loader.GetString("PrintTaskName"), ShowPrintError);
            await _printHelper.ShowPrintUIAsync(App.MainWindow!);
        }
        catch (Exception ex)
        {
            ShowPrintError(ex);
        }
        finally
        {
            _printHelper = null;
        }
    }

    /// <summary>打印失败时提示原因；打印线程也会回调到这里，所以自身不能再抛异常。</summary>
    private void ShowPrintError(Exception exception)
    {
        try
        {
            ErrorDialog.Show(XamlRoot, Loader.GetString("PrintFailedTitle"), exception.ToString());
        }
        catch
        {
            // 例如系统打印界面还开着时无法再弹对话框，忽略即可
        }
    }

    /// <summary>选文件并排好全部打印页；取消或没有文件时返回 null。</summary>
    private async Task<IReadOnlyList<UIElement>?> SelectAndBuildPrintPagesAsync()
    {
        List<FileInfo> files;

        try
        {
            files = await Task.Run(() => FileHelper.GetAnalyzableFiles()
                .Select(path => new FileInfo(path))
                .ToList());
        }
        catch (Exception ex) when (ex is ArgumentException || ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
        {
            files = new List<FileInfo>();
        }

        if (files.Count == 0)
        {
            await ShowMessageAsync(Loader.GetString("PrintFailedTitle"), Loader.GetString("PrintNoFile"));
            return null;
        }

        var fileSelector = new ComboBox
        {
            DisplayMemberPath = "Name",
            SelectedValuePath = "FullName",
            ItemsSource = files,
            PlaceholderText = Loader.GetString("SelectFilePlaceholder"),
            MinWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = files.Count - 1, // 默认最新一份
        };

        var preparing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Visibility = Visibility.Collapsed,
        };

        preparing.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        preparing.Children.Add(new TextBlock
        {
            Text = Loader.GetString("PrintPreparing"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var errorText = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel { Spacing = 8, MinWidth = 360 };
        content.Children.Add(new TextBlock { Text = Loader.GetString("PrintSelectPrompt") });
        content.Children.Add(fileSelector);
        content.Children.Add(preparing);
        content.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loader.GetString("PrintDialogTitle"),
            Content = content,
            PrimaryButtonText = Loader.GetString("PrintConfirm"),
            CloseButtonText = Loader.GetString("Close"),
            DefaultButton = ContentDialogButton.Primary,
            // 对话框不继承窗口内容主题，需显式跟随软件主题
            RequestedTheme = AppTheme.CurrentElementTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"]
        };

        IReadOnlyList<UIElement>? pages = null;
        bool dialogClosed = false;

        // 生成过程中用户可能直接关闭对话框：结果要丢弃，也不能再 Hide 已关闭的对话框
        dialog.Closed += (_, _) => dialogClosed = true;

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;

            if (fileSelector.SelectedItem is not FileInfo file)
            {
                errorText.Text = Loader.GetString("PrintNoFile");
                errorText.Visibility = Visibility.Visible;
                return;
            }

            errorText.Visibility = Visibility.Collapsed;
            preparing.Visibility = Visibility.Visible;
            fileSelector.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;

            try
            {
                ReportData data = await ReportData.CreateAsync(file.FullName);

                if (dialogClosed)
                {
                    return;
                }

                pages = PrintReportLayout.CreatePages(data, PrintMeasureHost);
                dialog.Hide();
            }
            catch (Exception ex)
            {
                pages = null;

                if (!dialogClosed)
                {
                    errorText.Text = ex.Message;
                    errorText.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                preparing.Visibility = Visibility.Collapsed;
                fileSelector.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = true;
            }
        };

        await dialog.ShowAsync();
        return pages;
    }

    // ---- 许可证：展示 Assets/LICENSE 内容 ----
    private async void LicenseHyperlink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/LICENSE"));
            string license = await FileIO.ReadTextAsync(file);

            var content = new ScrollViewer
            {
                MaxHeight = 400,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = license,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontFamily = new FontFamily("Consolas"),
                },
            };

            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loader.GetString("MitLicenseTitle"),
                Content = content,
                PrimaryButtonText = Loader.GetString("Copy"),
                PrimaryButtonCommand = new CopyCommand(license),
                CloseButtonText = Loader.GetString("Close"),
                // 对话框不继承窗口内容主题，需显式跟随软件主题
                RequestedTheme = AppTheme.CurrentElementTheme,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"]
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(XamlRoot, Loader.GetString("LicenseReadFailed"), ex.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = Loader.GetString("OK"),
            // 对话框不继承窗口内容主题，需显式跟随软件主题
            RequestedTheme = AppTheme.CurrentElementTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"]
        }.ShowAsync();
    }
}

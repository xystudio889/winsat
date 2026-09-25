using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
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
            Title = "导出向导",
            CloseButtonText = Loader.GetString("Close"),
            Content = new ExportChoose(),
            Width = 700,
            Height = 450
        };

        await dialog.ShowAsync();
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
        }.ShowAsync();
    }
}

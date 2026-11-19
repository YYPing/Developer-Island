using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Core.Updates;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace DeveloperIsland.UI.Update;

/// <summary>What the update dialog can do; the host supplies each step.</summary>
internal sealed record UpdateDialogActions(
    Action<bool> AnswerQuestion,
    Func<ReleaseInfo, IProgress<double>, CancellationToken, Task<UpdateDownloadResult>> Download,
    Func<string, bool> RunSetup,
    Action<ReleaseInfo> Skip,
    Action<Uri> OpenNotes);

/// <summary>
/// A small window for the update flow. Either the one-time question ("Check for updates?") or a
/// release: Install now downloads and verifies it (UpdateDownloader), then hands the setup to the
/// host, which quits; Release notes opens the release page; Later closes; Skip this version remembers it.
/// </summary>
public sealed partial class UpdateWindow : Window
{
    private enum Mode
    {
        Question,
        Available,
        Downloading,
        Failed,
    }

    private readonly UpdateDialogActions _actions;
    private Mode _mode;
    private ReleaseInfo? _release;
    private ReleaseVersion _current;
    private bool _canInstall;
    private CancellationTokenSource? _download;

    internal UpdateWindow(UpdateDialogActions actions, AppTheme theme)
    {
        _actions = actions;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.SetIcon(AppPaths.Icon);
        AppWindow.Title = "Developer Island 更新";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }

        Root.RequestedTheme = theme == AppTheme.Dark ? ElementTheme.Dark : ElementTheme.Default;
        Root.ActualThemeChanged += (_, _) => ApplyCaptionColors();
        ApplyCaptionColors();
        Closed += (_, _) => _download?.Cancel();
        SizeAndCenter();
    }

    /// <summary>A download is running: the host does not replace the dialog's content.</summary>
    public bool IsBusy => _mode == Mode.Downloading;

    /// <summary>The one-time question whether the island may check for updates.</summary>
    public void ShowQuestion()
    {
        _mode = Mode.Question;
        _release = null;
        TitleText.Text = "检查更新？";
        BodyText.Text = "Developer Island 可以每天向 GitHub 询问一次是否有新版本。这个请求会带上应用版本号 "
            + "以及任何网络请求都会带的 IP 地址等信息，除此之外没有别的。不点安装就不会装任何东西。"
            + "随时可以在 设置 → 通用 里改。";
        Progress.Visibility = Visibility.Collapsed;
        SetButtons(primary: "检查更新", secondary: "以后再说", notes: false, skip: false);
    }

    /// <param name="canInstall">False for a copy the setup did not install (portable, development): no in-place update.</param>
    internal void ShowRelease(ReleaseInfo release, ReleaseVersion current, bool canInstall)
    {
        _mode = Mode.Available;
        _release = release;
        _current = current;
        _canInstall = canInstall;
        TitleText.Text = $"Developer Island {release.Version} 可以更新了";
        BodyText.Text = canInstall
            ? $"你当前是 {current}。点「立即安装」会从 GitHub 下载安装包、校验签名、关闭 Developer Island 并启动新版本。你的设置和历史都会保留。"
            : $"你当前是 {current}。这份副本不是用安装包装的，无法自动更新。请到发布页下载新版本。";
        Progress.Visibility = Visibility.Collapsed;
        SetButtons(primary: canInstall ? "立即安装" : null, secondary: "稍后", notes: true, skip: true);
    }

    public void ShowAndFocus() => Activate();

    /// <summary>Development support (<c>--demo --snapshot</c>): renders the dialog offscreen. Mica does not render into a bitmap.</summary>
    public async Task SaveSnapshotAsync(string path)
    {
        AppWindow.Move(new PointInt32(-32000, -32000));
        AppWindow.Show(activateWindow: false);
        Root.Background = new SolidColorBrush(Root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
        await Task.Delay(500);
        await Components.SnapshotWriter.SaveAsync(Root, path);
    }

    /// <summary>Development support: the state while a download runs, without downloading.</summary>
    internal void ShowDownloadingForSnapshot(double progress)
    {
        EnterDownloading();
        Progress.IsIndeterminate = false;
        Progress.Value = progress;
    }

    /// <summary>Development support: a failed verification, without downloading.</summary>
    internal void ShowFailureForSnapshot(UpdateDownloadStatus status) => ShowFailure(FailureText(status), retry: false);

    private void SetButtons(string? primary, string? secondary, bool notes, bool skip)
    {
        PrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primary;
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Content = secondary;
        NotesButton.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = skip ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Question:
                _actions.AnswerQuestion(true);
                Close();
                break;
            case Mode.Available or Mode.Failed when _release is not null && _canInstall:
                _ = InstallAsync(_release);
                break;
        }
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Question:
                _actions.AnswerQuestion(false);
                Close();
                break;
            case Mode.Downloading:
                _download?.Cancel();
                break;
            default:
                Close();
                break;
        }
    }

    private void OnNotes(object sender, RoutedEventArgs e)
    {
        if (_release is not null)
        {
            _actions.OpenNotes(_release.NotesUrl);
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_release is not null)
        {
            _actions.Skip(_release);
            Close();
        }
    }

    private void EnterDownloading()
    {
        _mode = Mode.Downloading;
        BodyText.Text = "正在下载安装包并校验签名…";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        SetButtons(primary: null, secondary: "Cancel", notes: false, skip: false);
    }

    private async Task InstallAsync(ReleaseInfo release)
    {
        EnterDownloading();
        _download = new CancellationTokenSource();
        var progress = new Progress<double>(share =>
        {
            Progress.IsIndeterminate = false;
            Progress.Value = share;
        });

        UpdateDownloadResult result;
        try
        {
            result = await _actions.Download(release, progress, _download.Token);
        }
        catch (Exception ex)
        {
            Log.Error("update", "Update download failed", ex);
            result = new UpdateDownloadResult(UpdateDownloadStatus.Network);
        }

        if (result.Status == UpdateDownloadStatus.Cancelled)
        {
            ShowRelease(release, _current, _canInstall);
            return;
        }

        if (!result.IsReady)
        {
            ShowFailure(FailureText(result.Status), retry: result.Status is UpdateDownloadStatus.Network or UpdateDownloadStatus.Disk);
            return;
        }

        BodyText.Text = "校验通过，正在启动安装程序。Developer Island 马上会重启。";
        Progress.IsIndeterminate = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
        if (!_actions.RunSetup(result.SetupPath!))
        {
            ShowFailure("The setup could not be started. Nothing was changed.", retry: true);
        }
    }

    private void ShowFailure(string text, bool retry)
    {
        _mode = Mode.Failed;
        if (_release is not null)
        {
            TitleText.Text = $"Developer Island {_release.Version} was not installed";
        }

        BodyText.Text = text;
        Progress.Visibility = Visibility.Collapsed;
        SetButtons(primary: retry ? "Try again" : null, secondary: "Close", notes: true, skip: false);
    }

    private static string FailureText(UpdateDownloadStatus status) => status switch
    {
        UpdateDownloadStatus.NoSigningKey => "This build of Developer Island has no update signing key, so it cannot verify updates. Download the new version from the release page.",
        UpdateDownloadStatus.NotSigned => "This release has no signature, so it was not installed. Download it from the release page if you want it.",
        UpdateDownloadStatus.BadSignature => "The release's signature does not match Developer Island's key. Nothing was installed.",
        UpdateDownloadStatus.NoChecksum or UpdateDownloadStatus.HashMismatch => "The downloaded setup does not match its signed checksum. It was deleted; nothing was installed.",
        UpdateDownloadStatus.Disk => "The update could not be saved. Check the free disk space and try again.",
        _ => "GitHub could not be reached. Nothing was installed. Try again later.",
    };

    private void SizeAndCenter()
    {
        var scale = Platform.Native.NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(500 * scale), (int)(270 * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
    }

    private void ApplyCaptionColors()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Color.FromArgb(255, 245, 245, 247) : Color.FromArgb(255, 29, 29, 31);
        bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 122, 122, 122);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x1A, 255, 255, 255) : Color.FromArgb(0x14, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
    }
}

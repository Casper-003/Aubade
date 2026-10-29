using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Vesper.App;

public sealed partial class MainWindow : Window
{
    private readonly string preferencesPath = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Vesper", "shell-preferences.json");
    private readonly UISettings systemUi = new();
    private readonly Dictionary<string, FrameworkElement> pageContent = [];
    private readonly Dictionary<string, ShellPage> pageOwners = [];
    private bool ready;
    private bool changingSelection;
    private bool closed;
    private bool canSave = true;

    public MainWindow()
    {
        ShellPreferences preferences = new();
        try
        {
            if (File.Exists(preferencesPath))
                preferences = JsonSerializer.Deserialize<ShellPreferences>(File.ReadAllText(preferencesPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { canSave = false; }
        InitializeComponent();
        PageArea.DataContext = viewModel;
        InitializeLibraryPresentation();
        viewModel.HideUnavailable = preferences.HideUnavailable;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.Resize(new SizeInt32(1200, 800));
        ApplyWindowMinimumSize(1);
        Navigation.IsPaneOpen = preferences.PaneOpen;
        activityDisplay.IsPaneOpen = Navigation.IsPaneOpen;
        Navigation.SelectedItem = HomeItem;
        if (Navigation.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = "设置";
        VesperAccent.IsChecked = !preferences.SystemAccent;
        SystemAccent.IsChecked = preferences.SystemAccent;
        ready = true;
        Navigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
        {
            if (!ready) return;
            activityDisplay.IsPaneOpen = Navigation.IsPaneOpen;
            SavePreferences();
        });
        systemUi.ColorValuesChanged += OnSystemColorsChanged;
        Closed += (_, _) => { closed = true; systemUi.ColorValuesChanged -= OnSystemColorsChanged; CloseLibraryPresentation(); };
        Activated += (_, _) => ApplyTheme();
        ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("首页", this), new EntranceNavigationTransitionInfo());
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Navigation.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = "设置";
        ApplyTheme(); UpdateTitleLayout();
        if (!canSave) PreferenceNotice.IsOpen = true;
        if (!libraryInitialized)
        {
            libraryInitialized = true;
            await viewModel.InitializeAsync();
        }
    }

    private void OnSystemColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    { if (!closed) ApplyTheme(); });

    private void ApplyTheme()
    {
        if (!ready) return;
        object? light = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        bool dark = light is int value && value == 0;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = dark ? Colors.Gray : Colors.DimGray;
        Windows.UI.Color accent = SystemAccent.IsChecked == true
            ? systemUi.GetColorValue(UIColorType.Accent) : Windows.UI.Color.FromArgb(255, 0x39, 0xC5, 0xBB);
        Navigation.Resources["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(accent);
    }

    private void OnAccentChanged(object sender, RoutedEventArgs e)
    { if (ready) { ApplyTheme(); SavePreferences(); } }

    private void SavePreferences()
    {
        if (!canSave) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);
            string temporary = preferencesPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ShellPreferences(Navigation.IsPaneOpen,
                SystemAccent.IsChecked == true, viewModel.HideUnavailable)));
            File.Move(temporary, preferencesPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PreferenceNotice.IsOpen = true; }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || changingSelection) return;
        string? page = args.IsSettingsSelected ? "设置" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (page is null || (ContentFrame.Content as ShellPage)?.PageKey == page) return;
        ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest(page, this), new EntranceNavigationTransitionInfo());
    }

    private void OnBack(object sender, RoutedEventArgs e)
    { if (ContentFrame.CanGoBack) ContentFrame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft }); }

    private void OnForward(object sender, RoutedEventArgs e)
    { if (ContentFrame.CanGoForward) ContentFrame.GoForward(); }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Parameter is not ShellNavigationRequest request) return;
        string page = request.Key;
        changingSelection = true;
        try
        {
            Navigation.SelectedItem = page == "设置" ? Navigation.SettingsItem
                : Navigation.MenuItems.OfType<NavigationViewItem>().First(item => (string)item.Tag == page);
        }
        finally { changingSelection = false; }
        PageTitle.Text = page;
        LibraryActions.Visibility = page == "曲库" ? Visibility.Visible : Visibility.Collapsed;
        SongFeedbackPanel.Visibility = page is "曲库" or "收藏" ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = ContentFrame.CanGoBack;
        ForwardButton.IsEnabled = ContentFrame.CanGoForward;
        DispatcherQueue.TryEnqueue(UpdateTitleLayout);
    }

    internal void AttachPageContent(ShellPage page, string key)
    {
        if (!pageContent.TryGetValue(key, out FrameworkElement? content))
        {
            content = key switch { "首页" => HomePage, "曲库" => LibraryPage, "收藏" => FavoritesPage, "设置" => AppearancePanel, _ => new Grid() };
            PageStorage.Children.Remove(content);
            pageContent[key] = content;
        }
        if (pageOwners.TryGetValue(key, out ShellPage? previous)) previous.Content = null;
        content.Visibility = Visibility.Visible;
        content.DataContext = viewModel;
        page.Content = content;
        pageOwners[key] = page;
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs e) { if (ready) UpdateTitleLayout(); }

    private void UpdateTitleLayout()
    {
        if (Root.XamlRoot is null || Root.ActualWidth <= 0) return;
        Root.UpdateLayout();
        double scale = Root.XamlRoot.RasterizationScale;
        ApplyWindowMinimumSize(scale);
        double left = HistoryArea.TransformToVisual(Root).TransformPoint(default).X + HistoryArea.ActualWidth;
        double right = Root.ActualWidth - AppWindow.TitleBar.RightInset / scale;
        var regions = new List<RectInt32>();
        void Add(double x, double end)
        {
            if (end > x) regions.Add(new RectInt32((int)Math.Ceiling(x * scale), 0,
                (int)Math.Floor((end - x) * scale), (int)Math.Round(AppTitleBar.ActualHeight * scale)));
        }
        Add(left, right);
        AppWindow.TitleBar.SetDragRectangles(regions.ToArray());
    }

    private void ApplyWindowMinimumSize(double scale)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        int width = (int)Math.Round(900 * scale);
        int height = (int)Math.Round(600 * scale);
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
        if (presenter.State == OverlappedPresenterState.Restored
            && (AppWindow.Size.Width < width || AppWindow.Size.Height < height))
            AppWindow.Resize(new SizeInt32(Math.Max(AppWindow.Size.Width, width), Math.Max(AppWindow.Size.Height, height)));
    }
}

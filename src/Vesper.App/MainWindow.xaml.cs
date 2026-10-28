using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Vesper.App;

public sealed partial class MainWindow : Window
{
    private sealed record Preferences(bool PaneOpen = true, bool SystemAccent = false);
    private readonly string preferencesPath = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Vesper", "shell-preferences.json");
    private readonly UISettings systemUi = new();
    private readonly List<string> history = ["首页"];
    private bool ready;
    private bool changingSelection;
    private bool closed;
    private bool canSave = true;

    public MainWindow()
    {
        Preferences preferences = new();
        try
        {
            if (File.Exists(preferencesPath))
                preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(preferencesPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { canSave = false; }
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.Resize(new SizeInt32(1200, 800));
        Navigation.IsPaneOpen = preferences.PaneOpen;
        Navigation.SelectedItem = HomeItem;
        if (Navigation.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = "设置";
        VesperAccent.IsChecked = !preferences.SystemAccent;
        SystemAccent.IsChecked = preferences.SystemAccent;
        ready = true;
        Navigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
        {
            if (!ready) return;
            UpdateTitleLayout(); SavePreferences();
        });
        systemUi.ColorValuesChanged += OnSystemColorsChanged;
        Closed += (_, _) => { closed = true; systemUi.ColorValuesChanged -= OnSystemColorsChanged; };
        Activated += (_, _) => ApplyTheme();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Navigation.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = "设置";
        ApplyTheme(); UpdateTitleLayout();
        if (!canSave) PreferenceNotice.IsOpen = true;
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
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preferences(Navigation.IsPaneOpen, SystemAccent.IsChecked == true)));
            File.Move(temporary, preferencesPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PreferenceNotice.IsOpen = true; }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || changingSelection) return;
        string? page = args.IsSettingsSelected ? "设置" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (page is null || page == history[^1]) return;
        history.Add(page);
        ShowPage();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    { if (history.Count > 1) { history.RemoveAt(history.Count - 1); ShowPage(); } }

    private void ShowPage()
    {
        string page = history[^1];
        changingSelection = true;
        try
        {
            Navigation.SelectedItem = page == "设置" ? Navigation.SettingsItem
                : Navigation.MenuItems.OfType<NavigationViewItem>().First(item => (string)item.Tag == page);
        }
        finally { changingSelection = false; }
        PageTitle.Text = page;
        AppearancePanel.Visibility = page == "设置" ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = history.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(UpdateTitleLayout);
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs e) { if (ready) UpdateTitleLayout(); }

    private void UpdateTitleLayout()
    {
        TitlePaneColumn.Width = new GridLength(Navigation.IsPaneOpen ? 220 : 56);
        if (Root.XamlRoot is null || Root.ActualWidth <= 0 || SingingEntry.ActualWidth <= 0) return;
        Root.UpdateLayout();
        double scale = Root.XamlRoot.RasterizationScale;
        double left = BrandArea.TransformToVisual(Root).TransformPoint(default).X + BrandArea.ActualWidth;
        double centerLeft = SingingEntry.TransformToVisual(Root).TransformPoint(default).X;
        double centerRight = centerLeft + SingingEntry.ActualWidth;
        double right = Root.ActualWidth - AppWindow.TitleBar.RightInset / scale;
        var regions = new List<RectInt32>();
        void Add(double x, double end)
        {
            if (end > x) regions.Add(new RectInt32((int)Math.Ceiling(x * scale), 0,
                (int)Math.Floor((end - x) * scale), (int)Math.Round(AppTitleBar.ActualHeight * scale)));
        }
        Add(left, centerLeft); Add(centerRight, right);
        AppWindow.TitleBar.SetDragRectangles(regions.ToArray());
    }
}

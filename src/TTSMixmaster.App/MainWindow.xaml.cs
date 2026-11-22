using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using TTSMixmaster.App.Views;
using Windows.Graphics;

namespace TTSMixmaster.App;

/// <summary>Shell: custom title bar, left navigation, and the frame pages load into.</summary>
public sealed partial class MainWindow : Window
{
    // Multi-pane app (nav + list + detail): 1200×800 DIP, never smaller than 720×560.
    private const int DefaultWidth = 1200, DefaultHeight = 800, MinWidth = 720, MinHeight = 560;

    private readonly AppServices _services;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        SizeWindow();

        ApplyTheme();
        services.ThemeChanged += ApplyTheme;
        Closed += (_, _) => TrySaveSettings();

        RootFrame.Navigate(typeof(CollectionsPage));
    }

    /// <summary>Navigate to a page from inside another page (drill-in, or a link to Settings).</summary>
    public void Navigate(Type page, object? parameter = null) =>
        RootFrame.Navigate(page, parameter, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        if (NavView.SettingsItem is NavigationViewItem settings)
            AutomationProperties.SetAutomationId(settings, "NavSettings");
        SyncSelection();
    }

    // ItemInvoked (not SelectionChanged): it also fires when the already-selected item is clicked,
    // which is how "Collections" takes you back up from a collection.
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var target = args.IsSettingsInvoked ? typeof(SettingsPage)
            : args.InvokedItemContainer?.Tag as string == "Builds" ? typeof(BuildsPage)
            : typeof(CollectionsPage);
        var current = RootFrame.CurrentSourcePageType;
        if (current == target)
            return;
        if (target == typeof(CollectionsPage) && current == typeof(CollectionPage) && RootFrame.CanGoBack)
            RootFrame.GoBack();
        else
            RootFrame.Navigate(target, null, new EntranceNavigationTransitionInfo());
    }

    private void RootFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // Back means "up from a drill-in", not "the last section I clicked": top-level pages clear history.
        if (e.SourcePageType != typeof(CollectionPage))
            RootFrame.BackStack.Clear();
        AppTitleBar.IsBackButtonVisible = RootFrame.CanGoBack;
        SyncSelection();
    }

    private void SyncSelection()
    {
        var page = RootFrame.CurrentSourcePageType;
        object? item = page == typeof(SettingsPage) ? NavView.SettingsItem
            : page == typeof(BuildsPage) ? BuildsItem
            : CollectionsItem;
        if (item is null || ReferenceEquals(NavView.SelectedItem, item))
            return;
        NavView.SelectedItem = item;
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args)
    {
        if (RootFrame.CanGoBack)
            RootFrame.GoBack();
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void ApplyTheme()
    {
        var theme = _services.Settings.Theme.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        RootGrid.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void TrySaveSettings()
    {
        try
        {
            _services.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Closing, so there's nowhere left to report it; the Settings page reports save failures while open.
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private void SizeWindow()
    {
        var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;

        // Never bigger than the screen it opens on (small laptops, Windows Sandbox), and centred there.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)(DefaultWidth * scale), work.Width);
        var height = Math.Min((int)(DefaultHeight * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
        }
    }
}

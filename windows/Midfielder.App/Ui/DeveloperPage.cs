using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>
/// Diagnostics and the live log. Docked layout: header and status on top, the log list fills the rest of the
/// window with its own scrollbar, so the page itself never scrolls and never has an empty tail.
/// </summary>
internal sealed class DeveloperPage : PageBase
{
    private readonly Grid _status = new();
    private readonly ObservableCollection<LogRow> _rows = [];
    private readonly Fluent.TextBox _filter = new() { PlaceholderText = "Filter log…", Width = 240, Icon = new Fluent.SymbolIcon { Symbol = Fluent.SymbolRegular.Search24 } };
    private readonly Fluent.ToggleSwitch _errorsOnly = new() { Content = "Errors only" };
    private readonly System.Windows.Threading.DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public DeveloperPage(IAppHost host, MainWindow window) : base(host, window, scrolling: false)
    {
        var root = new Grid { MaxWidth = 1100, Margin = new Thickness(36, 28, 36, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Header("Developer", "Diagnostics for when something doesn't sync"));

        _status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var statusCard = Fx.Card(_status, 18);
        statusCard.Margin = new Thickness(0, 20, 0, 16);
        Grid.SetRow(statusCard, 1);
        root.Children.Add(statusCard);

        var list = new ListView
        {
            ItemsSource = _rows,
            ItemTemplate = (DataTemplate)XamlReader.Parse("""
                <DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                  <Grid Margin='0,1'>
                    <Grid.ColumnDefinitions><ColumnDefinition Width='4'/><ColumnDefinition Width='100'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
                    <Border Background='{Binding Stripe}' CornerRadius='2' Margin='0,2'/>
                    <TextBlock Grid.Column='1' Text='{Binding Time}' Margin='10,0,0,0' FontFamily='Cascadia Mono, Consolas' FontSize='12' Foreground='#5E6B66'/>
                    <TextBlock Grid.Column='2' Text='{Binding Message}' ToolTip='{Binding Full}' FontFamily='Cascadia Mono, Consolas' FontSize='12'
                               Foreground='{Binding Foreground}' TextTrimming='CharacterEllipsis'/>
                  </Grid>
                </DataTemplate>
                """),
            BorderThickness = new Thickness(0),
            Background = Fx.Surface,
        };
        // Long lines are trimmed (full text in the tooltip), so there is never a horizontal scrollbar.
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);

        var copy = Fx.Secondary("Copy", Fluent.SymbolRegular.Copy24, CopyLog);
        var openFile = Fx.Secondary("Open log file", Fluent.SymbolRegular.Open24, () => Shell.OpenFile(AppLog.Path));
        var clear = Fx.Subtle("Clear view", Fluent.SymbolRegular.Delete24, () =>
        {
            AppLog.ClearRecent();
            RenderLog();
        });
        var tools = Fx.Wrap(10, _filter, _errorsOnly, copy, openFile, clear);
        _errorsOnly.VerticalAlignment = VerticalAlignment.Center;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var title = Fx.Heading("Activity log");
        title.Margin = new Thickness(0, 6, 24, 10);
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        head.Children.Add(tools);
        var logGrid = new Grid();
        logGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        logGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        logGrid.Children.Add(head);
        Grid.SetRow(list, 1);
        logGrid.Children.Add(list);
        var logCard = Fx.Card(logGrid, 18, lift: false);
        Grid.SetRow(logCard, 2);
        root.Children.Add(logCard);
        Content = root;

        _filter.TextChanged += (_, _) => RenderLog();
        _errorsOnly.Checked += (_, _) => RenderLog();
        _errorsOnly.Unchecked += (_, _) => RenderLog();
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            RenderLog();
        };
        Host.StatusChanged += OnStatus;
        AppLog.Logged += OnLogged;
        RenderStatus();
        RenderLog();
    }

    private void OnStatus() => Post(RenderStatus);

    // Entries arrive in bursts; redraw at most ~3 times a second.
    private void OnLogged(LogEntry e) => Post(() => { if (!_refresh.IsEnabled) _refresh.Start(); });

    private void RenderStatus()
    {
        _status.Children.Clear();
        _status.RowDefinitions.Clear();
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
        var connected = Host.ConnectedDevices;
        var rows = new (string, string)[]
        {
            ("Status", Host.StatusText),
            ("Version", version),
            ("Connected", connected.Count == 0 ? "—" : string.Join(", ", connected.Select(d => $"{d.Name} ({Host.AddressOf(d) ?? "?"})"))),
            ("Listening port", Host.Port.ToString(CultureInfo.InvariantCulture)),
            ("Paired", Host.PairedDevices.Count == 0 ? "—" : string.Join(", ", Host.PairedDevices.Select(d => d.Name))),
            ("Local addresses", string.Join(", ", LanDiscovery.LocalAddresses(Host.Port).Select(a => a.Host))),
            ("Device ID", Host.DeviceId),
            ("Fingerprint", Host.Fingerprint),
            ("Windows 11 menu", Host.ModernMenu.ToString()),
            ("Log file", AppLog.Path),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            int r = i / 2, c = i % 2 * 2;
            if (c == 0) _status.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = Fx.Caption(rows[i].Item1);
            key.Margin = new Thickness(c == 0 ? 0 : 24, 5, 14, 5);
            var value = new TextBox
            {
                Text = rows[i].Item2,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Fx.Transparent,
                FontFamily = Fx.Mono,
                FontSize = 12.5,
                Foreground = Fx.Ink,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 5, 0, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Style = null,
            };
            Grid.SetRow(key, r);
            Grid.SetColumn(key, c);
            Grid.SetRow(value, r);
            Grid.SetColumn(value, c + 1);
            _status.Children.Add(key);
            _status.Children.Add(value);
        }
    }

    private void RenderLog()
    {
        string q = _filter.Text.Trim();
        bool errorsOnly = _errorsOnly.IsChecked == true;
        _rows.Clear();
        foreach (var e in AppLog.Entries())
        {
            if (errorsOnly && !e.IsError) continue;
            if (q.Length > 0 && !e.Message.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            _rows.Add(new LogRow(e));
        }
    }

    private void CopyLog()
    {
        string text = string.Join(Environment.NewLine, AppLog.Entries().Select(e =>
            $"{e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {(e.IsError ? "ERROR" : "INFO ")} {e.Message}"));
        if (text.Length == 0) return;
        System.Windows.Clipboard.SetText(text);
        Window.Toast("Log copied");
    }

    protected override void OnDispose()
    {
        Host.StatusChanged -= OnStatus;
        AppLog.Logged -= OnLogged;
        _refresh.Stop();
    }

    internal sealed class LogRow(LogEntry e)
    {
        public string Time { get; } = e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        public string Message { get; } = e.Message.Split('\n', 2)[0].TrimEnd('\r');
        public string Full { get; } = e.Message;
        public Brush Foreground { get; } = e.IsError ? Fx.Danger : Fx.Ink;
        public Brush Stripe { get; } = e.IsError ? Fx.Danger : Fx.HighlightStrong;
    }
}

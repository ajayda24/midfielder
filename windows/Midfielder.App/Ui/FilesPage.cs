using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Fluent = Wpf.Ui.Controls;

namespace Midfielder.App.Ui;

/// <summary>
/// Sent and received files as a list or a gallery. Both views are virtualized (only visible rows exist, so
/// only visible thumbnails load). Click selects (Ctrl/Shift for more), double-click or Enter opens, Del deletes
/// to the Recycle Bin, right-click for everything else; "Remove from list" can be undone.
/// </summary>
internal sealed class FilesPage : PageBase
{
    private enum Filter { All, Received, Sent, Images }

    private static Filter s_filter = Filter.All;
    private static bool s_gallery;

    private readonly ThumbnailCache _thumbs;
    private readonly ItemsControl _list;
    private readonly FrameworkElement _empty;
    private readonly Dictionary<Filter, Border> _segments = [];
    private readonly Fluent.Button _view;
    private readonly Fluent.Button _open;
    private readonly Fluent.Button _reveal;
    private readonly Fluent.Button _delete;
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private List<FileItem> _items = [];
    private int _anchor = -1;
    private int _columns;

    public FilesPage(IAppHost host, MainWindow window) : base(host, window, scrolling: false)
    {
        _thumbs = new ThumbnailCache(Dispatcher, 220);

        var root = new Grid { MaxWidth = 1100, Margin = new Thickness(36, 28, 36, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _view = Fx.Secondary("Gallery", Fluent.SymbolRegular.Grid24, () => SetGallery(!s_gallery));
        var openFolder = Fx.Secondary("Open folder", Fluent.SymbolRegular.FolderOpen24, () => Shell.OpenFolder(Host.Settings.ReceiveFolder));
        root.Children.Add(Fx.Split(Header("Files", "Everything you sent and received"), Fx.Row(8, _view, openFolder)));

        _open = Fx.Secondary("Open", Fluent.SymbolRegular.Open24, () => { foreach (var r in SelectedRecords().Take(10)) Open(Window, Host, r); });
        _reveal = Fx.Secondary("Show in folder", Fluent.SymbolRegular.FolderOpen24, () => { if (SelectedRecords().FirstOrDefault() is { } r) Reveal(r); });
        _delete = Fx.Button("Delete", Fluent.SymbolRegular.Delete24, Fluent.ControlAppearance.Danger, () => _ = DeleteAsync(SelectedRecords()));
        var toolbar = Fx.Split(Segmented(), Fx.Row(8, _open, _reveal, _delete));
        toolbar.Margin = new Thickness(0, 20, 0, 12);
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        _list = VirtualList();
        _list.PreviewMouseLeftButtonDown += OnClick;
        _list.PreviewMouseRightButtonDown += OnRightClick;
        _list.KeyDown += OnKey;
        _list.SizeChanged += (_, _) => { if (s_gallery && ColumnsFor() != _columns) Rebind(); };
        Grid.SetRow(_list, 2);
        root.Children.Add(_list);

        var emptyIcon = Fx.Badge(Fluent.SymbolRegular.FolderOpen24, 88, circle: true);
        emptyIcon.HorizontalAlignment = HorizontalAlignment.Center;
        var emptyTitle = Fx.Text("No files yet", 18, FontWeights.SemiBold);
        var emptyHint = Fx.Caption("Files you send or receive show up here. Drop files on Home, or right-click them in Explorer → Send to phone.", wrap: true);
        emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        emptyHint.TextAlignment = TextAlignment.Center;
        emptyHint.MaxWidth = 380;
        _empty = Fx.Stack(14, emptyIcon, emptyTitle, emptyHint);
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Margin = new Thickness(0, 0, 0, 60);
        Grid.SetRow(_empty, 2);
        root.Children.Add(_empty);

        Content = root;
        Host.History.Changed += OnHistory;
        SetGallery(s_gallery);
    }

    // --- Views -------------------------------------------------------------------------------------

    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:ui='http://schemas.lepo.co/wpfui/2022/xaml'";

    // Per page: templates belong to the UI thread that parsed them.
    private readonly DataTemplate _rowTemplate = (DataTemplate)XamlReader.Parse($$"""
        <DataTemplate {{Ns}}>
          <Border x:Name='root' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='0,0,6,6' Padding='12,10' CornerRadius='10'
                  Background='{Binding Background}' BorderBrush='{Binding Border}' BorderThickness='1' Cursor='Hand'>
            <Grid>
              <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
              <Grid Width='48' Height='48'>
                <Rectangle RadiusX='10' RadiusY='10' Fill='#E6F4EE'/>
                <ui:SymbolIcon Symbol='{Binding Symbol}' FontSize='22' Foreground='#1F6F5C' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                <Rectangle RadiusX='10' RadiusY='10'><Rectangle.Fill><ImageBrush ImageSource='{Binding Thumb}' Stretch='UniformToFill'/></Rectangle.Fill></Rectangle>
              </Grid>
              <StackPanel Grid.Column='1' Margin='14,0,0,0' VerticalAlignment='Center'>
                <TextBlock Text='{Binding Name}' FontSize='14.5' FontWeight='SemiBold' Foreground='#1B2421' TextTrimming='CharacterEllipsis'/>
                <TextBlock Text='{Binding Meta}' FontSize='12.5' Foreground='{Binding MetaBrush}' Margin='0,3,0,0' TextTrimming='CharacterEllipsis'/>
              </StackPanel>
            </Grid>
          </Border>
          <DataTemplate.Triggers>
            <Trigger SourceName='root' Property='IsMouseOver' Value='True'><Setter TargetName='root' Property='BorderBrush' Value='#C9E7DA'/></Trigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """);

    private readonly DataTemplate _galleryTemplate = (DataTemplate)XamlReader.Parse($$"""
        <DataTemplate {{Ns}}>
          <ItemsControl ItemsSource='{Binding Items}' Margin='0,0,0,0' Focusable='False'>
            <ItemsControl.ItemsPanel><ItemsPanelTemplate><UniformGrid Rows='1' Columns='{Binding Columns}'/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <Grid x:Name='tile' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='0,0,10,10' Height='{Binding TileSize}' Cursor='Hand' Background='Transparent'>
                  <Rectangle RadiusX='14' RadiusY='14' Fill='#E6F4EE'/>
                  <StackPanel VerticalAlignment='Center' Margin='10,0'>
                    <ui:SymbolIcon Symbol='{Binding Symbol}' FontSize='34' Foreground='#1F6F5C' HorizontalAlignment='Center'/>
                    <TextBlock Text='{Binding Name}' Visibility='{Binding NameVisibility}' Margin='0,10,0,0' FontSize='12' Foreground='#5E6B66'
                               TextAlignment='Center' TextTrimming='CharacterEllipsis'/>
                  </StackPanel>
                  <Rectangle RadiusX='14' RadiusY='14'><Rectangle.Fill><ImageBrush ImageSource='{Binding Thumb}' Stretch='UniformToFill'/></Rectangle.Fill></Rectangle>
                  <Rectangle x:Name='ring' RadiusX='14' RadiusY='14' Stroke='{Binding Ring}' StrokeThickness='3'/>
                  <Border Background='#CC1F6F5C' CornerRadius='11' Width='22' Height='22' HorizontalAlignment='Right' VerticalAlignment='Top' Margin='8'
                          Visibility='{Binding CheckVisibility}'>
                    <ui:SymbolIcon Symbol='Checkmark24' FontSize='14' Foreground='White' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                  </Border>
                </Grid>
                <DataTemplate.Triggers>
                  <Trigger SourceName='tile' Property='IsMouseOver' Value='True'><Setter TargetName='tile' Property='Opacity' Value='0.88'/></Trigger>
                </DataTemplate.Triggers>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </DataTemplate>
        """);

    private static ItemsControl VirtualList()
    {
        var list = new ItemsControl { Focusable = true, FocusVisualStyle = null, Padding = new Thickness(0, 0, 4, 0) };
        list.Template = (ControlTemplate)XamlReader.Parse($$"""
            <ControlTemplate TargetType='ItemsControl' {{Ns}}>
              <ScrollViewer Focusable='False' CanContentScroll='True' Padding='{TemplateBinding Padding}'><ItemsPresenter/></ScrollViewer>
            </ControlTemplate>
            """);
        list.ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse($"<ItemsPanelTemplate {Ns}><VirtualizingStackPanel/></ItemsPanelTemplate>");
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(list, true);
        return list;
    }

    private UIElement Segmented()
    {
        var bar = new Border
        {
            Background = Fx.Highlight,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var f in Enum.GetValues<Filter>())
        {
            var seg = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Colors.Transparent),
                Child = Fx.Text(f.ToString(), 13.5, FontWeights.SemiBold, Fx.Muted),
            };
            seg.MouseLeftButtonUp += (_, _) => SetFilter(f);
            _segments[f] = seg;
            row.Children.Add(seg);
        }
        bar.Child = row;
        PaintSegments();
        return bar;
    }

    private void PaintSegments()
    {
        foreach (var (f, seg) in _segments)
        {
            bool on = f == s_filter;
            Anim.Tint(seg, on ? Colors.White : Colors.Transparent);
            ((TextBlock)seg.Child).Foreground = on ? Fx.PrimaryDark : Fx.Muted;
            // The selected segment stands out with an edge, not a shadow effect (effects slow scrolling).
            seg.BorderBrush = on ? Fx.CardEdge : Fx.Transparent;
            seg.BorderThickness = new Thickness(1, 1, 1, 1.5);
        }
    }

    private void SetFilter(Filter f)
    {
        s_filter = f;
        PaintSegments();
        Reload();
    }

    private void SetGallery(bool on)
    {
        s_gallery = on;
        _view.Content = on ? "List" : "Gallery";
        _view.Icon = new Fluent.SymbolIcon { Symbol = on ? Fluent.SymbolRegular.List24 : Fluent.SymbolRegular.Grid24, FontSize = 16 };
        _list.ItemTemplate = on ? _galleryTemplate : _rowTemplate;
        Reload();
    }

    private void OnHistory() => Post(Reload);

    private void Reload()
    {
        var all = Host.History.Records();
        var records = s_filter switch
        {
            Filter.Received => all.Where(r => r.Direction == TransferDirection.Received),
            Filter.Sent => all.Where(r => r.Direction == TransferDirection.Sent),
            Filter.Images => all.Where(r => r.IsImage),
            _ => all,
        };
        _items = records.Select(r => new FileItem(r, this)).ToList();
        _selected.IntersectWith(_items.Select(i => i.Record.Id));
        foreach (var i in _items) i.Selected = _selected.Contains(i.Record.Id);
        _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Rebind();
        UpdateButtons();
    }

    private int ColumnsFor() => Math.Max(2, (int)((Math.Max(0, _list.ActualWidth - 8) + 10) / 180));

    private void Rebind()
    {
        if (!s_gallery)
        {
            _list.ItemsSource = _items;
            return;
        }
        _columns = ColumnsFor();
        double tile = Math.Max(120, (_list.ActualWidth - 8) / _columns - 10);
        var rows = new List<GalleryRow>();
        for (int i = 0; i < _items.Count; i += _columns)
        {
            var slice = _items.Skip(i).Take(_columns).ToList();
            foreach (var it in slice) it.TileSize = tile;
            rows.Add(new GalleryRow(slice, _columns));
        }
        _list.ItemsSource = rows;
    }

    // --- Selection & input -------------------------------------------------------------------------

    private static FileItem? ItemAt(object source) => (source as FrameworkElement)?.DataContext as FileItem
        ?? ((source as FrameworkContentElement)?.Parent as FrameworkElement)?.DataContext as FileItem;

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        _list.Focus();
        var item = ItemAt(e.OriginalSource);
        if (item is null) return;
        if (e.ClickCount == 2)
        {
            Open(Window, Host, item.Record);
            e.Handled = true;
            return;
        }
        int index = _items.IndexOf(item);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (!_selected.Add(item.Record.Id)) _selected.Remove(item.Record.Id);
            _anchor = index;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _anchor >= 0)
        {
            _selected.Clear();
            for (int k = Math.Min(_anchor, index); k <= Math.Max(_anchor, index); k++) _selected.Add(_items[k].Record.Id);
        }
        else
        {
            _selected.Clear();
            _selected.Add(item.Record.Id);
            _anchor = index;
        }
        Refresh();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource);
        if (item is null) return;
        if (!_selected.Contains(item.Record.Id))
        {
            _selected.Clear();
            _selected.Add(item.Record.Id);
            Refresh();
        }
        ShowMenu(SelectedRecords());
        e.Handled = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selected.Count > 0) _ = DeleteAsync(SelectedRecords());
        else if (e.Key == Key.Enter && SelectedRecords().FirstOrDefault() is { } r) Open(Window, Host, r);
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            foreach (var i in _items) _selected.Add(i.Record.Id);
            Refresh();
        }
        else if (e.Key == Key.Escape)
        {
            _selected.Clear();
            Refresh();
        }
        else return;
        e.Handled = true;
    }

    private void Refresh()
    {
        foreach (var i in _items) i.Selected = _selected.Contains(i.Record.Id);
        UpdateButtons();
    }

    private List<TransferRecord> SelectedRecords() => _items.Where(i => i.Selected).Select(i => i.Record).ToList();

    private void UpdateButtons()
    {
        int n = _selected.Count;
        _open.IsEnabled = n > 0;
        _reveal.IsEnabled = n == 1;
        _delete.IsEnabled = n > 0;
    }

    private void ShowMenu(List<TransferRecord> records)
    {
        if (records.Count == 0) return;
        var menu = new ContextMenu();
        void Item(string text, Fluent.SymbolRegular icon, Action a)
        {
            var mi = new MenuItem { Header = text, Icon = new Fluent.SymbolIcon { Symbol = icon } };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Item("Open", Fluent.SymbolRegular.Open24, () => { foreach (var r in records.Take(10)) Open(Window, Host, r); });
        if (records.Count == 1)
        {
            Item("Show in folder", Fluent.SymbolRegular.FolderOpen24, () => Reveal(records[0]));
            Item("Copy path", Fluent.SymbolRegular.Copy24, () =>
            {
                System.Windows.Clipboard.SetText(records[0].Location);
                Window.Toast("Path copied");
            });
        }
        menu.Items.Add(new Separator());
        bool anyReceived = records.Any(r => r.Direction == TransferDirection.Received);
        Item(anyReceived ? "Delete" : "Remove from list", Fluent.SymbolRegular.Delete24, () => _ = DeleteAsync(records));
        if (anyReceived) Item("Remove from list only", Fluent.SymbolRegular.Dismiss24, () => RemoveWithUndo(records));
        menu.IsOpen = true;
    }

    // --- Actions -----------------------------------------------------------------------------------

    public static void Open(MainWindow window, IAppHost host, TransferRecord r)
    {
        if (File.Exists(r.Location))
        {
            Shell.OpenFile(r.Location);
            return;
        }
        _ = MissingAsync(window, host, r);
    }

    private static async Task MissingAsync(MainWindow window, IAppHost host, TransferRecord r)
    {
        if (await window.ConfirmAsync("File not found", $"{r.Name} is no longer at {r.Location}. Remove it from the list?", "Remove"))
        {
            host.History.Remove([r.Id]);
        }
    }

    private void Reveal(TransferRecord r)
    {
        if (File.Exists(r.Location)) Shell.Reveal(r.Location);
        else _ = MissingAsync(Window, Host, r);
    }

    private async Task DeleteAsync(List<TransferRecord> records)
    {
        if (records.Count == 0) return;
        var onDisk = records.Where(r => r.Direction == TransferDirection.Received && File.Exists(r.Location)).ToList();
        string what = records.Count == 1 ? $"\"{records[0].Name}\"" : $"{records.Count} files";
        string message = onDisk.Count > 0
            ? $"Move {what} to the Recycle Bin and remove from the list? You can restore them from the Recycle Bin."
            : $"Remove {what} from the list? Files you sent stay where they are.";
        if (!await Window.ConfirmAsync(onDisk.Count > 0 ? "Delete files" : "Remove from list", message, onDisk.Count > 0 ? "Delete" : "Remove", danger: true)) return;
        foreach (var r in onDisk)
        {
            try
            {
                Shell.Recycle(r.Location);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                AppLog.Error($"Could not delete {r.Name}", e);
            }
        }
        _selected.Clear();
        Host.History.Remove(records.Select(r => r.Id));
        Window.Toast(onDisk.Count > 0 ? (onDisk.Count == 1 ? "Moved to the Recycle Bin" : $"{onDisk.Count} files moved to the Recycle Bin") : "Removed from list");
    }

    private void RemoveWithUndo(List<TransferRecord> records)
    {
        _selected.Clear();
        Host.History.Remove(records.Select(r => r.Id));
        Window.Toast(records.Count == 1 ? "Removed from list" : $"{records.Count} removed from list", () => Host.History.Restore(records));
    }

    internal void RequestThumb(FileItem item)
    {
        if (!Disposed) _thumbs.Request(item.Record.Location, img => item.Thumb = img);
    }

    protected override void OnDispose()
    {
        Host.History.Changed -= OnHistory;
        _list.ItemsSource = null;
        _thumbs.Dispose();
    }

    // --- View models -------------------------------------------------------------------------------

    internal sealed class GalleryRow(List<FileItem> items, int columns)
    {
        public List<FileItem> Items { get; } = items;
        public int Columns { get; } = columns;
    }

    /// <summary>One file in either view. The thumbnail loads the first time a visible row asks for it.</summary>
    internal sealed class FileItem(TransferRecord record, FilesPage page) : INotifyPropertyChanged
    {
        private static readonly Brush SelectedBg = Fx.HighlightStrong;
        private ImageSource? _thumb;
        private bool _requested;
        private bool _selected;
        private double _tile = 150;

        public event PropertyChangedEventHandler? PropertyChanged;

        public TransferRecord Record { get; } = record;
        public string Name => Record.Name;

        public string Meta
        {
            get
            {
                string dir = Record.Direction == TransferDirection.Sent ? $"↑ to {Record.Peer}" : $"↓ from {Record.Peer}";
                string state = Record.Status switch { TransferStatus.Failed => " · failed", TransferStatus.Cancelled => " · cancelled", _ => "" };
                return $"{dir} · {Fx.Bytes(Record.Size)} · {Fx.When(Record.Time)}{state}";
            }
        }

        public Brush MetaBrush => Record.Status == TransferStatus.Done ? Fx.Muted : Fx.Danger;

        public Fluent.SymbolRegular Symbol => Record.IsImage ? Fluent.SymbolRegular.Image24
            : Record.Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? Fluent.SymbolRegular.Video24
            : Record.Mime == "application/pdf" ? Fluent.SymbolRegular.DocumentPdf24
            : Fluent.SymbolRegular.Document24;

        public Visibility NameVisibility => Record.IsImage ? Visibility.Collapsed : Visibility.Visible;

        public ImageSource? Thumb
        {
            get
            {
                if (!_requested && (Record.IsImage || Record.Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
                {
                    _requested = true;
                    page.RequestThumb(this);
                }
                return _thumb;
            }
            set
            {
                _thumb = value;
                Changed(nameof(Thumb));
            }
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                Changed(nameof(Background));
                Changed(nameof(Border));
                Changed(nameof(Ring));
                Changed(nameof(CheckVisibility));
            }
        }

        public Brush Background => _selected ? SelectedBg : Fx.Surface;
        public Brush Border => _selected ? Fx.Green : Fx.Outline;
        public Brush Ring => _selected ? Fx.Green : Fx.Transparent;
        public Visibility CheckVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;

        public double TileSize
        {
            get => _tile;
            set
            {
                _tile = value;
                Changed(nameof(TileSize));
            }
        }

        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

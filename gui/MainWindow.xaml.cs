using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SpaceSaver;

namespace SpaceSaver.Gui;

public partial class MainWindow : Window
{
    enum Kind { Folder, Promoted, Remainder, Files }

    sealed class Entry
    {
        public required DirNode Node;
        public bool Clickable;
        public Kind Kind;
    }

    readonly List<DirNode> _history = new();                       // путь навигации, последний = текущая папка
    // Свёртки храним по путям (а не по объектам) — чтобы переживали пересканирование.
    readonly Dictionary<string, HashSet<string>> _unwrapped = new(StringComparer.OrdinalIgnoreCase);
    List<Entry> _entries = new();                                  // эффективные записи текущего вида

    ScanStats? _stats;
    readonly DispatcherTimer _timer;
    readonly Stopwatch _sw = new();
    bool _scanning;
    bool _dark = true;
    string _inspectPath = "";
    DirNode? _inspectNode;                                         // узел, показанный в панели деталей
    readonly List<(Window Window, Action Retheme)> _childWindows = new(); // открытые окна Отчёт/Что удалить
    const string GithubUrl = "https://github.com/ChernyshellyOfficial";

    DirNode? Current => _history.Count > 0 ? _history[^1] : null;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => UpdateScanProgress();

        Treemap.Inspect += OnInspect;
        Treemap.Drill += DrillInto;
        Treemap.ContextRequested += OnTreemapContext;

        ApplyTheme();
        PathBox.Text = DefaultPath();
        Status.Text = "Выбери папку и нажми «Обзор…» — скан начнётся сразу. Можно перетащить папку в окно.";

        Loaded += OnLoaded;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Путь можно передать аргументом: SpaceSaver.exe "D:\"
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i].Trim().Trim('"');
            if (Directory.Exists(a))
            {
                PathBox.Text = a;
                await StartScan(a);
                break;
            }
        }
    }

    static string DefaultPath()
    {
        try
        {
            var d = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && x.DriveType == DriveType.Fixed);
            if (d != null) return d.RootDirectory.FullName;
        }
        catch { }
        return Directory.GetCurrentDirectory();
    }

    // ======================= тема =======================
    void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        _dark = !_dark;
        ApplyTheme();
        ApplyDarkTitleBar(this, _dark);
        foreach (var (w, retheme) in _childWindows.ToList())
        {
            ApplyDarkTitleBar(w, _dark);
            retheme();
        }
        if (Current != null) RefreshView();
    }

    void ApplyTheme()
    {
        void Set(string key, string hex) => Application.Current.Resources[key] = new SolidColorBrush(Hex(hex));

        if (_dark)
        {
            Set("WinBg", "#0F1115"); Set("BarBg", "#161A21"); Set("PanelBg", "#161A21"); Set("InputBg", "#0C0F14");
            Set("Fg", "#E6E8EC"); Set("FgStrong", "#FFFFFF"); Set("FgDim", "#9AA2AE");
            Set("Accent", "#2F6FED"); Set("AccentHover", "#3D8BFD"); Set("AccentPressed", "#2A63D6"); Set("AccentFg", "#FFFFFF");
            Set("BtnBg", "#222732"); Set("BtnHover", "#2C3340"); Set("BtnPressed", "#39414F");
            Set("Border", "#222732"); Set("RowHover", "#1B202A"); Set("RowSel", "#243049");
            Set("CrumbFg", "#9CC4FF"); Set("CrumbHover", "#1C2230");
            ThemeButton.Content = "Светлая тема";
        }
        else
        {
            Set("WinBg", "#F4F5F7"); Set("BarBg", "#FFFFFF"); Set("PanelBg", "#FFFFFF"); Set("InputBg", "#FFFFFF");
            Set("Fg", "#1F2430"); Set("FgStrong", "#0B0E14"); Set("FgDim", "#6B7280");
            Set("Accent", "#2F6FED"); Set("AccentHover", "#1D62E8"); Set("AccentPressed", "#1A56C9"); Set("AccentFg", "#FFFFFF");
            Set("BtnBg", "#E9ECF1"); Set("BtnHover", "#DFE3EA"); Set("BtnPressed", "#D2D7E0");
            Set("Border", "#E2E5EA"); Set("RowHover", "#EEF1F6"); Set("RowSel", "#DCE7FF");
            Set("CrumbFg", "#2F6FED"); Set("CrumbHover", "#ECF1FB");
            ThemeButton.Content = "Тёмная тема";
        }
        Treemap.SetTheme(!_dark);
    }

    // ----- тёмный заголовок окна (нативная рамка Windows, поведение сохраняется) -----
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static void ApplyDarkTitleBar(Window w, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            int val = dark ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (новые сборки Win10/11), 19 — для старых.
            if (DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref val, sizeof(int));
        }
        catch { }
    }

    void ShowThemedWindow(Window w, Action retheme)
    {
        ApplyDarkTitleBar(w, _dark);
        var entry = (w, retheme);
        _childWindows.Add(entry);
        w.Closed += (_, _) => _childWindows.Remove(entry);
        w.Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar(this, _dark);
    }

    // ======================= сканирование =======================
    // «Обновить» — пересканировать текущий корень, сохранив свёртки и позицию навигации.
    async void OnRescan(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) { await StartScan(PathBox.Text); return; }
        var navPaths = _history.Select(h => h.Path).ToList();
        await StartScan(navPaths[0], navPaths);
    }

    async Task StartScan(string? path, List<string>? restoreNav = null)
    {
        if (_scanning) return;

        path = (path ?? "").Trim().Trim('"');
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Status.Text = $"Папка не найдена: {path}";
            return;
        }

        PathBox.Text = path;
        _scanning = true;
        SetControlsEnabled(false);
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        _stats = new ScanStats();
        _sw.Restart();
        _timer.Start();

        var dir = new DirectoryInfo(path);
        try
        {
            var stats = _stats;
            var root = await Task.Run(() => Scanner.ScanRoot(dir, stats));
            _sw.Stop();

            _history.Clear();
            _history.Add(root);

            if (restoreNav == null)
            {
                // новая папка — начинаем с чистого листа
                _unwrapped.Clear();
            }
            else
            {
                // обновление — восстанавливаем позицию по путям (свёртки уже привязаны к путям)
                var cur = root;
                for (int i = 1; i < restoreNav.Count; i++)
                {
                    var next = cur.Children.FirstOrDefault(c =>
                        string.Equals(c.Path, restoreNav[i], StringComparison.OrdinalIgnoreCase));
                    if (next == null) break;
                    _history.Add(next);
                    cur = next;
                }
            }

            RefreshView();
            Status.Text =
                $"{Format.Size(root.Size)}  ·  {Format.Count(root.FileCount)} файлов  ·  " +
                $"{Format.Count(root.DirCount)} папок  ·  {_sw.Elapsed.TotalSeconds:0.0} с{Notes(stats)}";
        }
        catch (Exception ex)
        {
            Status.Text = "Ошибка: " + ex.Message;
        }
        finally
        {
            _timer.Stop();
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
            _scanning = false;
            SetControlsEnabled(true);
        }
    }

    void UpdateScanProgress()
    {
        if (_stats == null) return;
        Status.Text = $"Сканирую…  {Format.Count(_stats.Files)} файлов,  {Format.Size(_stats.Bytes)}";
    }

    static string Notes(ScanStats s)
    {
        var parts = new List<string>();
        if (s.Denied > 0) parts.Add($"{s.Denied} без доступа");
        if (s.Reparse > 0) parts.Add($"{s.Reparse} ссылок");
        if (s.Errors > 0) parts.Add($"{s.Errors} ошибок");
        return parts.Count == 0 ? "" : "   (пропущено: " + string.Join(", ", parts) + ")";
    }

    void SetControlsEnabled(bool on)
    {
        RefreshButton.IsEnabled = on;
        BrowseButton.IsEnabled = on;
        PathBox.IsEnabled = on;
        ReportButton.IsEnabled = on;
        CleanupButton.IsEnabled = on;
        UpButton.IsEnabled = on && _history.Count > 1;
    }

    // ======================= навигация =======================
    void DrillInto(DirNode node)
    {
        _history.Add(node);
        RefreshView();
    }

    void GoUp()
    {
        if (_history.Count > 1)
        {
            _history.RemoveAt(_history.Count - 1);
            RefreshView();
        }
    }

    void OnUp(object sender, RoutedEventArgs e) => GoUp();

    void GoToCrumb(int index)
    {
        if (index >= 0 && index < _history.Count - 1)
        {
            _history.RemoveRange(index + 1, _history.Count - index - 1);
            RefreshView();
        }
    }

    // ======================= построение вида =======================
    void RefreshView()
    {
        var folder = Current;
        if (folder == null) return;

        _entries = BuildEntries(folder);

        long max = _entries.Count > 0 ? _entries.Max(e => e.Node.Size) : 1;
        if (max < 1) max = 1;

        var slices = new List<Slice>(_entries.Count);
        var rows = new List<RowVM>(_entries.Count);
        int colorIdx = 0;
        foreach (var e in _entries)
        {
            Color col = e.Kind switch
            {
                Kind.Files => _dark ? Hex("#4A505C") : Hex("#C5CAD3"),
                Kind.Remainder => _dark ? Hex("#3A3F49") : Hex("#D6DAE1"),
                _ => TreemapView.ColorForIndex(colorIdx++, !_dark)
            };
            slices.Add(new Slice
            {
                Node = e.Node,
                Clickable = e.Clickable,
                Aggregate = e.Kind is Kind.Files or Kind.Remainder,
                Color = col
            });
            rows.Add(new RowVM
            {
                Node = e.Node,
                Name = e.Node.Name,
                SizeText = Format.Size(e.Node.Size),
                Swatch = new SolidColorBrush(col),
                BarWidth = Math.Max(3, (double)e.Node.Size / max * 150),
                Clickable = e.Clickable
            });
        }

        Treemap.SetSlices(slices);
        ChildList.ItemsSource = rows;
        BuildBreadcrumb();
        UpdateDetails(folder);
        UpButton.IsEnabled = _history.Count > 1;

        bool hasUnwraps = _unwrapped.TryGetValue(folder.Path, out var set) && set.Count > 0;
        ResetButton.Visibility = hasUnwraps ? Visibility.Visible : Visibility.Collapsed;
    }

    List<Entry> BuildEntries(DirNode folder)
    {
        var list = new List<Entry>();
        _unwrapped.TryGetValue(folder.Path, out var set);

        foreach (var child in folder.Children)
        {
            if (child.Size <= 0) continue;
            if (set != null && set.Contains(child.Path))
            {
                var r = Analysis.SmartUnwrap(child);
                foreach (var p in r.Promoted)
                    list.Add(new Entry { Node = p, Clickable = p.Children.Count > 0, Kind = Kind.Promoted });
                long rem = child.Size - r.UsedSize;
                if (rem > 0)
                    list.Add(new Entry
                    {
                        Node = new DirNode { Path = child.Path, Name = child.Name + " — прочее", Size = rem, UnwrapSource = child },
                        Clickable = false,
                        Kind = Kind.Remainder
                    });
            }
            else
            {
                list.Add(new Entry { Node = child, Clickable = child.Children.Count > 0, Kind = Kind.Folder });
            }
        }

        if (folder.OwnFileSize > 0)
            list.Add(new Entry
            {
                Node = new DirNode { Path = folder.Path, Name = "Файлы в этой папке", Size = folder.OwnFileSize },
                Clickable = false,
                Kind = Kind.Files
            });

        list.Sort((a, b) => b.Node.Size.CompareTo(a.Node.Size));
        return list;
    }

    // ======================= детали / список =======================
    void OnInspect(DirNode? node) => UpdateDetails(node ?? Current);

    void UpdateDetails(DirNode? node)
    {
        _inspectNode = node;

        if (node == null)
        {
            DetailName.Text = ""; DetailPath.Text = ""; DetailSize.Text = ""; DetailMeta.Text = "";
            UnwrapButton.Visibility = CollapseButton.Visibility = OpenButton.Visibility = Visibility.Collapsed;
            return;
        }

        DetailName.Text = node.Name;
        DetailPath.Text = node.Path;

        string pct = Current is { Size: > 0 } cur && !ReferenceEquals(node, cur)
            ? $"   ·   {Format.Percent(node.Size, cur.Size)} от текущей"
            : "";
        DetailSize.Text = Format.Size(node.Size) + pct;
        DetailMeta.Text = $"{Format.Count(node.FileCount)} файлов  ·  {Format.Count(node.DirCount)} папок";
        _inspectPath = node.Path;

        bool isDirectChild = Current != null && Current.Children.Contains(node);
        bool canUnwrap = isDirectChild && node.Children.Count > 0 && !IsUnwrapped(Current!, node);
        UnwrapButton.Visibility = canUnwrap ? Visibility.Visible : Visibility.Collapsed;
        CollapseButton.Visibility = node.UnwrapSource != null ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.Visibility = !string.IsNullOrEmpty(node.Path) && Directory.Exists(node.Path)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnListSelect(object sender, SelectionChangedEventArgs e)
    {
        if (ChildList.SelectedItem is RowVM r) UpdateDetails(r.Node);
    }

    void OnListActivate(object sender, MouseButtonEventArgs e)
    {
        if (ChildList.SelectedItem is RowVM r && r.Clickable) DrillInto(r.Node);
    }

    void BuildBreadcrumb()
    {
        Breadcrumb.Items.Clear();
        for (int i = 0; i < _history.Count; i++)
        {
            if (i > 0)
                Breadcrumb.Items.Add(new TextBlock
                {
                    Text = "  ›  ",
                    Foreground = (Brush)Application.Current.Resources["FgDim"],
                    VerticalAlignment = VerticalAlignment.Center
                });

            var node = _history[i];
            int index = i;
            var b = new Button
            {
                Content = i == 0 ? node.Path : node.Name,
                Style = (Style)FindResource("CrumbButton")
            };
            b.Click += (_, _) => GoToCrumb(index);
            Breadcrumb.Items.Add(b);
        }
    }

    // ======================= умная развёртка =======================
    bool IsUnwrapped(DirNode folder, DirNode child)
        => _unwrapped.TryGetValue(folder.Path, out var set) && set.Contains(child.Path);

    void Unwrap(DirNode folder, DirNode child)
    {
        if (!_unwrapped.TryGetValue(folder.Path, out var set))
            _unwrapped[folder.Path] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(child.Path);
        RefreshView();
    }

    void Collapse(DirNode folder, DirNode child)
    {
        if (_unwrapped.TryGetValue(folder.Path, out var set))
        {
            set.Remove(child.Path);
            if (set.Count == 0) _unwrapped.Remove(folder.Path);
        }
        RefreshView();
    }

    void OnUnwrapClick(object sender, RoutedEventArgs e)
    {
        if (Current != null && _inspectNode != null) Unwrap(Current, _inspectNode);
    }

    void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        if (Current != null && _inspectNode?.UnwrapSource != null) Collapse(Current, _inspectNode.UnwrapSource);
    }

    void OnResetUnwraps(object sender, RoutedEventArgs e)
    {
        if (Current != null) _unwrapped.Remove(Current.Path);
        RefreshView();
    }

    // правый клик по плитке — то же меню (альтернатива кнопкам)
    void OnTreemapContext(Slice slice)
    {
        try { BuildAndOpenContextMenu(slice); }
        catch (Exception ex) { App.Log(ex); Status.Text = "Ошибка меню: " + ex.Message; }
    }

    void BuildAndOpenContextMenu(Slice slice)
    {
        var node = slice.Node;
        var folder = Current;
        if (folder == null) return;

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = Treemap };

        bool isDirectChild = folder.Children.Contains(node);
        if (!slice.Aggregate && isDirectChild && node.Children.Count > 0 && !IsUnwrapped(folder, node))
        {
            var mi = new MenuItem { Header = "Умное добавление к расчётам" };
            mi.Click += (_, _) => Unwrap(folder, node);
            menu.Items.Add(mi);
        }

        if (node.UnwrapSource != null) // правый клик по плитке-остатку
        {
            var src = node.UnwrapSource;
            var mi = new MenuItem { Header = $"Свернуть обратно «{src.Name}»" };
            mi.Click += (_, _) => Collapse(folder, src);
            menu.Items.Add(mi);
        }

        if (!string.IsNullOrEmpty(node.Path) && Directory.Exists(node.Path))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var mi = new MenuItem { Header = "Открыть в проводнике" };
            mi.Click += (_, _) => OpenPath(node.Path);
            menu.Items.Add(mi);
        }

        if (menu.Items.Count == 0) return;
        menu.IsOpen = true;
    }

    // ======================= отчёт =======================
    void OnReport(object sender, RoutedEventArgs e)
    {
        var folder = Current;
        if (folder == null) { Status.Text = "Сначала отсканируй папку."; return; }

        // Виртуальный корень из эффективных записей (учитывает свёртки) — для текста и «топа».
        var vr = new DirNode
        {
            Path = folder.Path,
            Name = folder.Name,
            Size = folder.Size,
            OwnFileSize = folder.OwnFileSize,
            FileCount = folder.FileCount,
            DirCount = folder.DirCount
        };
        foreach (var en in _entries)
            if (en.Kind is Kind.Folder or Kind.Promoted or Kind.Remainder)
                vr.Children.Add(en.Node);

        var text = new StringBuilder();
        text.AppendLine(folder.Path);
        text.AppendLine($"Всего: {Format.Size(folder.Size)}   ({Format.Count(folder.FileCount)} файлов, {Format.Count(folder.DirCount)} папок)");
        text.AppendLine(new string('─', 56));
        text.AppendLine();
        text.Append(TextReport.Build(vr, showTree: true, treeDepth: 3, showTop: true, topN: 12));

        ShowVisualReport(folder, vr, text.ToString());
    }

    void ShowVisualReport(DirNode folder, DirNode vr, string plainText)
    {
        UIElement Build()
        {
        Brush Br(string key) => (Brush)Application.Current.Resources[key];
        Brush BG = Br("WinBg"), PANEL = Br("PanelBg"), FG = Br("Fg"), FGS = Br("FgStrong"),
              DIM = Br("FgDim"), TRACK = Br("Border"), ACC = Br("CrumbFg");

        long total = folder.Size <= 0 ? 1 : folder.Size;
        long max = _entries.Count > 0 ? Math.Max(1, _entries.Max(e => e.Node.Size)) : 1;

        // цвета как в treemap (чтобы отчёт и плитки совпадали)
        var colorOf = new Dictionary<DirNode, Color>();
        int ci = 0;
        foreach (var en in _entries)
            colorOf[en.Node] = en.Kind switch
            {
                Kind.Files => _dark ? Hex("#4A505C") : Hex("#C5CAD3"),
                Kind.Remainder => _dark ? Hex("#3A3F49") : Hex("#D6DAE1"),
                _ => TreemapView.ColorForIndex(ci++, !_dark)
            };

        var panel = new StackPanel { Margin = new Thickness(22) };

        // --- шапка ---
        panel.Children.Add(new TextBlock { Text = folder.Name, Foreground = FGS, FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = folder.Path, Foreground = DIM, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"{Format.Size(folder.Size)}   ·   {Format.Count(folder.FileCount)} файлов   ·   {Format.Count(folder.DirCount)} папок", Foreground = FG, FontSize = 13.5, Margin = new Thickness(0, 8, 0, 0) });
        if (_unwrapped.TryGetValue(folder.Path, out var uset) && uset.Count > 0)
            panel.Children.Add(new TextBlock { Text = $"Учтена умная развёртка ({uset.Count})", Foreground = ACC, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });

        // --- строители ---
        FrameworkElement Bar(long size, Color c)
        {
            var grid = new Grid { Width = 204, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = TRACK });
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(4, (double)size / max * 204) });
            return grid;
        }

        FrameworkElement MakeRow(string name, long size, Color c)
        {
            var g = new Grid { Height = 28 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

            var sw = new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sw, 0); g.Children.Add(sw);

            var nm = new TextBlock { Text = name, Foreground = FG, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 10, 0) };
            Grid.SetColumn(nm, 1); g.Children.Add(nm);

            var bar = Bar(size, c); Grid.SetColumn(bar, 2); g.Children.Add(bar);

            var sz = new TextBlock { Text = Format.Size(size), Foreground = FG, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 8, 0) };
            Grid.SetColumn(sz, 3); g.Children.Add(sz);

            var pc = new TextBlock { Text = Format.Percent(size, total), Foreground = DIM, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(pc, 4); g.Children.Add(pc);

            return g;
        }

        void AddHeader(string label, string right)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 18, 0, 6) };
            var r = new TextBlock { Text = right, Foreground = DIM, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
            DockPanel.SetDock(r, Dock.Right); dp.Children.Add(r);
            dp.Children.Add(new TextBlock { Text = label, Foreground = FGS, FontSize = 14.5, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(dp);
            panel.Children.Add(new Border { Height = 1, Background = TRACK, Margin = new Thickness(0, 0, 0, 4) });
        }

        static long Median(IReadOnlyList<long> sizes)
        {
            if (sizes.Count == 0) return 0;
            var sorted = sizes.OrderBy(x => x).ToArray();
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }

        // Группа: по умолчанию 4 строки, ссылка «Показать ещё N» разворачивает остальные.
        void AddGroup(string label, List<Entry> items)
        {
            if (items.Count == 0) return;
            long sum = items.Sum(e => e.Node.Size);
            long median = Median(items.Select(e => e.Node.Size).ToList());
            AddHeader(label,
                $"{Format.Count(items.Count)} {Format.Plural(items.Count, "папка", "папки", "папок")}  ·  " +
                $"{Format.Size(sum)}  ·  {Format.Percent(sum, total)}  ·  медиана {Format.Size(median)}");

            int show = Math.Min(items.Count, 4);
            for (int i = 0; i < show; i++)
                panel.Children.Add(MakeRow(items[i].Node.Name, items[i].Node.Size, colorOf[items[i].Node]));

            if (items.Count <= show) return;

            var rest = new StackPanel { Visibility = Visibility.Collapsed };
            for (int i = show; i < items.Count; i++)
                rest.Children.Add(MakeRow(items[i].Node.Name, items[i].Node.Size, colorOf[items[i].Node]));
            panel.Children.Add(rest);

            int hidden = items.Count - show;
            long hiddenSum = items.Skip(show).Sum(e => e.Node.Size);
            string collapsed = $"▾  Показать ещё {hidden} {Format.Plural(hidden, "папку", "папки", "папок")} (суммарно {Format.Size(hiddenSum)})";
            var link = new TextBlock
            {
                Text = collapsed,
                Foreground = ACC,
                FontSize = 12.5,
                Cursor = Cursors.Hand,
                Margin = new Thickness(18, 5, 0, 2)
            };
            link.MouseLeftButtonUp += (_, _) =>
            {
                bool open = rest.Visibility != Visibility.Visible;
                rest.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                link.Text = open ? "▴  Свернуть" : collapsed;
            };
            panel.Children.Add(link);
        }

        // --- группы по размеру ---
        const long GB = 1L << 30, TB = 1L << 40;
        var tiers = new (string label, long min, long max)[]
        {
            ("Гиганты (≥ 1 ТБ)", TB, long.MaxValue),
            ("Очень крупные (100 ГБ – 1 ТБ)", 100 * GB, TB),
            ("Крупные (10 – 100 ГБ)", 10 * GB, 100 * GB),
            ("Средние (1 – 10 ГБ)", GB, 10 * GB),
        };
        var folders = _entries.Where(e => e.Kind != Kind.Files).ToList();

        foreach (var (label, min, mx) in tiers)
            AddGroup(label, folders.Where(e => e.Node.Size >= min && e.Node.Size < mx).ToList());

        AddGroup("Мелкие (< 1 ГБ)", folders.Where(e => e.Node.Size < GB).ToList());

        var files = _entries.FirstOrDefault(e => e.Kind == Kind.Files);
        if (files != null)
        {
            AddHeader("Файлы в этой папке", $"{Format.Size(files.Node.Size)}  ·  {Format.Percent(files.Node.Size, total)}");
            panel.Children.Add(MakeRow(files.Node.Name, files.Node.Size, colorOf[files.Node]));
        }

        // --- крупнейшие папки на любой глубине ---
        var all = new List<DirNode>();
        var stack = new Stack<DirNode>();
        foreach (var c in vr.Children) if (c.UnwrapSource == null) stack.Push(c);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            all.Add(n);
            foreach (var ch in n.Children) stack.Push(ch);
        }
        var top = all.OrderByDescending(n => n.Size).Take(12).ToList();
        if (top.Count > 0)
        {
            AddHeader("Крупнейшие папки (на любой глубине)", "");
            int i = 0;
            foreach (var n in top)
            {
                string rel = Path.GetRelativePath(folder.Path, n.Path);
                panel.Children.Add(MakeRow(rel, n.Size, TreemapView.ColorForIndex(i++, !_dark)));
            }
        }

        // --- окно ---
        var sv = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = BG
        };

        var copy = new Button { Content = "Копировать текст", Margin = new Thickness(12), Style = (Style)Application.Current.FindResource("DialogButton") };
        copy.Click += (_, _) => { try { Clipboard.SetText(plainText); } catch { } };
        var barRow = new Border
        {
            Background = PANEL,
            BorderBrush = TRACK,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy } }
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(sv, 0); grid.Children.Add(sv);
        Grid.SetRow(barRow, 1); grid.Children.Add(barRow);
        return grid;
        }

        var win = new Window
        {
            Title = $"Отчёт — {folder.Name}",
            Owner = this,
            Icon = Icon,
            Width = 760,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        win.Background = (Brush)Application.Current.Resources["WinBg"];
        win.Content = Build();
        ShowThemedWindow(win, () =>
        {
            win.Background = (Brush)Application.Current.Resources["WinBg"];
            win.Content = Build();
        });
    }

    // ======================= что удалить =======================
    void OnCleanup(object sender, RoutedEventArgs e)
    {
        if (Current == null) { Status.Text = "Сначала отсканируй папку."; return; }
        ShowCleanupWindow(Current);
    }

    static long? TryGetFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    void ShowCleanupWindow(DirNode folder)
    {
        var candidates = _entries
            .Where(en => en.Kind is Kind.Folder or Kind.Promoted)
            .Select(en => en.Node)
            .Where(n => n.Size > 0 && !string.IsNullOrEmpty(n.Path))
            .OrderByDescending(n => n.Size)
            .ToList();
        long totalCand = candidates.Sum(n => n.Size);
        long? free = TryGetFreeSpace(folder.Path);
        string driveLetter = (Path.GetPathRoot(folder.Path) ?? "").TrimEnd('\\').TrimEnd(':'); // напр. "D"
        string driveSuffix = driveLetter.Length == 0 ? "" : $" (диск {driveLetter})";
        string driveOn = driveLetter.Length == 0 ? "на диске" : $"на диске {driveLetter}";
        string lastAmount = "100 ГБ"; // сохраняется между перестроениями (смена темы)

        UIElement Build()
        {
        Brush Br(string key) => (Brush)Application.Current.Resources[key];
        Brush BG = Br("WinBg"), PANEL = Br("PanelBg"), FG = Br("Fg"), FGS = Br("FgStrong"),
              DIM = Br("FgDim"), TRACK = Br("Border"), ACC = Br("CrumbFg");
        Brush OK = new SolidColorBrush(_dark ? Hex("#5BD18B") : Hex("#1E9E57"));

        // Редактируемый список: можно выбрать готовый объём или вписать свой («75 ГБ», «1 ТБ»).
        var amountBox = new ComboBox
        {
            IsEditable = true, IsTextSearchEnabled = false, Text = lastAmount, Width = 130, FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        foreach (var preset in new[] { "50 ГБ", "100 ГБ", "200 ГБ", "300 ГБ", "500 ГБ", "1 ТБ" })
            amountBox.Items.Add(preset);
        amountBox.Style = (Style)Application.Current.FindResource("ThemedComboBox");
        amountBox.ItemContainerStyle = (Style)Application.Current.FindResource("ThemedComboItem");
        var go = new Button { Content = "Подобрать", Margin = new Thickness(10, 0, 0, 0), Style = (Style)Application.Current.FindResource("DialogButton") };

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        inputRow.Children.Add(new TextBlock { Text = $"Желаемый свободный объём{driveSuffix}:", Foreground = FG, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        inputRow.Children.Add(amountBox);
        inputRow.Children.Add(go);

        var top = new StackPanel { Margin = new Thickness(20, 18, 20, 10) };
        top.Children.Add(new TextBlock { Text = "Что удалить, чтобы освободить место", Foreground = FGS, FontSize = 18, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Программа ничего не удаляет — только подсказывает. Удаляй сам в проводнике.", Foreground = DIM, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (free.HasValue)
        {
            var freeBlock = new TextBlock { FontSize = 15, Margin = new Thickness(0, 12, 0, 0) };
            freeBlock.Inlines.Add(new System.Windows.Documents.Run($"Сейчас свободно {driveOn}:  ") { Foreground = FG });
            freeBlock.Inlines.Add(new System.Windows.Documents.Run(Format.Size(free.Value)) { Foreground = OK, FontWeight = FontWeights.Bold, FontSize = 16 });
            top.Children.Add(freeBlock);
        }
        else
        {
            top.Children.Add(new TextBlock { Text = "Свободное место диска определить не удалось — введённое число трактуется как «сколько освободить».", Foreground = DIM, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
        top.Children.Add(inputRow);

        var results = new StackPanel { Margin = new Thickness(20, 8, 20, 18) };

        FrameworkElement RowOf(DirNode f)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = f.Name, Foreground = FG, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            left.Children.Add(new TextBlock { Text = f.Path, Foreground = DIM, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(left, 0); g.Children.Add(left);

            var sz = new TextBlock { Text = Format.Size(f.Size), Foreground = FG, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(sz, 1); g.Children.Add(sz);

            var open = new TextBlock { Text = "Открыть в проводнике", Foreground = ACC, FontSize = 11.5, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), ToolTip = "Открыть проводник и выделить эту папку" };
            open.MouseLeftButtonUp += (_, _) => OpenInExplorerSelect(f.Path);
            Grid.SetColumn(open, 2); g.Children.Add(open);

            return g;
        }

        static void OpenAll(List<DirNode> folders)
        {
            if (folders.Count == 0) return;
            if (folders.Count > 5)
            {
                var r = MessageBox.Show(
                    $"Откроется {folders.Count} окон проводника — это довольно много. Продолжить?",
                    "Открыть все", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            foreach (var f in folders) OpenInExplorerSelect(f.Path);
        }

        FrameworkElement Card(string title, string subtitle, Analysis.CleanupPlan plan, long need)
        {
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = title, Foreground = FGS, FontSize = 14.5, FontWeight = FontWeights.SemiBold });
            inner.Children.Add(new TextBlock { Text = subtitle, Foreground = DIM, FontSize = 12, Margin = new Thickness(0, 1, 0, 0) });

            if (plan.Folders.Count == 0)
            {
                inner.Children.Add(new TextBlock { Text = "Подходящих папок такого размера здесь нет.", Foreground = DIM, FontSize = 12, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 8, 0, 0) });
            }
            else
            {
                var summary = new TextBlock { FontSize = 13, Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap };
                summary.Inlines.Add(new System.Windows.Documents.Run($"Удалить {Format.Count(plan.Folders.Count)} {Format.Plural(plan.Folders.Count, "папку", "папки", "папок")}  ·  освободит {Format.Size(plan.Freed)}") { Foreground = FG });
                if (free.HasValue)
                    summary.Inlines.Add(new System.Windows.Documents.Run($"  ·  станет свободно ≈ {Format.Size(free.Value + plan.Freed)}") { Foreground = DIM });
                summary.Inlines.Add(new System.Windows.Documents.Run(plan.ReachedTarget ? "   ✓ цель достигнута" : $"   не хватает {Format.Size(Math.Max(0, need - plan.Freed))}") { Foreground = plan.ReachedTarget ? OK : DIM });
                inner.Children.Add(summary);

                const int cap = 12;
                foreach (var f in plan.Folders.Take(cap)) inner.Children.Add(RowOf(f));
                if (plan.Folders.Count > cap)
                {
                    long restSum = plan.Folders.Skip(cap).Sum(f => f.Size);
                    int more = plan.Folders.Count - cap;
                    inner.Children.Add(new TextBlock { Text = $"… ещё {Format.Count(more)} {Format.Plural(more, "папка", "папки", "папок")} (суммарно {Format.Size(restSum)})", Foreground = DIM, FontSize = 11.5, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0) });
                }

                var openAll = new TextBlock { Text = $"Открыть все в проводнике ({plan.Folders.Count})", Foreground = ACC, FontSize = 12, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Margin = new Thickness(0, 12, 0, 0), ToolTip = "Откроет окно проводника для каждой папки плана" };
                openAll.MouseLeftButtonUp += (_, _) => OpenAll(plan.Folders);
                inner.Children.Add(openAll);
            }

            return new Border { Background = PANEL, BorderBrush = TRACK, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 14), Child = inner };
        }

        static bool TryParseAmount(string? s, out long bytes)
        {
            bytes = 0;
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return false;

            long mul = 1L << 30; // ГБ по умолчанию
            (string suffix, long m)[] units =
            {
                ("тб", 1L << 40), ("tb", 1L << 40),
                ("гб", 1L << 30), ("gb", 1L << 30),
                ("мб", 1L << 20), ("mb", 1L << 20),
            };
            foreach (var (suffix, m) in units)
                if (s.EndsWith(suffix)) { mul = m; s = s[..^suffix.Length].Trim(); break; }

            if (!double.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double val) || val <= 0)
                return false;
            bytes = (long)(val * mul);
            return bytes > 0;
        }

        void Recompute()
        {
            lastAmount = amountBox.Text;
            results.Children.Clear();

            if (!TryParseAmount(amountBox.Text, out long targetBytes))
            {
                results.Children.Add(new TextBlock { Text = "Введи объём, например «100 ГБ» или просто «100».", Foreground = DIM, FontSize = 13 });
                return;
            }
            long need = free.HasValue ? targetBytes - free.Value : targetBytes;

            if (free.HasValue)
                results.Children.Add(new TextBlock { Text = $"Цель: {Format.Size(targetBytes)} свободно  ·  нужно освободить ещё {Format.Size(Math.Max(0, need))}", Foreground = FG, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) });

            if (need <= 0)
            {
                results.Children.Add(new TextBlock { Text = "Уже свободно достаточно — удалять ничего не нужно.", Foreground = FG, FontSize = 13.5 });
                return;
            }
            if (candidates.Count == 0)
            {
                results.Children.Add(new TextBlock { Text = "В этой папке нет папок-кандидатов для удаления.", Foreground = DIM, FontSize = 13 });
                return;
            }
            if (need > totalCand)
                results.Children.Add(new TextBlock { Text = $"Запрошено больше, чем занимают все папки здесь ({Format.Size(totalCand)}). Даже удалив всё, столько не освободить.", Foreground = DIM, FontSize = 12, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });

            results.Children.Add(Card("Вариант 1 — минимум папок", "одна-две папки, чтобы закрыть цель сразу", Analysis.PlanClosestFew(candidates, need), need));
            results.Children.Add(Card("Вариант 2 — несколько средних", "папки среднего размера", Analysis.PlanGreedyCapped(candidates, need, Math.Max(1, need / 2)), need));
            results.Children.Add(Card("Вариант 3 — много мелких", "много небольших папок, не трогая крупные", Analysis.PlanGreedyCapped(candidates, need, Math.Max(1, need / 15)), need));
        }

        go.Click += (_, _) => Recompute();
        amountBox.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) Recompute(); };
        amountBox.SelectionChanged += (_, _) => Recompute();
        Recompute();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var topBorder = new Border { Background = PANEL, BorderBrush = TRACK, BorderThickness = new Thickness(0, 0, 0, 1), Child = top };
        Grid.SetRow(topBorder, 0); grid.Children.Add(topBorder);
        var sv = new ScrollViewer { Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = BG };
        Grid.SetRow(sv, 1); grid.Children.Add(sv);
        return grid;
        }

        var win = new Window
        {
            Title = $"Что удалить — {folder.Name}",
            Owner = this,
            Icon = Icon,
            Width = 720,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        win.Background = (Brush)Application.Current.Resources["WinBg"];
        win.Content = Build();
        ShowThemedWindow(win, () =>
        {
            win.Background = (Brush)Application.Current.Resources["WinBg"];
            win.Content = Build();
        });
    }

    // ======================= прочий ввод =======================
    void OnOpenInExplorer(object sender, RoutedEventArgs e) => OpenPath(_inspectPath);

    static void OpenPath(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }

    // Открывает проводник и выделяет папку (удобно, чтобы сразу её удалить).
    static void OpenInExplorerSelect(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path.TrimEnd('\\')}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { }
    }

    // ======================= о программе =======================
    void OnCredit(object sender, MouseButtonEventArgs e) => ShowAbout();

    void ShowAbout()
    {
        UIElement Build()
        {
            Brush Br(string k) => (Brush)Application.Current.Resources[k];
            Brush FG = Br("Fg"), FGS = Br("FgStrong"), DIM = Br("FgDim"), ACC = Br("CrumbFg");
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

            var panel = new StackPanel { Margin = new Thickness(26) };

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Image { Source = Icon, Width = 56, Height = 56, VerticalAlignment = VerticalAlignment.Center });
            var titleCol = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            titleCol.Children.Add(new TextBlock { Text = "SpaceSaver", Foreground = FGS, FontSize = 22, FontWeight = FontWeights.SemiBold });
            titleCol.Children.Add(new TextBlock { Text = "Анализатор занятого места на диске", Foreground = DIM, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
            header.Children.Add(titleCol);
            panel.Children.Add(header);

            panel.Children.Add(new TextBlock { Text = $"Версия {v?.Major ?? 1}.{v?.Minor ?? 0}", Foreground = DIM, FontSize = 12.5, Margin = new Thickness(0, 18, 0, 0) });
            panel.Children.Add(new TextBlock { Text = "Автор: Chernyshelly", Foreground = FG, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });

            var link = new TextBlock
            {
                Text = "github.com/ChernyshellyOfficial",
                Foreground = ACC,
                FontSize = 13.5,
                Cursor = Cursors.Hand,
                TextDecorations = TextDecorations.Underline,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = GithubUrl
            };
            link.MouseLeftButtonUp += (_, _) => OpenUrl(GithubUrl);
            panel.Children.Add(link);

            panel.Children.Add(new TextBlock { Text = "Сделано на C# / WPF (.NET 9).", Foreground = DIM, FontSize = 11.5, Margin = new Thickness(0, 18, 0, 0) });

            return panel;
        }

        var win = new Window
        {
            Title = "О программе",
            Owner = this,
            Icon = Icon,
            Width = 440,
            Height = 290,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        win.Background = (Brush)Application.Current.Resources["WinBg"];
        win.Content = Build();
        ShowThemedWindow(win, () =>
        {
            win.Background = (Brush)Application.Current.Resources["WinBg"];
            win.Content = Build();
        });
    }

    async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Выбери папку для анализа" };
        if (!string.IsNullOrWhiteSpace(PathBox.Text) && Directory.Exists(PathBox.Text))
            dlg.InitialDirectory = PathBox.Text;
        if (dlg.ShowDialog() == true)
        {
            PathBox.Text = dlg.FolderName;
            await StartScan(dlg.FolderName);
        }
    }

    void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back && Keyboard.FocusedElement is not TextBox)
        {
            GoUp();
            e.Handled = true;
        }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            PathBox.Text = paths[0];
            await StartScan(paths[0]);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.FocusedElement == PathBox && !_scanning)
        {
            _ = StartScan(PathBox.Text);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}

/// <summary>Строка списка подпапок.</summary>
public sealed class RowVM
{
    public DirNode Node { get; set; } = null!;
    public string Name { get; set; } = "";
    public string SizeText { get; set; } = "";
    public Brush Swatch { get; set; } = Brushes.Gray;
    public double BarWidth { get; set; }
    public bool Clickable { get; set; }
}

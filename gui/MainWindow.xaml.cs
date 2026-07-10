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
    enum Kind { Folder, Promoted, Remainder, File, FilesTail }

    sealed class Entry
    {
        public required DirNode Node;
        public bool Clickable;
        public Kind Kind;
        public string? Display;   // подпись вместо Node.Name (имя игры/программы), если задана
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
    bool _syncSel;                                                 // защита от зацикливания синхронизации выбора treemap↔список
    bool _global;                                                  // включён «глобальный вид»
    DirNode? _globalRoot;                                          // корень глобального скана (дерево кешируется в памяти)
    bool _globalUseDbs = true;                                     // использовать базы имён (реестр/Steam/Chocolatey) в глобальном скане
    bool _pendingGlobal;                                           // после скана войти в глобальный вид
    readonly List<(Window Window, Action Retheme)> _childWindows = new(); // открытые окна Отчёт/Что удалить
    const string GithubUrl = "https://github.com/ChernyshellyOfficial";

    // Иконки для списка: папка и документ (в системе координат ~14×14).
    static readonly Geometry FolderGlyphGeo = FrozenGeo("M1,3 L5,3 L6.5,5 L13,5 L13,12 L1,12 Z");
    static readonly Geometry FileGlyphGeo = FrozenGeo("M2.5,1 L8.5,1 L12,4.5 L12,13 L2.5,13 Z");
    static Geometry FrozenGeo(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }

    DirNode? Current => _history.Count > 0 ? _history[^1] : null;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => UpdateScanProgress();

        Treemap.Inspect += OnInspect;
        Treemap.Drill += DrillInto;
        Treemap.FileActivated += node => OpenInExplorerSelect(node.Path);
        Treemap.ContextRequested += OnTreemapContext;

        // Настройки прошлого запуска: тема и последняя папка.
        Settings.Load();
        _dark = Settings.Dark;
        ApplyTheme();
        PathBox.Text = !string.IsNullOrWhiteSpace(Settings.LastPath) && Directory.Exists(Settings.LastPath)
            ? Settings.LastPath! : DefaultPath();
        Status.Text = "Выбери папку и нажми «Обзор…» — скан начнётся сразу. Можно перетащить папку в окно.";

        // Прогреваем базы (имена игр + программ + реестр) в фоне, чтобы глобальный скан не подтормаживал.
        Task.Run(() => { GameDb.Ensure(); ProgramDb.Ensure(); _ = InstalledPrograms.Count; });

        Closing += (_, _) => Settings.Save();
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
        Settings.Dark = _dark; Settings.Save();
        ApplyTheme();
        ApplyDarkTitleBar(this, _dark);
        foreach (var (w, retheme) in _childWindows.ToList())
        {
            ApplyDarkTitleBar(w, _dark);
            retheme();
        }
        if (_global && _globalRoot != null) EnterGlobalView(_globalRoot);
        else if (Current != null) RefreshView();
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
            Set("ScrollThumb", "#39414F"); Set("ScrollThumbHover", "#565F73");
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
            Set("ScrollThumb", "#C6CBD4"); Set("ScrollThumbHover", "#AAB1BD");
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

    // «Глобальный скан» — полный скан + раскладка всего значимого в один вид.
    async void OnGlobalScan(object sender, RoutedEventArgs e)
    {
        var path = (PathBox.Text ?? "").Trim().Trim('"');
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Status.Text = $"Папка не найдена: {path}";
            return;
        }
        if (!ConfirmGlobalScan(path)) return;

        _pendingGlobal = true;
        await StartScan(path);
    }

    // Диалог подтверждения глобального скана с галочкой «узнавать по базам имён».
    bool ConfirmGlobalScan(string path)
    {
        Brush Br(string k) => (Brush)Application.Current.Resources[k];

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = "Глобальный скан", Foreground = Br("FgStrong"),
            FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Полностью просканирует «{path}» и разложит всё значимое — игры, программы, тяжёлые файлы — " +
                   "в один общий вид, как бы глубоко они ни лежали.\n\nДля целого диска это может занять заметно больше времени, чем обычный просмотр.",
            Foreground = Br("Fg"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        });

        var chk = new CheckBox
        {
            Content = "Узнавать игры и программы по базам имён (реестр, Steam, Chocolatey)",
            IsChecked = _globalUseDbs,
            Style = (Style)Application.Current.FindResource("ThemedCheckBox")
        };
        panel.Children.Add(chk);
        panel.Children.Add(new TextBlock
        {
            Text = "Сними галочку, если в реестре что-то помечено неверно — тогда раскладка будет чисто по структуре папок.",
            Foreground = Br("FgDim"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(26, 4, 0, 18)
        });

        var win = new Window
        {
            Title = "Глобальный скан", Owner = this, Icon = Icon, Width = 500,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Br("WinBg")
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", Style = (Style)Application.Current.FindResource("DialogButton"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Сканировать", Style = (Style)Application.Current.FindResource("DialogButton"), MinWidth = 120 };
        cancel.Click += (_, _) => win.DialogResult = false;
        ok.Click += (_, _) => win.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        win.Content = panel;
        win.SourceInitialized += (_, _) => ApplyDarkTitleBar(win, _dark);
        bool proceed = win.ShowDialog() == true;
        if (proceed) _globalUseDbs = chk.IsChecked == true;
        return proceed;
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
        _globalRoot = null;   // прежний кеш глобального скана больше не актуален (для глобального его пере-выставит EnterGlobalView)
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
            var root = await Task.Run(() => Scanner.ScanRoot(dir, stats, collectFiles: true));
            _sw.Stop();
            Settings.LastPath = path; Settings.Save();   // запоминаем последнюю успешно отсканированную папку

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

            if (_pendingGlobal) EnterGlobalView(root);
        }
        catch (Exception ex)
        {
            Status.Text = "Ошибка: " + ex.Message;
        }
        finally
        {
            _pendingGlobal = false;
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
        GlobalButton.IsEnabled = on;
        PathBox.IsEnabled = on;
        ReportButton.IsEnabled = on;
        FilesButton.IsEnabled = on;
        CleanupButton.IsEnabled = on;
        UpButton.IsEnabled = on && _history.Count > 1;
    }

    // ======================= навигация =======================
    void DrillInto(DirNode node)
    {
        if (_global) { ExitGlobalTo(node); return; }
        _history.Add(node);
        RefreshView();
    }

    void GoUp()
    {
        if (_global) { _global = false; RefreshView(); return; }
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
        _global = false;
        var folder = Current;
        if (folder == null) return;

        _entries = BuildEntries(folder);
        RenderEntries();
        BuildBreadcrumb();
        UpdateDetails(folder, false);
        UpButton.IsEnabled = _history.Count > 1;

        bool hasUnwraps = _unwrapped.TryGetValue(folder.Path, out var set) && set.Count > 0;
        ResetButton.Visibility = hasUnwraps ? Visibility.Visible : Visibility.Collapsed;
    }

    // Строит плитки и строки из текущих _entries (общий код для обычного и глобального вида).
    void RenderEntries()
    {
        long max = _entries.Count > 0 ? _entries.Max(e => e.Node.Size) : 1;
        if (max < 1) max = 1;

        var slices = new List<Slice>(_entries.Count);
        var rows = new List<RowVM>(_entries.Count);
        int colorIdx = 0;
        foreach (var e in _entries)
        {
            bool isFile = e.Kind is Kind.File or Kind.FilesTail;
            Color col = e.Kind switch
            {
                Kind.File => _dark ? Hex("#5E6E8C") : Hex("#A9B6CC"),
                Kind.FilesTail => _dark ? Hex("#454E5E") : Hex("#C7CDD8"),
                Kind.Remainder => _dark ? Hex("#3A3F49") : Hex("#D6DAE1"),
                _ => TreemapView.ColorForIndex(colorIdx++, !_dark)
            };
            slices.Add(new Slice
            {
                Node = e.Node,
                Clickable = e.Clickable,
                Aggregate = e.Kind is Kind.Remainder or Kind.FilesTail,
                IsFile = isFile,
                Color = col,
                Label = e.Display
            });
            rows.Add(new RowVM
            {
                Node = e.Node,
                Name = e.Display ?? e.Node.Name,
                SizeText = Format.Size(e.Node.Size),
                Swatch = new SolidColorBrush(col),
                BarWidth = Math.Max(3, (double)e.Node.Size / max * 150),
                Clickable = e.Clickable,
                IsFile = isFile,
                Aggregate = e.Kind is Kind.Remainder or Kind.FilesTail,
                Glyph = isFile ? FileGlyphGeo : FolderGlyphGeo
            });
        }

        Treemap.SetSlices(slices);
        ChildList.ItemsSource = rows;
    }

    // ======================= глобальный вид =======================

    // Резолвер «известной единицы» для GlobalExplode: сперва программа по пути установки
    // (реестр, с правильным именем), затем известная игра по имени папки.
    static Func<DirNode, string?> UnitResolver()
    {
        var gi = GameDb.Ensure();
        var pi = ProgramDb.Ensure();
        return node =>
        {
            bool isGame = gi.Matches(node.Name);
            // 1) установленная программа по пути (реестр) — с правильным именем;
            //    но игровые библиотеки (Steam: внутри есть steamapps) НЕ сворачиваем одной
            //    плиткой — их надо разложить на игры, которые дальше сами узнаются.
            var prog = InstalledPrograms.Resolve(node.Path);
            if (prog != null && !IsGameLibrary(node))
            {
                // Если папка — известная игра, а имя из реестра к ней не относится
                // (мод/русификатор/патч, прописавший себя на папку игры) — показываем игру.
                if (isGame && !NamesRelated(prog, node.Name)) return node.Name;
                return prog;
            }
            // 2) известная игра по имени папки; 3) известная программа по имени папки.
            if (isGame) return node.Name;
            if (pi.Matches(node.Name)) return node.Name;
            return null;
        };
    }

    // Имена «про одно и то же»: нормализованное более короткое — префикс более длинного
    // (напр. «Black Myth: Wukong» ↔ папка «BlackMythWukong»). Русификатор к игре не относится.
    static bool NamesRelated(string a, string b)
    {
        var na = Analysis.GameNameIndex.Normalize(a);
        var nb = Analysis.GameNameIndex.Normalize(b);
        if (na.Length < 6 || nb.Length < 6) return false;
        var (shorter, longer) = na.Length <= nb.Length ? (na, nb) : (nb, na);
        return longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    // Папка — игровая библиотека (её надо разворачивать в игры, а не показывать целиком),
    // если внутри есть подпапка steamapps (Steam-клиент и вторичные Steam-библиотеки).
    static bool IsGameLibrary(DirNode node)
    {
        foreach (var c in node.Children)
            if (string.Equals(c.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    void EnterGlobalView(DirNode root)
    {
        _global = true;
        _globalRoot = root;
        // Глобальный вид всегда «стоит» на корне скана — синхронизируем историю навигации,
        // иначе после «← Глобальный вид» кнопка «↑» уводила бы в глубокую папку (стейл _history).
        _history.Clear();
        _history.Add(root);

        Func<DirNode, string?>? resolver = _globalUseDbs ? UnitResolver() : null;
        var items = Analysis.GlobalExplode(root, null, resolver);
        _entries = BuildGlobalEntries(root, items);
        RenderEntries();
        BuildGlobalBreadcrumb(root);
        UpdateDetails(root, false);
        UpButton.IsEnabled = true;
        ResetButton.Visibility = Visibility.Collapsed;

        int folders = items.Count(i => !i.IsFile);
        int fileItems = items.Count(i => i.IsFile);
        string db = _globalUseDbs && GameDb.Ensure().Count > 0 ? $" · база игр: {Format.Count(GameDb.Ensure().Count)}" : "";
        string pr = _globalUseDbs && InstalledPrograms.Count > 0 ? $" · программ: {Format.Count(InstalledPrograms.Count)}" : "";
        string noDb = _globalUseDbs ? "" : " · базы имён выкл.";
        Status.Text = $"Глобальный вид: {Format.Count(folders)} папок-единиц + {Format.Count(fileItems)} тяжёлых файлов в {Format.Size(root.Size)}{db}{pr}{noDb}";
    }

    List<Entry> BuildGlobalEntries(DirNode root, List<Analysis.ExplodeItem> items)
    {
        var list = new List<Entry>();
        const int cap = 400;
        int shown = Math.Min(items.Count, cap);
        long shownSum = 0;
        for (int i = 0; i < shown; i++)
        {
            var it = items[i];
            shownSum += it.Size;
            if (it.IsFile)
                list.Add(new Entry { Node = new DirNode { Path = it.Path, Name = it.Name, Size = it.Size }, Clickable = false, Kind = Kind.File });
            else
                // it.Name может быть «красивым» именем (программа из реестра) — показываем его как подпись,
                // но Node оставляем настоящим (для перехода/деталей/проводника).
                list.Add(new Entry { Node = it.Node!, Clickable = true, Kind = Kind.Folder, Display = it.Name });
        }

        long rest = root.Size - shownSum; // всё несущественное + переполнение сверх cap
        if (rest > 0)
        {
            string name = items.Count > shown
                ? $"… ещё {Format.Count(items.Count - shown)} + мелочь"
                : "Мелкие файлы и прочее";
            list.Add(new Entry { Node = new DirNode { Path = root.Path, Name = name, Size = rest }, Clickable = false, Kind = Kind.FilesTail });
        }

        list.Sort((a, b) => b.Node.Size.CompareTo(a.Node.Size));
        return list;
    }

    void BuildGlobalBreadcrumb(DirNode root)
    {
        Breadcrumb.Items.Clear();
        var back = new Button { Content = "← Обычный вид", Style = (Style)FindResource("CrumbButton") };
        back.Click += (_, _) => { _global = false; RefreshView(); };
        Breadcrumb.Items.Add(back);
        Breadcrumb.Items.Add(new TextBlock
        {
            Text = "    Глобальный вид · " + root.Path,
            Foreground = (Brush)Application.Current.Resources["FgDim"],
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    void ExitGlobalTo(DirNode node)
    {
        // строим историю по цепочке родителей (root → … → node)
        var chain = new List<DirNode>();
        for (var n = node; n != null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        _global = false;
        _history.Clear();
        _history.AddRange(chain);
        RefreshView();
    }

    // «Показать содержимое» сводной плитки: раскрываем более мелкие элементы,
    // не попавшие в основной вид (не входящие в уже показанные единицы).
    void ShowRemainderContents(DirNode root)
    {
        var surfacedFolders = _entries
            .Where(e => e.Kind == Kind.Folder && !string.IsNullOrEmpty(e.Node.Path))
            .Select(e => e.Node.Path).ToList();
        var surfacedFiles = new HashSet<string>(
            _entries.Where(e => e.Kind == Kind.File).Select(e => e.Node.Path),
            StringComparer.OrdinalIgnoreCase);

        bool UnderSurfaced(string p)
        {
            if (surfacedFiles.Contains(p)) return true;
            foreach (var sf in surfacedFolders)
            {
                if (p.Equals(sf, StringComparison.OrdinalIgnoreCase)) return true;
                string prefix = sf.EndsWith("\\") ? sf : sf + "\\";
                if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        long lowerMin = Math.Max(4L << 20, root.Size / 20000);   // порог мельче основного
        Func<DirNode, string?>? resolver = _globalUseDbs ? UnitResolver() : null;
        var rest = Analysis.GlobalExplode(root, lowerMin, resolver)
            .Where(it => !UnderSurfaced(it.Path))
            .OrderByDescending(it => it.Size)
            .ToList();

        long tileTotal = _entries.Where(e => e.Kind is Kind.FilesTail).Sum(e => e.Node.Size);
        ShowItemsWindow("Мелкие файлы и прочее — что внутри", root, rest, tileTotal, lowerMin);
    }

    void ShowItemsWindow(string title, DirNode root, List<Analysis.ExplodeItem> items, long aggregateTotal = 0, long tailThreshold = 0)
    {
        long total = items.Sum(i => i.Size);
        long tail = aggregateTotal > total ? aggregateTotal - total : 0; // не поместившийся «хвост» совсем мелких
        long maxItem = items.Count > 0 ? items[0].Size : 1;
        if (maxItem < 1) maxItem = 1;
        string Rel(string p) => p.Length > root.Path.Length ? Path.GetRelativePath(root.Path, p) : p;

        var plain = new StringBuilder();
        plain.AppendLine(title);
        plain.AppendLine(root.Path);
        plain.AppendLine(new string('─', 56));
        foreach (var it in items.Take(500))
            plain.AppendLine($"  {Format.Size(it.Size),12}   {(it.IsFile ? "" : "[папка] ")}{Rel(it.Path)}");
        if (tail > 0)
            plain.AppendLine($"  {Format.Size(tail),12}   (множество файлов мельче {Format.Size(tailThreshold)} — поштучно не показаны)");

        UIElement Build()
        {
            Brush Br(string k) => (Brush)Application.Current.Resources[k];
            Brush FG = Br("Fg"), FGS = Br("FgStrong"), DIM = Br("FgDim"), TRACK = Br("Border"), PANEL = Br("PanelBg"), ACC = Br("CrumbFg");

            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = title, Foreground = FGS, FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            string head = aggregateTotal > total
                ? $"{Format.Count(items.Count)} элементов  ·  показано поштучно {Format.Size(total)} из {Format.Size(aggregateTotal)}"
                : $"{Format.Count(items.Count)} элементов  ·  {Format.Size(total)}";
            panel.Children.Add(new TextBlock { Text = head, Foreground = FG, FontSize = 13.5, Margin = new Thickness(0, 6, 0, 12) });

            if (items.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "Здесь нет ничего крупного для показа поштучно — только совсем мелкие файлы.", Foreground = DIM, FontSize = 13 });
            }
            else
            {
                const int cap = 400;
                for (int i = 0; i < Math.Min(items.Count, cap); i++)
                {
                    var it = items[i];
                    Color c = it.IsFile ? (_dark ? Hex("#5E6E8C") : Hex("#A9B6CC")) : TreemapView.ColorForIndex(i, !_dark);

                    var g = new Grid { Height = 26 };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var glyph = new System.Windows.Shapes.Path { Data = it.IsFile ? FileGlyphGeo : FolderGlyphGeo, Fill = new SolidColorBrush(c), Width = 13, Height = 13, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(glyph, 0); g.Children.Add(glyph);

                    var nm = new TextBlock { Text = Rel(it.Path), Foreground = FG, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 10, 0) };
                    Grid.SetColumn(nm, 1); g.Children.Add(nm);

                    var barGrid = new Grid { Width = 160, Height = 7, VerticalAlignment = VerticalAlignment.Center };
                    barGrid.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = TRACK });
                    barGrid.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(3, (double)it.Size / maxItem * 160) });
                    Grid.SetColumn(barGrid, 2); g.Children.Add(barGrid);

                    var sz = new TextBlock { Text = Format.Size(it.Size), Foreground = FG, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 8, 0) };
                    Grid.SetColumn(sz, 3); g.Children.Add(sz);

                    var open = new TextBlock { Text = "Открыть", Foreground = ACC, FontSize = 11.5, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Открыть в проводнике и выделить" };
                    string p = it.Path;
                    open.MouseLeftButtonUp += (_, _) => OpenInExplorerSelect(p);
                    Grid.SetColumn(open, 4); g.Children.Add(open);

                    panel.Children.Add(g);
                }
                if (items.Count > cap)
                    panel.Children.Add(new TextBlock { Text = $"… и ещё {Format.Count(items.Count - cap)} (показаны крупнейшие {cap})", Foreground = DIM, FontSize = 11.5, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 6, 0, 0) });
            }

            // «Хвост»: разница между размером сводной плитки и суммой показанного — это масса
            // совсем мелких файлов (мельче порога), которых слишком много для поштучного списка.
            if (tail > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = $"+ {Format.Size(tail)} — это множество файлов мельче {Format.Size(tailThreshold)}. " +
                           "Их слишком много, чтобы перечислять поштучно, — это и есть основная часть «прочего».",
                    Foreground = DIM, FontSize = 12, FontStyle = FontStyles.Italic,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 2)
                });

            var sv = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Br("WinBg") };
            var copy = new Button { Content = "Копировать текст", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(12), Style = (Style)Application.Current.FindResource("DialogButton") };
            copy.Click += (_, _) => { try { Clipboard.SetText(plain.ToString()); } catch { } };
            var barRow = new Border { Background = PANEL, BorderBrush = TRACK, BorderThickness = new Thickness(0, 1, 0, 0), Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy } } };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(sv, 0); grid.Children.Add(sv);
            Grid.SetRow(barRow, 1); grid.Children.Add(barRow);
            return grid;
        }

        var win = new Window
        {
            Title = title,
            Owner = this,
            Icon = Icon,
            Width = 780,
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
                        Node = new DirNode { Path = child.Path, Name = child.Name + " — остаток", Size = rem, UnwrapSource = child },
                        Clickable = false,
                        Kind = Kind.Remainder
                    });
            }
            else
            {
                list.Add(new Entry { Node = child, Clickable = child.Children.Count > 0, Kind = Kind.Folder });
            }
        }

        AddFileEntries(folder, list);

        list.Sort((a, b) => b.Node.Size.CompareTo(a.Node.Size));
        return list;
    }

    // Файлы папки: крупные — поштучно, мелкий хвост — одной сводной записью.
    void AddFileEntries(DirNode folder, List<Entry> list)
    {
        if (folder.Files is { Count: > 0 } files)
        {
            var sorted = files.Where(f => f.Size > 0).OrderByDescending(f => f.Size).ToList();
            const int cap = 150;
            int shown = Math.Min(sorted.Count, cap);
            for (int i = 0; i < shown; i++)
            {
                var f = sorted[i];
                list.Add(new Entry
                {
                    Node = new DirNode { Path = Path.Combine(folder.Path, f.Name), Name = f.Name, Size = f.Size },
                    Clickable = false,
                    Kind = Kind.File
                });
            }
            if (sorted.Count > shown)
            {
                long rest = 0;
                for (int i = shown; i < sorted.Count; i++) rest += sorted[i].Size;
                int restCount = sorted.Count - shown;
                if (rest > 0)
                    list.Add(new Entry
                    {
                        Node = new DirNode { Path = folder.Path, Name = $"… ещё {Format.Count(restCount)} {Format.Plural(restCount, "файл", "файла", "файлов")}", Size = rest },
                        Clickable = false,
                        Kind = Kind.FilesTail
                    });
            }
        }
        else if (folder.OwnFileSize > 0)
        {
            // подстраховка, если файлы не собирались при скане
            list.Add(new Entry
            {
                Node = new DirNode { Path = folder.Path, Name = "Файлы в этой папке", Size = folder.OwnFileSize },
                Clickable = false,
                Kind = Kind.FilesTail
            });
        }
    }

    // ======================= детали / список =======================
    void OnInspect(DirNode? node, bool isFile)
    {
        var n = node ?? Current;
        UpdateDetails(n, node != null && isFile);
        SyncListSelection(n);                 // подсветить ту же строку в списке справа
    }

    // Выделить в списке справа строку, соответствующую узлу (без повторного захода в OnListSelect).
    void SyncListSelection(DirNode? node)
    {
        _syncSel = true;
        try
        {
            RowVM? row = null;
            if (node != null && ChildList.ItemsSource is IEnumerable<RowVM> rows)
                foreach (var r in rows)
                    if (ReferenceEquals(r.Node, node)) { row = r; break; }
            ChildList.SelectedItem = row;
        }
        finally { _syncSel = false; }
    }

    void UpdateDetails(DirNode? node, bool isFile)
    {
        if (node == null)
        {
            DetailName.Text = ""; DetailPath.Text = ""; DetailSize.Text = ""; DetailMeta.Text = "";
            DetailActions.Children.Clear();
            return;
        }

        DetailName.Text = node.Name;
        DetailPath.Text = node.Path;

        string pct = Current is { Size: > 0 } cur && !ReferenceEquals(node, cur)
            ? $"   ·   {Format.Percent(node.Size, cur.Size)} от текущей"
            : "";
        DetailSize.Text = Format.Size(node.Size) + pct;

        if (isFile)
        {
            string ext = Path.GetExtension(node.Name).TrimStart('.').ToUpperInvariant();
            DetailMeta.Text = ext.Length > 0 ? $"Файл · {ext}" : "Файл";
        }
        else
        {
            DetailMeta.Text = $"{Format.Count(node.FileCount)} файлов  ·  {Format.Count(node.DirCount)} папок";
        }

        // Кнопки действий — тот же набор, что и в контекстном меню (ПКМ по плитке/строке).
        bool isAggregate = _entries.Any(en => ReferenceEquals(en.Node, node) && en.Kind is Kind.Remainder or Kind.FilesTail);
        DetailActions.Children.Clear();
        foreach (var (label, act) in BuildActions(node, isFile, isAggregate))
        {
            var a = act;
            var b = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
                Style = (Style)FindResource(label.StartsWith("Умное добавление") ? "AccentButton" : "ToolButton")
            };
            b.Click += (_, _) => a();
            DetailActions.Children.Add(b);
        }
    }

    void OnListSelect(object sender, SelectionChangedEventArgs e)
    {
        if (_syncSel) return;                 // выбор пришёл из treemap — не зацикливаемся
        if (ChildList.SelectedItem is RowVM r)
        {
            UpdateDetails(r.Node, r.IsFile);
            Treemap.SelectNode(r.Node);        // синхронизируем подсветку слева
        }
    }

    void OnListActivate(object sender, MouseButtonEventArgs e)
    {
        if (ChildList.SelectedItem is not RowVM r) return;
        if (r.Clickable) DrillInto(r.Node);
        else if (r.IsFile) OpenInExplorerSelect(r.Node.Path);
    }

    void BuildBreadcrumb()
    {
        Breadcrumb.Items.Clear();

        // Если пришли из глобального скана (дерево ещё в памяти) — кнопка вернуться в него без пере-скана.
        if (_globalRoot != null && _history.Count > 0 && ReferenceEquals(_history[0], _globalRoot))
        {
            var g = new Button { Content = "← Глобальный вид", Style = (Style)FindResource("CrumbButton") };
            g.Click += (_, _) => { if (_globalRoot != null) EnterGlobalView(_globalRoot); };
            Breadcrumb.Items.Add(g);
            Breadcrumb.Items.Add(new TextBlock
            {
                Text = "   ·   ",
                Foreground = (Brush)Application.Current.Resources["FgDim"],
                VerticalAlignment = VerticalAlignment.Center
            });
        }

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

    void OnResetUnwraps(object sender, RoutedEventArgs e)
    {
        if (Current != null) _unwrapped.Remove(Current.Path);
        RefreshView();
    }

    // правый клик по плитке — единое контекстное меню
    void OnTreemapContext(Slice slice)
    {
        try { OpenContextMenu(slice.Node, slice.IsFile, slice.Aggregate, Treemap); }
        catch (Exception ex) { App.Log(ex); Status.Text = "Ошибка меню: " + ex.Message; }
    }

    // правый клик по строке списка справа — то же меню, что и по плитке
    void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep is not ListBoxItem) dep = VisualTreeHelper.GetParent(dep);
            if (dep is ListBoxItem { DataContext: RowVM r })
            {
                ChildList.SelectedItem = r;                 // выделяем строку, как при обычном клике
                OpenContextMenu(r.Node, r.IsFile, r.Aggregate, ChildList);
                e.Handled = true;
            }
        }
        catch (Exception ex) { App.Log(ex); Status.Text = "Ошибка меню: " + ex.Message; }
    }

    // Единый набор действий для узла — общий для контекстного меню (ПКМ по плитке/строке)
    // и для кнопок в панели деталей. Так набор действий везде одинаковый.
    List<(string label, Action act)> BuildActions(DirNode? node, bool isFile, bool isAggregate)
    {
        var list = new List<(string, Action)>();
        var folder = Current;
        if (folder == null || node == null) return list;

        if (_global && !isFile && !isAggregate && !ReferenceEquals(node, folder))
            list.Add(("Перейти к папке", () => ExitGlobalTo(node)));

        if (_global && isAggregate && _globalRoot is { } gRoot)
            list.Add(("Показать содержимое", () => ShowRemainderContents(gRoot)));

        if (!isFile && !isAggregate && node.FileCount > 0)
            list.Add(("Отчёт по файлам", () => ShowFileReport(node)));

        // «Умное добавление» — фича обычного вида. В глобальном виде всё и так разложено,
        // а её вызов там сбрасывал бы в обычный вид (и порождал плитку «‹папка› — остаток»).
        if (!_global && !isAggregate && folder.Children.Contains(node) && node.Children.Count > 0 && !IsUnwrapped(folder, node))
            list.Add(("Умное добавление к расчётам", () => Unwrap(folder, node)));

        if (node.UnwrapSource is { } src)
            list.Add(($"Свернуть обратно «{src.Name}»", () => Collapse(folder, src)));

        if (isFile && !isAggregate && File.Exists(node.Path))
            list.Add(("Открыть в проводнике", () => OpenInExplorerSelect(node.Path)));
        else if (!isFile && !string.IsNullOrEmpty(node.Path) && Directory.Exists(node.Path))
            list.Add(("Открыть в проводнике", () => OpenPath(node.Path)));

        return list;
    }

    void OpenContextMenu(DirNode node, bool isFile, bool isAggregate, UIElement target)
    {
        var actions = BuildActions(node, isFile, isAggregate);
        if (actions.Count == 0) return;

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = target };
        foreach (var (label, act) in actions)
        {
            if (label == "Открыть в проводнике" && menu.Items.Count > 0)
                menu.Items.Add(new Separator());
            var a = act;
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
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
        // Снимок записей на момент открытия — окно немодальное и перестраивается при смене темы,
        // а folder/vr/текст зафиксированы; без снимка перерисовка брала бы уже другой (текущий) вид.
        var repEntries = _entries.ToList();

        UIElement Build()
        {
        Brush Br(string key) => (Brush)Application.Current.Resources[key];
        Brush BG = Br("WinBg"), PANEL = Br("PanelBg"), FG = Br("Fg"), FGS = Br("FgStrong"),
              DIM = Br("FgDim"), TRACK = Br("Border"), ACC = Br("CrumbFg");

        long total = folder.Size <= 0 ? 1 : folder.Size;
        long max = repEntries.Count > 0 ? Math.Max(1, repEntries.Max(e => e.Node.Size)) : 1;

        // цвета как в treemap (чтобы отчёт и плитки совпадали)
        var colorOf = new Dictionary<DirNode, Color>();
        int ci = 0;
        foreach (var en in repEntries)
            colorOf[en.Node] = en.Kind switch
            {
                Kind.File or Kind.FilesTail => _dark ? Hex("#5E6E8C") : Hex("#A9B6CC"),
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

            const int maxRows = 80;
            int expandTo = Math.Min(items.Count, maxRows);
            var rest = new StackPanel { Visibility = Visibility.Collapsed };
            for (int i = show; i < expandTo; i++)
                rest.Children.Add(MakeRow(items[i].Node.Name, items[i].Node.Size, colorOf[items[i].Node]));
            int notShown = items.Count - expandTo;
            if (notShown > 0)
                rest.Children.Add(new TextBlock { Text = $"… и ещё {Format.Count(notShown)} {Format.Plural(notShown, "папка", "папки", "папок")} (суммарно {Format.Size(items.Skip(expandTo).Sum(e => e.Node.Size))})", Foreground = DIM, FontSize = 11.5, FontStyle = FontStyles.Italic, Margin = new Thickness(6, 4, 0, 0) });
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
        var folders = repEntries.Where(e => e.Kind is Kind.Folder or Kind.Promoted or Kind.Remainder).ToList();

        foreach (var (label, min, mx) in tiers)
            AddGroup(label, folders.Where(e => e.Node.Size >= min && e.Node.Size < mx).ToList());

        AddGroup("Мелкие (< 1 ГБ)", folders.Where(e => e.Node.Size < GB).ToList());

        if (folder.OwnFileSize > 0)
        {
            AddHeader("Файлы напрямую в папке", $"{Format.Size(folder.OwnFileSize)}  ·  {Format.Percent(folder.OwnFileSize, total)}");
            panel.Children.Add(new TextBlock { Text = "Подробный разбор файлов — кнопка «Файлы».", Foreground = DIM, FontSize = 11.5, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 0) });
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

    // ======================= отчёт по файлам =======================
    void OnFileReport(object sender, RoutedEventArgs e)
    {
        var folder = Current;
        if (folder == null) { Status.Text = "Сначала отсканируй папку."; return; }
        ShowFileReport(folder);
    }

    void ShowFileReport(DirNode folder)
    {
        var files = Analysis.CollectFiles(folder).Where(f => f.Size > 0).OrderByDescending(f => f.Size).ToList();
        long totalFiles = 0;
        foreach (var f in files) totalFiles += f.Size;
        long denom = totalFiles <= 0 ? 1 : totalFiles;
        string Rel(string p) => Path.GetRelativePath(folder.Path, p);

        var plain = new StringBuilder();
        plain.AppendLine(folder.Path);
        plain.AppendLine($"Файлов: {Format.Count(files.Count)}   ·   {Format.Size(totalFiles)}");
        plain.AppendLine(new string('─', 56));
        plain.AppendLine();
        plain.AppendLine("Крупнейшие файлы:");
        foreach (var f in files.Take(40)) plain.AppendLine($"  {Format.Size(f.Size),12}   {Rel(f.Path)}");

        UIElement Build()
        {
            Brush Br(string k) => (Brush)Application.Current.Resources[k];
            Brush FG = Br("Fg"), FGS = Br("FgStrong"), DIM = Br("FgDim"), TRACK = Br("Border"), PANEL = Br("PanelBg");
            Color fileCol = _dark ? Hex("#5E6E8C") : Hex("#8FA0BE");
            long maxF = files.Count > 0 ? files[0].Size : 1;
            if (maxF < 1) maxF = 1;

            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = "Файлы: " + folder.Name, Foreground = FGS, FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock { Text = folder.Path, Foreground = DIM, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = $"{Format.Count(files.Count)} {Format.Plural(files.Count, "файл", "файла", "файлов")}   ·   {Format.Size(totalFiles)}", Foreground = FG, FontSize = 13.5, Margin = new Thickness(0, 8, 0, 0) });

            if (files.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "Файлов не найдено.", Foreground = DIM, FontSize = 13, Margin = new Thickness(0, 14, 0, 0) });
            }
            else
            {
                FrameworkElement Bar(long size)
                {
                    var g = new Grid { Width = 204, Height = 8, VerticalAlignment = VerticalAlignment.Center };
                    g.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = TRACK });
                    g.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(fileCol), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(4, (double)size / maxF * 204) });
                    return g;
                }
                FrameworkElement MakeRow(string name, long size)
                {
                    var g = new Grid { Height = 26 };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                    var nm = new TextBlock { Text = name, Foreground = FG, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) };
                    Grid.SetColumn(nm, 0); g.Children.Add(nm);
                    var bar = Bar(size); Grid.SetColumn(bar, 1); g.Children.Add(bar);
                    var sz = new TextBlock { Text = Format.Size(size), Foreground = FG, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 8, 0) };
                    Grid.SetColumn(sz, 2); g.Children.Add(sz);
                    var pc = new TextBlock { Text = Format.Percent(size, denom), Foreground = DIM, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                    Grid.SetColumn(pc, 3); g.Children.Add(pc);
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
                static long Median(IReadOnlyList<long> xs)
                {
                    if (xs.Count == 0) return 0;
                    var s = xs.OrderBy(x => x).ToArray();
                    int m = s.Length / 2;
                    return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
                }
                void AddGroup(string label, List<Analysis.FileRef> items)
                {
                    if (items.Count == 0) return;
                    long sum = 0; foreach (var f in items) sum += f.Size;
                    long median = Median(items.Select(f => f.Size).ToList());
                    AddHeader(label,
                        $"{Format.Count(items.Count)} {Format.Plural(items.Count, "файл", "файла", "файлов")}  ·  " +
                        $"{Format.Size(sum)}  ·  {Format.Percent(sum, denom)}  ·  медиана {Format.Size(median)}");
                    int show = Math.Min(items.Count, 4);
                    for (int i = 0; i < show; i++) panel.Children.Add(MakeRow(Rel(items[i].Path), items[i].Size));
                    if (items.Count <= show) return;

                    // Не плодим тысячи строк: разворачиваем максимум maxRows, остальное — одной строкой.
                    const int maxRows = 80;
                    int expandTo = Math.Min(items.Count, maxRows);
                    var rest = new StackPanel { Visibility = Visibility.Collapsed };
                    for (int i = show; i < expandTo; i++) rest.Children.Add(MakeRow(Rel(items[i].Path), items[i].Size));
                    int notShown = items.Count - expandTo;
                    if (notShown > 0)
                    {
                        long tailSum = 0; for (int i = expandTo; i < items.Count; i++) tailSum += items[i].Size;
                        rest.Children.Add(new TextBlock { Text = $"… и ещё {Format.Count(notShown)} {Format.Plural(notShown, "файл", "файла", "файлов")} (суммарно {Format.Size(tailSum)})", Foreground = DIM, FontSize = 11.5, FontStyle = FontStyles.Italic, Margin = new Thickness(6, 4, 0, 0) });
                    }
                    panel.Children.Add(rest);

                    int hidden = items.Count - show;
                    long hiddenSum = 0; for (int i = show; i < items.Count; i++) hiddenSum += items[i].Size;
                    string collapsed = $"▾  Показать ещё {hidden} {Format.Plural(hidden, "файл", "файла", "файлов")} (суммарно {Format.Size(hiddenSum)})";
                    var link = new TextBlock { Text = collapsed, Foreground = Br("CrumbFg"), FontSize = 12.5, Cursor = Cursors.Hand, Margin = new Thickness(18, 5, 0, 2) };
                    link.MouseLeftButtonUp += (_, _) =>
                    {
                        bool open = rest.Visibility != Visibility.Visible;
                        rest.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                        link.Text = open ? "▴  Свернуть" : collapsed;
                    };
                    panel.Children.Add(link);
                }

                const long GB = 1L << 30, MB = 1L << 20;
                AddGroup("Огромные (≥ 1 ГБ)", files.Where(f => f.Size >= GB).ToList());
                AddGroup("Крупные (100 МБ – 1 ГБ)", files.Where(f => f.Size >= 100 * MB && f.Size < GB).ToList());
                AddGroup("Средние (10 – 100 МБ)", files.Where(f => f.Size >= 10 * MB && f.Size < 100 * MB).ToList());
                AddGroup("Небольшие (1 – 10 МБ)", files.Where(f => f.Size >= MB && f.Size < 10 * MB).ToList());
                AddGroup("Мелкие (< 1 МБ)", files.Where(f => f.Size < MB).ToList());

                // по типам файлов
                var byExt = files
                    .GroupBy(f => { var x = Path.GetExtension(f.Path).TrimStart('.').ToUpperInvariant(); return x.Length == 0 ? "(без расширения)" : x; })
                    .Select(g => (Ext: g.Key, Sum: g.Sum(x => x.Size), Count: g.Count()))
                    .OrderByDescending(x => x.Sum)
                    .Take(12)
                    .ToList();
                if (byExt.Count > 0)
                {
                    AddHeader("По типам файлов", "");
                    foreach (var t in byExt)
                        panel.Children.Add(MakeRow($"{t.Ext}   ({Format.Count(t.Count)} {Format.Plural(t.Count, "файл", "файла", "файлов")})", t.Sum));
                }

                AddHeader("Крупнейшие файлы", "");
                foreach (var f in files.Take(15)) panel.Children.Add(MakeRow(Rel(f.Path), f.Size));
            }

            var sv = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Br("WinBg") };
            var copy = new Button { Content = "Копировать текст", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(12), Style = (Style)Application.Current.FindResource("DialogButton") };
            copy.Click += (_, _) => { try { Clipboard.SetText(plain.ToString()); } catch { } };
            var barRow = new Border { Background = PANEL, BorderBrush = TRACK, BorderThickness = new Thickness(0, 1, 0, 0), Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy } } };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(sv, 0); grid.Children.Add(sv);
            Grid.SetRow(barRow, 1); grid.Children.Add(barRow);
            return grid;
        }

        var win = new Window
        {
            Title = $"Файлы — {folder.Name}",
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
        // Кандидаты на удаление — папки И файлы (в т.ч. тяжёлые файлы из глобального скана).
        // Служебные плитки (остаток развёртки, «Мелкие файлы и прочее») не берём.
        var candidates = _entries
            .Where(en => en.Kind is Kind.Folder or Kind.Promoted or Kind.File)
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

            var open = new TextBlock { Text = "Открыть в проводнике", Foreground = ACC, FontSize = 11.5, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), ToolTip = "Открыть проводник и выделить этот элемент" };
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
                inner.Children.Add(new TextBlock { Text = "Подходящих элементов такого размера здесь нет.", Foreground = DIM, FontSize = 12, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 8, 0, 0) });
            }
            else
            {
                var summary = new TextBlock { FontSize = 13, Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap };
                summary.Inlines.Add(new System.Windows.Documents.Run($"Удалить {Format.Count(plan.Folders.Count)} {Format.Plural(plan.Folders.Count, "элемент", "элемента", "элементов")}  ·  освободит {Format.Size(plan.Freed)}") { Foreground = FG });
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
                    inner.Children.Add(new TextBlock { Text = $"… ещё {Format.Count(more)} {Format.Plural(more, "элемент", "элемента", "элементов")} (суммарно {Format.Size(restSum)})", Foreground = DIM, FontSize = 11.5, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0) });
                }

                var openAll = new TextBlock { Text = $"Открыть все в проводнике ({plan.Folders.Count})", Foreground = ACC, FontSize = 12, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Margin = new Thickness(0, 12, 0, 0), ToolTip = "Откроет окно проводника для каждого элемента плана" };
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
                results.Children.Add(new TextBlock { Text = "Здесь нет подходящих элементов для удаления.", Foreground = DIM, FontSize = 13 });
                return;
            }
            if (need > totalCand)
                results.Children.Add(new TextBlock { Text = $"Запрошено больше, чем занимают все элементы здесь ({Format.Size(totalCand)}). Даже удалив всё, столько не освободить.", Foreground = DIM, FontSize = 12, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });

            results.Children.Add(Card("Вариант 1 — минимум элементов", "один-два самых крупных, чтобы закрыть цель сразу", Analysis.PlanClosestFew(candidates, need), need));
            results.Children.Add(Card("Вариант 2 — несколько средних", "элементы среднего размера", Analysis.PlanGreedyCapped(candidates, need, Math.Max(1, need / 2)), need));
            results.Children.Add(Card("Вариант 3 — много мелких", "много небольших, не трогая крупные", Analysis.PlanGreedyCapped(candidates, need, Math.Max(1, need / 15)), need));
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
    static void OpenPath(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }

    // Открывает проводник и выделяет папку или файл (удобно, чтобы сразу удалить).
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
    public bool IsFile { get; set; }
    public bool Aggregate { get; set; }
    public Geometry? Glyph { get; set; }
}

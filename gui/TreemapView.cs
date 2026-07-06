using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SpaceSaver;

namespace SpaceSaver.Gui;

/// <summary>Одна плитка treemap: узел + флаги + цвет (цвета назначает MainWindow, чтобы совпадали со списком).</summary>
public sealed class Slice
{
    public required DirNode Node;
    public bool Clickable;     // настоящая папка, в которую можно провалиться
    public bool Aggregate;     // служебная плитка (файлы / остаток / «прочее»)
    public Color Color;
}

/// <summary>
/// Treemap-визуализация. Рисует переданный список слайсов (площадь ∝ размеру),
/// двойной клик — провалиться, правый клик — контекстное меню. Тема настраивается.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    readonly List<Slice> _slices = new();
    readonly List<(Slice slice, Rect rect)> _tiles = new();
    int _hover = -1;
    int _selected = -1;
    bool _hasData;

    public event Action<DirNode?>? Inspect;
    public event Action<DirNode>? Drill;
    public event Action<Slice>? ContextRequested;

    // тема
    Brush _back = null!, _label = null!, _shadow = null!, _hint = null!;
    Color _aggColor;
    Pen _grid = null!, _hoverPen = null!, _selPen = null!;

    static readonly Typeface Face =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public TreemapView()
    {
        Focusable = true;
        ClipToBounds = true;
        SetTheme(light: false);
    }

    public void SetTheme(bool light)
    {
        if (light)
        {
            _back = B(Color.FromRgb(0xFF, 0xFF, 0xFF));
            _label = B(Color.FromRgb(0x14, 0x18, 0x20));
            _shadow = B(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            _hint = B(Color.FromRgb(0x9A, 0xA1, 0xAD));
            _aggColor = Color.FromRgb(0xCF, 0xD4, 0xDC);
            _grid = P(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 1);
            _hoverPen = P(Color.FromRgb(0x1A, 0x1E, 0x26), 2);
            _selPen = P(Color.FromRgb(0x2F, 0x6F, 0xED), 2);
        }
        else
        {
            _back = B(Color.FromRgb(0x14, 0x16, 0x1B));
            _label = B(Color.FromRgb(0xF2, 0xF4, 0xF8));
            _shadow = B(Color.FromArgb(0xCC, 0, 0, 0));
            _hint = B(Color.FromRgb(0x6B, 0x72, 0x80));
            _aggColor = Color.FromRgb(0x30, 0x34, 0x3D);
            _grid = P(Color.FromArgb(0x66, 0, 0, 0), 1);
            _hoverPen = P(Color.FromRgb(0xFF, 0xFF, 0xFF), 2);
            _selPen = P(Color.FromRgb(0x7C, 0xC4, 0xFF), 2);
        }
        InvalidateVisual();
    }

    public void SetSlices(IReadOnlyList<Slice> slices)
    {
        _slices.Clear();
        _slices.AddRange(slices);
        _hasData = true;
        _hover = -1;
        _selected = -1;
        Relayout();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Relayout();
    }

    void Relayout()
    {
        _tiles.Clear();
        if (_slices.Count > 0 && ActualWidth > 4 && ActualHeight > 4)
        {
            // Слишком мелкие плитки сворачиваем в одну служебную (только для рисования).
            var ordered = _slices.OrderByDescending(s => s.Node.Size).ToList();
            const int cap = 200;
            List<Slice> draw;
            if (ordered.Count > cap)
            {
                draw = ordered.Take(cap).ToList();
                long rest = ordered.Skip(cap).Sum(s => s.Node.Size);
                if (rest > 0)
                    draw.Add(new Slice
                    {
                        Node = new DirNode { Path = "", Name = "… прочее", Size = rest },
                        Aggregate = true,
                        Color = _aggColor
                    });
            }
            else draw = ordered;

            var items = new List<(int idx, double area)>(draw.Count);
            for (int i = 0; i < draw.Count; i++) items.Add((i, Math.Max(1, draw[i].Node.Size)));
            foreach (var (idx, rect) in Squarify(items, new Rect(0, 0, ActualWidth, ActualHeight)))
                _tiles.Add((draw[idx], rect));
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_back, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (!_hasData) { DrawHint(dc, "Выбери папку кнопкой «Обзор…» или перетащи её сюда"); return; }
        if (_tiles.Count == 0) { DrawHint(dc, "Здесь пусто"); return; }

        for (int i = 0; i < _tiles.Count; i++)
        {
            var (slice, r0) = _tiles[i];
            if (r0.Width < 1 || r0.Height < 1) continue;
            var rect = Inset(r0, 1);
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            var fill = i == _hover ? Brighten(slice.Color, 1.18) : slice.Color;
            dc.DrawRoundedRectangle(new SolidColorBrush(fill), _grid, rect, 3, 3);
            DrawLabel(dc, slice, rect);
        }

        if (_selected >= 0 && _selected < _tiles.Count)
        {
            var r = Inset(_tiles[_selected].rect, 1);
            if (r.Width > 0 && r.Height > 0) dc.DrawRoundedRectangle(null, _selPen, r, 3, 3);
        }
        if (_hover >= 0 && _hover < _tiles.Count)
        {
            var r = Inset(_tiles[_hover].rect, 1);
            if (r.Width > 0 && r.Height > 0) dc.DrawRoundedRectangle(null, _hoverPen, r, 3, 3);
        }
    }

    void DrawLabel(DrawingContext dc, Slice slice, Rect r)
    {
        if (r.Width < 50 || r.Height < 22) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double maxW = Math.Max(1, r.Width - 10);

        var name = Text(slice.Node.Name, 12.5, _label, dpi, maxW);
        var shadow = Text(slice.Node.Name, 12.5, _shadow, dpi, maxW);

        dc.PushClip(new RectangleGeometry(r));
        double x = r.X + 6, y = r.Y + 4;
        dc.DrawText(shadow, new Point(x + 1, y + 1));
        dc.DrawText(name, new Point(x, y));
        if (r.Height > 40)
        {
            var size = Text(Format.Size(slice.Node.Size), 11, _label, dpi, maxW);
            size.SetForegroundBrush(Brighten(_label is SolidColorBrush sb ? sb.Color : Colors.Gray, 0.85).ToBrush());
            dc.DrawText(size, new Point(x, y + 17));
        }
        dc.Pop();
    }

    static FormattedText Text(string s, double size, Brush brush, double dpi, double maxW)
        => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, dpi)
        { MaxTextWidth = maxW, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    void DrawHint(DrawingContext dc, string text)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 15, _hint, dpi);
        dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
    }

    // ---- мышь ----
    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Наведение только подсвечивает плитку; детали обновляются по клику (стабильность панели).
        int t = HitTest(e.GetPosition(this));
        if (t != _hover)
        {
            _hover = t;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != -1)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        int t = HitTest(e.GetPosition(this));
        if (t < 0) return;
        _selected = t;
        var slice = _tiles[t].slice;
        Inspect?.Invoke(slice.Node);
        InvalidateVisual();
        if (e.ClickCount == 2 && slice.Clickable) Drill?.Invoke(slice.Node);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        int t = HitTest(e.GetPosition(this));
        if (t < 0) return;
        _selected = t;
        InvalidateVisual();
        ContextRequested?.Invoke(_tiles[t].slice);
        e.Handled = true;
    }

    int HitTest(Point p)
    {
        for (int i = 0; i < _tiles.Count; i++)
            if (_tiles[i].rect.Contains(p)) return i;
        return -1;
    }

    // ---- squarified treemap (Bruls, Huizing, van Wijk) ----
    static List<(int idx, Rect rect)> Squarify(List<(int idx, double area)> items, Rect bounds)
    {
        var result = new List<(int, Rect)>();
        if (items.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        double total = 0;
        foreach (var it in items) total += it.area;
        if (total <= 0) return result;

        double scale = bounds.Width * bounds.Height / total;
        var scaled = items.Select(it => (it.idx, area: it.area * scale)).ToList();

        Rect rect = bounds;
        var row = new List<(int idx, double area)>();
        int i = 0;
        while (i < scaled.Count)
        {
            double side = Math.Min(rect.Width, rect.Height);
            if (row.Count == 0) { row.Add(scaled[i]); i++; continue; }

            double curWorst = Worst(row, side);
            row.Add(scaled[i]);
            double newWorst = Worst(row, side);
            if (newWorst <= curWorst) { i++; }
            else { row.RemoveAt(row.Count - 1); rect = LayoutRow(row, rect, result); row.Clear(); }
        }
        if (row.Count > 0) LayoutRow(row, rect, result);
        return result;
    }

    static double Worst(List<(int idx, double area)> row, double side)
    {
        double sum = 0, max = double.MinValue, min = double.MaxValue;
        foreach (var r in row) { sum += r.area; if (r.area > max) max = r.area; if (r.area < min) min = r.area; }
        if (sum <= 0 || side <= 0) return double.PositiveInfinity;
        double s2 = sum * sum, side2 = side * side;
        return Math.Max(side2 * max / s2, s2 / (side2 * min));
    }

    static Rect LayoutRow(List<(int idx, double area)> row, Rect rect, List<(int, Rect)> result)
    {
        double sum = 0;
        foreach (var r in row) sum += r.area;
        if (sum <= 0) return rect;

        if (rect.Width >= rect.Height)
        {
            double band = Math.Min(sum / rect.Height, rect.Width);
            double y = rect.Y;
            foreach (var r in row)
            {
                double h = band > 0 ? r.area / band : 0;
                result.Add((r.idx, new Rect(rect.X, y, band, h)));
                y += h;
            }
            return new Rect(rect.X + band, rect.Y, Math.Max(0, rect.Width - band), rect.Height);
        }
        else
        {
            double band = Math.Min(sum / rect.Width, rect.Height);
            double x = rect.X;
            foreach (var r in row)
            {
                double w = band > 0 ? r.area / band : 0;
                result.Add((r.idx, new Rect(x, rect.Y, w, band)));
                x += w;
            }
            return new Rect(rect.X, rect.Y + band, rect.Width, Math.Max(0, rect.Height - band));
        }
    }

    // ---- цвет ----
    public static Color ColorForIndex(int i, bool light)
    {
        double hue = i * 137.508 % 360.0; // золотой угол — соседние плитки контрастны
        return light ? HslToRgb(hue, 0.55, 0.70) : HslToRgb(hue, 0.50, 0.55);
    }

    static Color HslToRgb(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360 / 360.0;
        double r, g, b;
        if (s == 0) { r = g = b = l; }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    static Color Brighten(Color c, double f)
        => Color.FromRgb(ClampByte(c.R * f), ClampByte(c.G * f), ClampByte(c.B * f));

    static byte ClampByte(double v) => (byte)Math.Max(0, Math.Min(255, v));

    static Rect Inset(Rect r, double d)
        => new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

    static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Pen P(Color c, double w) { var p = new Pen(B(c), w); p.Freeze(); return p; }
}

static class ColorExt
{
    public static Brush ToBrush(this Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

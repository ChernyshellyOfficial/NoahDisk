using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NoahDisk;

// ============================================================================
//  Текстовый отчёт по дереву папок — общий для консоли и GUI.
//  Группирует папки по размеру (как в исходном примере пользователя),
//  плюс дерево «тяжёлых веток» и топ крупнейших папок.
// ============================================================================
public static class TextReport
{
    const long MB = 1L << 20;
    const long GB = 1L << 30;
    const long TB = 1L << 40;

    public static string Build(DirNode root, bool showTree = true, int treeDepth = 3, bool showTop = true, int topN = 12)
    {
        var sb = new StringBuilder();
        sb.Append(Distribution(root));
        if (showTree && treeDepth > 0) sb.Append(HeavyTree(root, treeDepth));
        if (showTop && topN > 0) sb.Append(Top(root, topN));
        return sb.ToString();
    }

    public static string Distribution(DirNode root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Распределение по папкам верхнего уровня:");
        sb.AppendLine();

        var children = root.Children.OrderByDescending(c => c.Size).ToList();
        long total = root.Size;

        if (children.Count == 0)
        {
            sb.AppendLine("  Вложенных папок нет.");
        }
        else
        {
            (string label, long min, long max)[] tiers =
            {
                ("≥ 1 ТБ",         TB,        long.MaxValue),
                ("100 ГБ – 1 ТБ",  100 * GB,  TB),
                ("10 – 100 ГБ",    10 * GB,   100 * GB),
                ("1 – 10 ГБ",      GB,        10 * GB),
            };

            bool printedAny = false;
            foreach (var (label, min, max) in tiers)
            {
                var items = children.Where(c => c.Size >= min && c.Size < max).ToList();
                if (items.Count == 0) continue;
                printedAny = true;

                long sum = items.Sum(c => c.Size);
                sb.AppendLine($"  {label}  —  {Format.Count(items.Count)} " +
                              $"{Format.Plural(items.Count, "папка", "папки", "папок")}, " +
                              $"{Format.Size(sum)} ({Format.Percent(sum, total)})");

                const int cap = 15;
                foreach (var c in items.Take(cap))
                    sb.AppendLine($"      • {c.Name,-32} {Format.Size(c.Size)}");
                if (items.Count > cap)
                    sb.AppendLine($"      • … ещё {items.Count - cap}");
                sb.AppendLine();
            }

            var tail = children.Where(c => c.Size < GB).ToList();
            if (tail.Count > 0)
            {
                long sum = tail.Sum(c => c.Size);
                sb.AppendLine($"  Мелкие (< 1 ГБ)  —  {Format.Count(tail.Count)} " +
                              $"{Format.Plural(tail.Count, "папка", "папки", "папок")}, " +
                              $"{Format.Size(sum)} ({Format.Percent(sum, total)})");
                var biggest = tail.OrderByDescending(c => c.Size).First();
                if (biggest.Size > 0)
                    sb.AppendLine($"      крупнейшая из них: {biggest.Name} ({Format.Size(biggest.Size)})");
                sb.AppendLine();
            }

            if (!printedAny && tail.Count == 0)
                sb.AppendLine("  Все папки пустые.");
        }

        if (root.OwnFileSize > 0)
        {
            sb.AppendLine($"  Файлы прямо в корне: {Format.Size(root.OwnFileSize)} " +
                          $"({Format.Percent(root.OwnFileSize, total)})");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string HeavyTree(DirNode root, int maxDepth)
    {
        var sb = new StringBuilder();
        long threshold = Math.Max(root.Size / 100, 100 * MB);
        sb.AppendLine($"Где концентрируется место (ветки ≥ {Format.Size(threshold)}):");
        sb.AppendLine();
        sb.AppendLine($"  {root.Name}  —  {Format.Size(root.Size)}");
        Walk(sb, root, "  ", root.Size, threshold, maxDepth, 1);
        sb.AppendLine();
        return sb.ToString();
    }

    static void Walk(StringBuilder sb, DirNode node, string prefix, long grandTotal, long threshold, int maxDepth, int depth)
    {
        if (depth > maxDepth) return;

        var heavy = node.Children
            .Where(c => c.Size >= threshold)
            .OrderByDescending(c => c.Size)
            .ToList();
        if (heavy.Count == 0) return;

        const int cap = 8;
        bool truncated = heavy.Count > cap;
        var rows = heavy.Take(cap).ToList();

        for (int i = 0; i < rows.Count; i++)
        {
            var c = rows[i];
            bool isLast = i == rows.Count - 1 && !truncated;
            string conn = isLast ? "└─ " : "├─ ";
            string childPrefix = prefix + (isLast ? "   " : "│  ");
            sb.AppendLine($"{prefix}{conn}{c.Name}  {Format.Size(c.Size)}  ({Format.Percent(c.Size, grandTotal)})");
            Walk(sb, c, childPrefix, grandTotal, threshold, maxDepth, depth + 1);
        }

        if (truncated)
            sb.AppendLine($"{prefix}└─ … ещё {heavy.Count - cap} крупных веток");
    }

    public static string Top(DirNode root, int topN)
    {
        var all = new List<DirNode>();
        var stack = new Stack<DirNode>();
        foreach (var c in root.Children) stack.Push(c);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            all.Add(n);
            foreach (var c in n.Children) stack.Push(c);
        }

        var top = all.OrderByDescending(n => n.Size).Take(topN).ToList();
        if (top.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine($"Топ-{top.Count} крупнейших папок (на любой глубине):");
        sb.AppendLine();
        int rank = 1;
        foreach (var n in top)
        {
            string rel = Path.GetRelativePath(root.Path, n.Path);
            sb.AppendLine($"  {rank,2}. {Format.Size(n.Size),12}   {rel}");
            rank++;
        }
        sb.AppendLine();
        return sb.ToString();
    }
}

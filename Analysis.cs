using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceSaver;

// ============================================================================
//  Аналитика поверх дерева папок. Чистая логика на DirNode (без UI) —
//  чтобы её можно было переиспользовать и покрыть тестами.
// ============================================================================
public static class Analysis
{
    /// <summary>Доля «всего кроме крупнейшего ребёнка», ниже которой считаем папку
    /// проходной (вес сосредоточен в одном ребёнке) и спускаемся глубже.</summary>
    public const double DefaultRestThreshold = 0.15;

    public sealed class UnwrapResult
    {
        public required DirNode SplitNode;        // узел, на котором вес разделился на значимые доли
        public required List<DirNode> Promoted;   // его значимые дети (их подмешиваем в родителя)
        public long UsedSize;                     // суммарный размер promoted
    }

    /// <summary>
    /// «Умная развёртка»: спускается по доминирующей цепочке (Steam → steamapps → common),
    /// пока вес не разделится на значимые доли, и возвращает детей точки разделения.
    /// </summary>
    public static UnwrapResult SmartUnwrap(DirNode node, double restThreshold = DefaultRestThreshold)
    {
        var cur = node;
        while (true)
        {
            var kids = cur.Children.Where(c => c.Size > 0).ToList();
            if (kids.Count == 0) break;

            DirNode largest = kids[0];
            foreach (var k in kids) if (k.Size > largest.Size) largest = k;

            long rest = cur.Size - largest.Size;
            // спускаемся, только если крупнейший — папка и всё остальное в сумме незначительно
            bool dominant = largest.Children.Count > 0 && rest < cur.Size * restThreshold;
            if (!dominant) break;
            cur = largest;
        }

        var promoted = cur.Children.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();
        return new UnwrapResult
        {
            SplitNode = cur,
            Promoted = promoted,
            UsedSize = promoted.Sum(c => c.Size)
        };
    }

    // ========================================================================
    //  Планы освобождения места: какие папки удалить, чтобы набрать ~need байт.
    // ========================================================================
    public sealed class CleanupPlan
    {
        public List<DirNode> Folders = new();
        public long Freed;
        public bool ReachedTarget;
    }

    /// <summary>Вариант «минимум папок»: 1–2 папки, чья сумма размеров ближе всего к need.</summary>
    public static CleanupPlan PlanClosestFew(List<DirNode> cands, long need)
    {
        var plan = new CleanupPlan();
        if (cands.Count == 0) return plan;

        DirNode? best1 = null; long diff1 = long.MaxValue;
        foreach (var c in cands)
        {
            long d = Math.Abs(c.Size - need);
            if (d < diff1) { diff1 = d; best1 = c; }
        }

        var asc = cands.OrderBy(c => c.Size).ToList();
        DirNode? a = null, b = null; long diff2 = long.MaxValue, sum2 = 0;
        int i = 0, j = asc.Count - 1;
        while (i < j)
        {
            long sum = asc[i].Size + asc[j].Size;
            long d = Math.Abs(sum - need);
            if (d < diff2) { diff2 = d; a = asc[j]; b = asc[i]; sum2 = sum; }
            if (sum < need) i++; else j--;
        }

        if (a != null && b != null && diff2 < diff1)
        {
            plan.Folders.Add(a); plan.Folders.Add(b); plan.Freed = sum2;
        }
        else if (best1 != null)
        {
            plan.Folders.Add(best1); plan.Freed = best1.Size;
        }
        plan.ReachedTarget = plan.Freed >= need;
        return plan;
    }

    /// <summary>Жадно берём папки (от крупных к мелким) размером не больше cap, пока не наберём need.</summary>
    public static CleanupPlan PlanGreedyCapped(List<DirNode> cands, long need, long capPerFolder)
    {
        var plan = new CleanupPlan();
        foreach (var c in cands.OrderByDescending(c => c.Size))
        {
            if (plan.Freed >= need) break;
            if (c.Size > capPerFolder) continue;
            plan.Folders.Add(c);
            plan.Freed += c.Size;
        }
        plan.ReachedTarget = plan.Freed >= need;
        return plan;
    }
}

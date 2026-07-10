using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NoahDisk;

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

    /// <summary>Вариант «минимум элементов»: 1–2 элемента, которые ДОСТИГАЮТ цели с наименьшим
    /// перелётом (при равенстве — меньше элементов). Если ≤2 элементами цель не достижима —
    /// лучший по объёму вариант из 1–2 самых крупных.</summary>
    public static CleanupPlan PlanClosestFew(List<DirNode> cands, long need)
    {
        var plan = new CleanupPlan();
        if (cands.Count == 0) return plan;

        var asc = cands.OrderBy(c => c.Size).ToList();

        // 1 элемент, достигающий цели: наименьший c.Size >= need (минимальный перелёт для одного).
        DirNode? single = null;
        foreach (var c in asc) if (c.Size >= need) { single = c; break; }

        // 2 элемента с суммой >= need и минимальной суммой (два указателя по отсортированному).
        DirNode? pairA = null, pairB = null; long pairSum = 0;
        {
            int i = 0, j = asc.Count - 1; long best = long.MaxValue;
            while (i < j)
            {
                long sum = asc[i].Size + asc[j].Size;
                if (sum >= need) { if (sum < best) { best = sum; pairA = asc[j]; pairB = asc[i]; pairSum = sum; } j--; }
                else i++;
            }
        }

        if (single != null || pairA != null)
        {
            // Оба достигают — берём с меньшим перелётом; при равном перелёте предпочитаем 1 элемент.
            long singleOver = single != null ? single.Size - need : long.MaxValue;
            long pairOver = pairA != null ? pairSum - need : long.MaxValue;
            if (single != null && singleOver <= pairOver)
            {
                plan.Folders.Add(single); plan.Freed = single.Size;
            }
            else
            {
                plan.Folders.Add(pairA!); plan.Folders.Add(pairB!); plan.Freed = pairSum;
            }
        }
        else
        {
            // Цель не достижима 1–2 элементами — берём то, что освободит больше (1 или пара крупнейших).
            long two = asc.Count >= 2 ? asc[^1].Size + asc[^2].Size : asc[^1].Size;
            if (asc.Count >= 2 && two > asc[^1].Size)
            {
                plan.Folders.Add(asc[^1]); plan.Folders.Add(asc[^2]); plan.Freed = two;
            }
            else
            {
                plan.Folders.Add(asc[^1]); plan.Freed = asc[^1].Size;
            }
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

    /// <summary>Файл с полным путём (для отчёта по файлам).</summary>
    public readonly record struct FileRef(string Path, long Size);

    /// <summary>Все файлы поддерева (с полными путями). Требует скана с collectFiles.</summary>
    public static List<FileRef> CollectFiles(DirNode root)
    {
        var result = new List<FileRef>();
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.Files != null)
                foreach (var f in n.Files)
                    result.Add(new FileRef(System.IO.Path.Combine(n.Path, f.Name), f.Size));
            foreach (var c in n.Children) stack.Push(c);
        }
        return result;
    }

    // ========================================================================
    //  Глобальная развёртка: раскладываем всё дерево на «единицы» —
    //  игры/программы (папки-юниты) и тяжёлые файлы, спускаясь сквозь
    //  папки-контейнеры (games → steam → steamapps → common → игры).
    // ========================================================================
    public sealed class ExplodeItem
    {
        public required string Path;
        public required string Name;
        public long Size;
        public bool IsFile;
        public DirNode? Node;   // для папок — чтобы можно было перейти к ним
    }

    /// <param name="resolveUnit">Опционально: по узлу-папке возвращает «красивое» имя, если это
    /// известная игра/программа (по базе имён игр — по имени папки, или по реестру установленных
    /// программ — по пути), иначе null. Такие папки не разворачиваем, показываем целиком под этим
    /// именем (даже если внутри есть деление веса).</param>
    public static List<ExplodeItem> GlobalExplode(DirNode root, long? minOverride = null, Func<DirNode, string?>? resolveUnit = null)
    {
        long total = root.Size <= 0 ? 1 : root.Size;
        // Порог «значимой единицы»: масштабируется с размером диска, но НЕ выше 150 МБ —
        // иначе на больших дисках (used/3000) отсекались бы настоящие небольшие игры/программы
        // (~200–300 МБ). Снизу — 50 МБ. Показ ограничен ещё и лимитом в 400 плиток.
        long minSize = minOverride ?? Math.Clamp(total / 3000, 50L << 20, 150L << 20);
        var items = new List<ExplodeItem>();
        foreach (var child in root.Children)
            Explode(child, minSize, fromCollection: false, items, resolveUnit);
        SurfaceBigFiles(root, minSize, items); // крупные файлы прямо в корне
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return items;
    }

    static void Explode(DirNode f, long minSize, bool fromCollection, List<ExplodeItem> items, Func<DirNode, string?>? resolveUnit)
    {
        var cur = f;
        while (true)
        {
            // Известная игра/программа (по базе имён игр или по реестру программ) — не углубляемся,
            // показываем целиком и с «правильным» именем, если оно есть.
            if (resolveUnit != null && cur.Size >= minSize)
            {
                var dn = resolveUnit(cur);
                if (!string.IsNullOrEmpty(dn))
                {
                    items.Add(new ExplodeItem { Path = cur.Path, Name = dn!, Size = cur.Size, IsFile = false, Node = cur });
                    return;
                }
            }

            var sig = cur.Children.Where(c => c.Size >= minSize).OrderByDescending(c => c.Size).ToList();

            // Крупнейшая подпапка забирает почти весь вес (всё остальное < 15%) — папка
            // проходная. Спускаемся дальше, СОХРАНЯЯ имя старшей папки: если ниже так и не
            // будет значимого деления веса, покажем именно её (BlackMythWukong, а не b1/Content).
            if (sig.Count >= 1 && cur.Size - sig[0].Size < cur.Size * 0.15)
            {
                cur = sig[0];
                continue;
            }

            if (sig.Count >= 2)
            {
                // коллекция (много сопоставимых подпапок): разворачиваем каждую + крупные файлы
                foreach (var cf in sig) Explode(cf, minSize, fromCollection: true, items, resolveUnit);
                SurfaceBigFiles(cur, minSize, items);
            }
            else if (sig.Count == 1 && !fromCollection)
            {
                // одна крупная подпапка + прочее на верхнем уровне: разворачиваем её и всплываем крупные файлы
                Explode(sig[0], minSize, fromCollection: true, items, resolveUnit);
                SurfaceBigFiles(cur, minSize, items);
            }
            else if (!fromCollection && sig.Count == 0 && IsFileHeavy(f, minSize))
            {
                SurfaceBigFiles(f, minSize, items);   // downloads: всплывают файлы
            }
            else if (f.Size >= minSize)
            {
                // цельная «единица» — игра, программа, папка-мешок. Только если не мельче порога:
                // прямые дети корня попадают сюда любого размера, и без этой проверки пустые/крошечные
                // папки диска (Program Files, WindowsApps, Work…) просачивались бы в общий вид.
                items.Add(new ExplodeItem { Path = f.Path, Name = f.Name, Size = f.Size, IsFile = false, Node = f });
            }
            // иначе — мельче порога: уходит в остаток «прочее» (учитывается его суммой)
            return;
        }
    }

    // ========================================================================
    //  База нормализованных имён игр/программ (для распознавания папок).
    // ========================================================================
    public sealed class GameNameIndex
    {
        public const int MinLen = 8; // минимальная длина имени, чтобы избежать ложных совпадений

        readonly HashSet<string> _names = new(StringComparer.Ordinal);
        static readonly HashSet<string> Block = new(StringComparer.Ordinal)
        {
            "programfiles", "programfilesx86", "windows", "system", "system32", "users",
            "documents", "downloads", "desktop", "pictures", "music", "videos", "appdata",
            "temp", "steamlibrary", "steamapps", "recyclebin", "onedrive", "programdata"
        };

        public int Count => _names.Count;

        /// <summary>Нижний регистр, только буквы и цифры (пробелы/спецсимволы убираются).</summary>
        public static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        public void AddNormalized(string norm)
        {
            if (norm.Length >= MinLen) _names.Add(norm);
        }

        public void Add(string rawName) => AddNormalized(Normalize(rawName));

        /// <summary>Папка похожа на известную игру, если её имя начинается с известного
        /// названия длиной ≥ MinLen (учитывает суффиксы вида «v1.0.0.10», «GOTY» и т.п.).</summary>
        public bool Matches(string folderName)
        {
            var f = Normalize(folderName);
            if (f.Length < MinLen || Block.Contains(f)) return false;
            for (int len = f.Length; len >= MinLen; len--)
                if (_names.Contains(f.Substring(0, len)))
                    return true;
            return false;
        }
    }

    static bool IsFileHeavy(DirNode f, long minSize)
    {
        if (f.Files == null || f.Size <= 0) return false;
        long big = 0;
        foreach (var x in f.Files) if (x.Size >= minSize) big += x.Size;
        return big >= f.Size * 0.5;
    }

    static void SurfaceBigFiles(DirNode f, long minSize, List<ExplodeItem> items)
    {
        if (f.Files == null) return;
        foreach (var x in f.Files)
            if (x.Size >= minSize)
                items.Add(new ExplodeItem { Path = System.IO.Path.Combine(f.Path, x.Name), Name = x.Name, Size = x.Size, IsFile = true });
    }
}

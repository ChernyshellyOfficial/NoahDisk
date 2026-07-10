using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpaceSaver;

// ============================================================================
//  Общий код сканирования — используется и консолью, и GUI.
// ============================================================================

/// <summary>Файл: имя и размер (для учёта и отчёта по файлам).</summary>
public readonly record struct FileItem(string Name, long Size);

/// <summary>Папка и её суммарный размер.</summary>
public sealed class DirNode
{
    public required string Path;
    public required string Name;
    public long Size;        // суммарный размер папки (рекурсивно), байт
    public long FileCount;   // файлов рекурсивно
    public long DirCount;    // вложенных папок рекурсивно
    public long OwnFileSize; // размер файлов, лежащих прямо в этой папке
    public DirNode? Parent;
    public List<DirNode> Children = new();

    // Прямые файлы этой папки — заполняются только при сканировании с collectFiles (в GUI).
    public List<FileItem>? Files;

    // Для синтетических узлов «остаток» при умной развёртке: исходная свёрнутая папка.
    public DirNode? UnwrapSource;
}


/// <summary>Рекурсивный обход файловой системы с подсчётом размеров.</summary>
public static class Scanner
{
    public static DirNode ScanRoot(DirectoryInfo dir, ScanStats stats, bool collectFiles = false)
    {
        var root = new DirNode { Path = dir.FullName, Name = NiceName(dir) };
        var subdirs = new List<DirectoryInfo>();
        EnumerateInto(dir, root, subdirs, stats, collectFiles);

        // Верхнеуровневые подпапки считаем параллельно — обычно там и сидит основной объём.
        var childResults = new DirNode?[subdirs.Count];
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) };
        Parallel.For(0, subdirs.Count, po, i =>
        {
            childResults[i] = ScanRecursive(subdirs[i], stats, collectFiles);
        });

        foreach (var c in childResults)
        {
            if (c is null) continue;
            c.Parent = root;
            root.Children.Add(c);
            root.Size += c.Size;
            root.FileCount += c.FileCount;
            root.DirCount += 1 + c.DirCount;
        }
        return root;
    }

    // Предел глубины рекурсии — защита от StackOverflowException (он неперехватываемый и
    // роняет процесс) на патологически глубоких деревьях (напр. при включённой поддержке
    // длинных путей). 512 уровней заведомо больше любой реальной структуры папок и умещается
    // в стек потока (~1 МБ). Глубже — не спускаемся, недосчитанное помечаем ошибкой.
    const int MaxDepth = 512;

    static DirNode ScanRecursive(DirectoryInfo dir, ScanStats stats, bool collectFiles, int depth = 0)
    {
        var node = new DirNode { Path = dir.FullName, Name = dir.Name };
        var subdirs = new List<DirectoryInfo>();
        EnumerateInto(dir, node, subdirs, stats, collectFiles);

        if (depth >= MaxDepth)
        {
            if (subdirs.Count > 0) stats.AddError();   // слишком глубоко — дальше не идём
            return node;
        }

        foreach (var sd in subdirs)
        {
            var child = ScanRecursive(sd, stats, collectFiles, depth + 1);
            child.Parent = node;
            node.Children.Add(child);
            node.Size += child.Size;
            node.FileCount += child.FileCount;
            node.DirCount += 1 + child.DirCount;
        }
        return node;
    }

    // Один уровень: складывает размеры файлов в node, а подпапки добавляет в subdirs.
    static void EnumerateInto(DirectoryInfo dir, DirNode node, List<DirectoryInfo> subdirs, ScanStats stats, bool collectFiles)
    {
        IEnumerator<FileSystemInfo> e;
        try
        {
            e = dir.EnumerateFileSystemInfos().GetEnumerator();
        }
        catch (UnauthorizedAccessException) { stats.AddDenied(); return; }
        catch (Exception) { stats.AddError(); return; }

        using (e)
        {
            while (true)
            {
                try
                {
                    if (!e.MoveNext()) break;
                }
                catch (UnauthorizedAccessException) { stats.AddDenied(); break; }
                catch (Exception) { stats.AddError(); break; }

                var entry = e.Current;
                try
                {
                    // junction / симлинк — пропускаем, чтобы не зациклиться и не считать дважды.
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        stats.AddReparse();
                        continue;
                    }

                    if (entry is FileInfo f)
                    {
                        long len = f.Length;
                        node.OwnFileSize += len;
                        node.Size += len;
                        node.FileCount++;
                        if (collectFiles) (node.Files ??= new()).Add(new FileItem(f.Name, len));
                        stats.AddFile(len);
                    }
                    else if (entry is DirectoryInfo d)
                    {
                        subdirs.Add(d);
                        stats.AddDir();
                    }
                }
                catch (Exception) { stats.AddError(); }
            }
        }
    }

    static string NiceName(DirectoryInfo dir)
        => string.IsNullOrEmpty(dir.Name) ? dir.FullName : dir.Name;
}


/// <summary>Потокобезопасная статистика для индикатора прогресса.</summary>
public sealed class ScanStats
{
    long _files, _bytes;
    int _dirs, _denied, _reparse, _errors;

    public long Files => Interlocked.Read(ref _files);
    public long Bytes => Interlocked.Read(ref _bytes);
    public int Denied => Volatile.Read(ref _denied);
    public int Reparse => Volatile.Read(ref _reparse);
    public int Errors => Volatile.Read(ref _errors);

    public void AddFile(long len) { Interlocked.Increment(ref _files); Interlocked.Add(ref _bytes, len); }
    public void AddDir() => Interlocked.Increment(ref _dirs);
    public void AddDenied() => Interlocked.Increment(ref _denied);
    public void AddReparse() => Interlocked.Increment(ref _reparse);
    public void AddError() => Interlocked.Increment(ref _errors);
}


/// <summary>Форматирование чисел и размеров.</summary>
public static class Format
{
    static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };

    public static string Size(long bytes)
    {
        double b = bytes;
        int u = 0;
        while (b >= 1024 && u < Units.Length - 1) { b /= 1024; u++; }

        string num = u == 0 || b >= 100 ? b.ToString("0", CultureInfo.InvariantCulture)
                   : b >= 10 ? b.ToString("0.0", CultureInfo.InvariantCulture)
                   : b.ToString("0.00", CultureInfo.InvariantCulture);
        return $"{num} {Units[u]}";
    }

    public static string Count(long n)
        => n.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');

    public static string Percent(long part, long whole)
        => whole <= 0 ? "0%" : ((double)part / whole * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    // Русское склонение: 1 папка, 2 папки, 5 папок.
    public static string Plural(long n, string one, string few, string many)
    {
        long a = Math.Abs(n) % 100;
        long b = a % 10;
        if (a is > 10 and < 20) return many;
        if (b is > 1 and < 5) return few;
        if (b == 1) return one;
        return many;
    }
}

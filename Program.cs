using System.Diagnostics;
using System.Globalization;
using System.Text;
using SpaceSaver;

// ============================================================================
//  SpaceSaver (консоль) — кто съел место на диске.
//  Сканирование живёт в Scanner.cs, текстовый отчёт — в TextReport.cs
//  (их же использует GUI-версия).
// ============================================================================

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* перенаправленный вывод */ }
try { Console.InputEncoding = Encoding.UTF8; } catch { }

var options = Options.Parse(args);
if (options.ShowHelp)
{
    Options.PrintHelp();
    return 0;
}

string? target = options.Path;
if (string.IsNullOrWhiteSpace(target))
{
    Console.Write("Укажи путь к папке (Enter — текущая папка): ");
    var input = Console.ReadLine();
    target = string.IsNullOrWhiteSpace(input) ? Directory.GetCurrentDirectory() : input;
}
target = target.Trim().Trim('"');

if (File.Exists(target))
{
    Console.Error.WriteLine($"Это файл, а нужна папка: {target}");
    return 1;
}
if (!Directory.Exists(target))
{
    Console.Error.WriteLine($"Папка не найдена: {target}");
    return 1;
}

var rootInfo = new DirectoryInfo(target);

Console.WriteLine();
Console.WriteLine($"SpaceSaver — сканирую: {rootInfo.FullName}");

var stats = new ScanStats();
var sw = Stopwatch.StartNew();

using var cts = new CancellationTokenSource();
var progressTask = Task.Run(() => Progress.Run(stats, cts.Token));

var root = Scanner.ScanRoot(rootInfo, stats);

cts.Cancel();
try { progressTask.Wait(); } catch { }
Progress.Clear();
sw.Stop();

Report.Print(root, stats, sw.Elapsed, options);
return 0;


// ============================================================================
//  Живой индикатор во время сканирования.
// ============================================================================
static class Progress
{
    static readonly char[] Spin = { '|', '/', '-', '\\' };

    public static void Run(ScanStats stats, CancellationToken ct)
    {
        if (Console.IsOutputRedirected) return;
        int i = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Console.Write($"\r  {Spin[i++ % Spin.Length]} {Format.Count(stats.Files)} файлов, {Format.Size(stats.Bytes)}        ");
            }
            catch { }
            if (ct.WaitHandle.WaitOne(120)) break;
        }
    }

    public static void Clear()
    {
        if (Console.IsOutputRedirected) return;
        try { Console.Write("\r" + new string(' ', 64) + "\r"); } catch { }
    }
}


// ============================================================================
//  Разбор аргументов командной строки.
// ============================================================================
sealed class Options
{
    public string? Path;
    public int TreeDepth = 3;
    public int TopN = 12;
    public bool ShowTree = true;
    public bool ShowTop = true;
    public bool ShowHelp = false;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help": o.ShowHelp = true; break;
                case "--depth":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var d)) o.TreeDepth = Math.Max(0, d);
                    break;
                case "--top":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var t)) o.TopN = Math.Max(0, t);
                    break;
                case "--no-tree": o.ShowTree = false; break;
                case "--no-top": o.ShowTop = false; break;
                default: positional.Add(args[i]); break;
            }
        }

        if (positional.Count == 1)
        {
            o.Path = positional[0];
        }
        else if (positional.Count > 1)
        {
            var joined = string.Join(' ', positional);
            o.Path = Directory.Exists(joined) ? joined : positional[0];
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
@"SpaceSaver — кто съел место на диске.

Использование:
  SpaceSaver <путь> [опции]
  SpaceSaver                  (спросит путь)

Опции:
  --depth N     глубина дерева ""тяжёлых веток"" (по умолчанию 3, 0 — выключить)
  --top N       сколько крупнейших папок показать (по умолчанию 12)
  --no-tree     не показывать дерево тяжёлых веток
  --no-top      не показывать список крупнейших папок
  -h, --help    эта справка

Подсказка: можно просто перетащить папку на SpaceSaver.exe.");
    }
}


// ============================================================================
//  Печать отчёта в консоль (тело отчёта — из общего TextReport).
// ============================================================================
static class Report
{
    public static void Print(DirNode root, ScanStats stats, TimeSpan elapsed, Options opt)
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 60));
        Console.WriteLine($"  {root.Path}");
        Console.WriteLine($"  Всего: {Format.Size(root.Size)}   " +
                          $"({Format.Count(root.FileCount)} файлов, {Format.Count(root.DirCount)} папок)   " +
                          $"за {elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} с");
        Console.WriteLine(new string('═', 60));
        Console.WriteLine();

        Console.Write(TextReport.Build(root, opt.ShowTree, opt.TreeDepth, opt.ShowTop, opt.TopN));

        PrintNotes(stats);

        Console.WriteLine();
        Console.WriteLine("SpaceSaver · автор: Chernyshelly · github.com/ChernyshellyOfficial");
    }

    static void PrintNotes(ScanStats stats)
    {
        if (stats.Denied == 0 && stats.Reparse == 0 && stats.Errors == 0) return;
        var parts = new List<string>();
        if (stats.Denied > 0) parts.Add($"{stats.Denied} без доступа");
        if (stats.Reparse > 0) parts.Add($"{stats.Reparse} ссылок/junction");
        if (stats.Errors > 0) parts.Add($"{stats.Errors} ошибок чтения");
        Console.WriteLine($"Пропущено: {string.Join(", ", parts)}.");
    }
}

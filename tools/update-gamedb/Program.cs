using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NoahDisk;

// Обновляет встроенную базу имён игр. Качает свежий снимок списка приложений Steam
// (GitHub-зеркало — официальный api.steampowered.com в ряде сетей отдаёт «Method not
// found», поэтому берём зеркало) и перепаковывает gui/steam_names.txt.gz.
//
//   dotnet run --project tools/update-gamedb                 (качает список сам)
//   dotnet run --project tools/update-gamedb -- games.json   (берёт заранее скачанный JSON)
//
// Имена нормализуются той же функцией Analysis.GameNameIndex.Normalize, что и матчинг
// папок, — так встроенный список гарантированно согласован с логикой распознавания.

const string MirrorUrl = "https://raw.githubusercontent.com/jsnli/steamAppIDList/master/data/games_appid.json";

// Необязательный аргумент .json — использовать локальный файл вместо скачивания
// (пригодится, если из этой сети GitHub/Steam недоступны — скачай список вручную браузером).
string? srcFile = args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
if (srcFile != null && !File.Exists(srcFile)) throw new FileNotFoundException("Нет файла: " + srcFile);

string? root = FindRepoRoot();
string outGz = args.FirstOrDefault(a => a.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
    ?? (root != null ? Path.Combine(root, "gui", "steam_names.txt.gz")
        : throw new Exception("Не нашёл корень репозитория. Укажи путь к steam_names.txt.gz аргументом."));

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("NoahDisk-update-gamedb/1.0");
Console.WriteLine(srcFile != null ? $"Читаю {srcFile} …" : $"Скачиваю {MirrorUrl} …");
using var stream = srcFile != null ? File.OpenRead(srcFile) : await http.GetStreamAsync(MirrorUrl);
using var doc = await JsonDocument.ParseAsync(stream);

var set = new HashSet<string>(StringComparer.Ordinal);
foreach (var el in doc.RootElement.EnumerateArray())
    if (el.TryGetProperty("name", out var n) && n.GetString() is string name)
    {
        var norm = Analysis.GameNameIndex.Normalize(name);
        if (norm.Length >= Analysis.GameNameIndex.MinLen) set.Add(norm);
    }

var sorted = set.ToList();
sorted.Sort(StringComparer.Ordinal);

Directory.CreateDirectory(Path.GetDirectoryName(outGz)!);
using (var outFs = File.Create(outGz))
using (var gz = new GZipStream(outFs, CompressionLevel.SmallestSize))
using (var sw = new StreamWriter(gz))
    foreach (var s in sorted) sw.WriteLine(s);

Console.WriteLine($"Готово: {sorted.Count} имён → {outGz} ({new FileInfo(outGz).Length / 1024} КБ)");

// Ищем корень репозитория вверх по дереву — по наличию gui/NoahDisk.Gui.csproj.
static string? FindRepoRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "gui", "NoahDisk.Gui.csproj")))
            return d.FullName;
    return null;
}

using SpaceSaver;

// Мини-тесты «умной развёртки» (Analysis.SmartUnwrap) на синтетических деревьях.

const long GB = 1L << 30;
int failed = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
    if (!ok) failed++;
}

static DirNode D(string name, long size, params DirNode[] kids)
{
    var n = new DirNode { Path = @"X:\" + name, Name = name, Size = size };
    n.Children.AddRange(kids);
    return n;
}

// Лист с детьми, чтобы узел считался «папкой» (Children.Count > 0).
static DirNode Folder(string name, long size) => D(name, size, D(name + "/_", size));

// Простая папка с заданным размером (для планов удаления).
static DirNode F(string name, long bytes) => new DirNode { Path = @"X:\" + name, Name = name, Size = bytes };

Console.WriteLine("SmartUnwrap tests:");

// --- Сценарий 1: Games/Steam/steamapps/common/[игры] ---
var common = D("common", 285 * GB,
    Folder("Wukong", 140 * GB),
    Folder("CP2077", 90 * GB),
    Folder("Dota", 55 * GB));
var steamapps = D("steamapps", 290 * GB, common, Folder("workshop", 5 * GB));
var steam = D("Steam", 300 * GB, steamapps, Folder("config", 10 * GB));
var games = D("Games", 380 * GB, steam, Folder("GameA", 50 * GB), Folder("GameB", 30 * GB));

var r = Analysis.SmartUnwrap(steam);
Check("спуск дошёл до common", r.SplitNode.Name == "common");
Check("развернулось ровно 3 игры", r.Promoted.Count == 3);
Check("игры именно те", r.Promoted.Select(p => p.Name).OrderBy(x => x)
        .SequenceEqual(new[] { "CP2077", "Dota", "Wukong" }));
Check("использованный размер = 285 ГБ", r.UsedSize == 285 * GB);
Check("остаток (Steam - использованное) = 15 ГБ", steam.Size - r.UsedSize == 15 * GB);

// --- Сценарий 2: уже разделённая папка не спускается ---
var r2 = Analysis.SmartUnwrap(games);
Check("Games не спускается (вес уже разделён)", r2.SplitNode.Name == "Games");
Check("Games разворачивается в 3 верхних папки", r2.Promoted.Count == 3);

// --- Сценарий 3: цепочка из единственного ребёнка-листа не ныряет в лист ---
var leafChild = D("OnlyChild", 100 * GB); // без детей -> лист
var wrap = D("Wrap", 100 * GB, leafChild);
var r3 = Analysis.SmartUnwrap(wrap);
Check("не ныряем в лист, останавливаемся на Wrap", r3.SplitNode.Name == "Wrap");
Check("Wrap разворачивается в свой единственный лист", r3.Promoted.Count == 1 && r3.Promoted[0].Name == "OnlyChild");

// --- Сценарий 4: пустая папка ---
var empty = D("Empty", 0);
var r4 = Analysis.SmartUnwrap(empty);
Check("пустая папка -> нет развёртки", r4.Promoted.Count == 0 && r4.UsedSize == 0);

Console.WriteLine();
Console.WriteLine("CleanupPlan tests:");

var cands = new List<DirNode>
{
    F("A", 100 * GB), F("B", 60 * GB), F("C", 50 * GB), F("D", 20 * GB), F("E", 15 * GB),
    F("F", 10 * GB), F("G", 5 * GB), F("H", 3 * GB), F("I", 2 * GB), F("J", 1 * GB)
};

var c1 = Analysis.PlanClosestFew(cands, 55 * GB);
Check("closestFew(55) → 2 папки, ровно 55 ГБ", c1.Folders.Count == 2 && c1.Freed == 55 * GB && c1.ReachedTarget);

var c2 = Analysis.PlanClosestFew(cands, 95 * GB);
Check("closestFew(95) → 1 папка 100 ГБ", c2.Folders.Count == 1 && c2.Freed == 100 * GB);

var c3 = Analysis.PlanClosestFew(cands, 120 * GB);
Check("closestFew(120) → пара 100+20", c3.Folders.Count == 2 && c3.Freed == 120 * GB);

var c4 = Analysis.PlanClosestFew(new List<DirNode>(), 10 * GB);
Check("closestFew(пусто) → пусто", c4.Folders.Count == 0 && c4.Freed == 0);

var g1 = Analysis.PlanGreedyCapped(cands, 40 * GB, 25 * GB);
Check("greedy(need40,cap25) → {20,15,10}=45", g1.Folders.Count == 3 && g1.Freed == 45 * GB && g1.ReachedTarget);

var g2 = Analysis.PlanGreedyCapped(cands, 200 * GB, 1000 * GB);
Check("greedy(need200) → {100,60,50}=210", g2.Folders.Count == 3 && g2.Freed == 210 * GB && g2.ReachedTarget);

var g3 = Analysis.PlanGreedyCapped(cands, 500 * GB, 1000 * GB);
Check("greedy(need500) → не хватает, все 10", g3.Folders.Count == 10 && g3.Freed == 266 * GB && !g3.ReachedTarget);

var g4 = Analysis.PlanGreedyCapped(cands, 80 * GB, 5 * GB);
Check("greedy(cap5) → только мелкие ≤5", g4.Folders.All(f => f.Size <= 5 * GB));

Console.WriteLine();
Console.WriteLine("CollectFiles tests:");
var cfRoot = new DirNode { Path = @"X:\A", Name = "A", Files = new() { new FileItem("a.txt", 10), new FileItem("b.bin", 20) } };
var cfSub = new DirNode { Path = @"X:\A\sub", Name = "sub", Files = new() { new FileItem("c.dat", 30) } };
cfRoot.Children.Add(cfSub);
var cf = Analysis.CollectFiles(cfRoot);
Check("собрано 3 файла из поддерева", cf.Count == 3);
Check("суммарный размер файлов = 60", cf.Sum(x => x.Size) == 60);
Check("путь вложенного файла корректен", cf.Any(x => x.Path == @"X:\A\sub\c.dat"));
Check("папка без Files не ломает сбор", Analysis.CollectFiles(new DirNode { Path = "Y", Name = "Y" }).Count == 0);

Console.WriteLine();
Console.WriteLine("GlobalExplode tests:");

static DirNode Dir(string name, long size, params DirNode[] kids)
{
    var n = new DirNode { Path = @"D:\" + name, Name = name, Size = size };
    n.Children.AddRange(kids);
    return n;
}
static DirNode FileDir(string name, long size, params (string n, long s)[] fs)
{
    var n = new DirNode { Path = @"D:\" + name, Name = name, Size = size, Files = new() };
    foreach (var (fn, sz) in fs) n.Files.Add(new FileItem(fn, sz));
    return n;
}

var geCommon = Dir("common", 600,
    FileDir("Wukong", 300, ("game.pak", 300)),
    FileDir("CP2077", 200, ("data.bin", 200)),
    FileDir("Dota", 100, ("d.dat", 100)));
var geGames = Dir("games", 880,
    Dir("steam", 600, Dir("steamapps", 600, geCommon)),
    FileDir("GameA", 180, ("a.pak", 180)),
    FileDir("GameB", 100, ("b.pak", 100)));
var gePrograms = Dir("programs", 300,
    Dir("development", 300, FileDir("VS", 200, ("vs.exe", 200)), FileDir("Rider", 100, ("r.exe", 100))));
var geDownloads = FileDir("downloads", 231, ("movie.mkv", 150), ("installer.exe", 80), ("note.txt", 1));
var gRoot = Dir("D", 1411, geGames, gePrograms, geDownloads);

var g = Analysis.GlobalExplode(gRoot, 50);
var gn = g.Select(i => i.Name).ToList();
Check("развёрнуто ровно 9 значимых элементов", g.Count == 9);
Check("игры из steam/common всплыли", gn.Contains("Wukong") && gn.Contains("CP2077") && gn.Contains("Dota"));
Check("игры прямо из games всплыли", gn.Contains("GameA") && gn.Contains("GameB"));
Check("программы всплыли", gn.Contains("VS") && gn.Contains("Rider"));
Check("тяжёлые файлы из downloads всплыли", gn.Contains("movie.mkv") && gn.Contains("installer.exe"));
Check("контейнеры НЕ всплыли", !gn.Contains("games") && !gn.Contains("steam") && !gn.Contains("common") && !gn.Contains("downloads") && !gn.Contains("programs"));
Check("файлы внутри игр не всплыли отдельно", !gn.Contains("game.pak") && !gn.Contains("a.pak"));
Check("игра — папка, а movie — файл", !g.First(i => i.Name == "Wukong").IsFile && g.First(i => i.Name == "movie.mkv").IsFile);

// глубокая одноцепочечная игра: BlackMythWukong(138) → b1(138) → Content(138) → Paks(138)
var bmw = Dir("BlackMythWukong", 138, Dir("b1", 138, Dir("Content", 138, FileDir("Paks", 138, ("a.pak", 138)))));
var other = FileDir("OtherGame", 160, ("g.pak", 160));
var deepRoot = Dir("root", 298, Dir("games2", 298, bmw, other));
var ge2 = Analysis.GlobalExplode(deepRoot, 50).Select(i => i.Name).ToList();
Check("глубокая цепочка всплывает именем СТАРШЕЙ папки (BlackMythWukong)", ge2.Contains("BlackMythWukong"));
Check("глубокие звенья цепочки НЕ всплывают", !ge2.Contains("b1") && !ge2.Contains("Content") && !ge2.Contains("Paks"));
Check("файл-игра из коллекции — как папка, не как файл", ge2.Contains("OtherGame") && !ge2.Contains("g.pak"));

Console.WriteLine(failed == 0 ? "\nИТОГ: все тесты прошли ✔" : $"\nИТОГ: провалено {failed}");
return failed == 0 ? 0 : 1;

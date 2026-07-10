using NoahDisk;

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

// регресс: если крупный элемент один достигает цели — не возвращать мелкий недобор
var cUnder = new List<DirNode> { F("Big", 1000 * GB), F("Tiny", 2 * GB) };
var c5 = Analysis.PlanClosestFew(cUnder, 100 * GB);
Check("closestFew: крупный достигает цели, а не мелкий-недобор", c5.Folders.Count == 1 && c5.Folders[0].Name == "Big" && c5.ReachedTarget);

// цель недостижима ≤2 элементами → лучший по объёму, ReachedTarget=false
var c6 = Analysis.PlanClosestFew(cUnder, 5000 * GB);
Check("closestFew: цель недостижима → пара крупнейших, не достигнуто", c6.Freed == 1002 * GB && !c6.ReachedTarget);

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

// Порог по умолчанию зажат сверху 150 МБ: на большом диске (~900 ГБ, used/3000 ≈ 307 МБ)
// небольшая игра в коллекции всё равно всплывает (иначе улетала бы в «прочее»).
const long MB = 1L << 20;
var bigGame = FileDir("BigGame", 200 * MB, ("g.pak", 200 * MB));
var smallGame = FileDir("SmallGame", 100 * MB, ("s.pak", 100 * MB));
var hColl = Dir("Hcoll", 300 * MB, bigGame, smallGame);
long padSize = 900L * GB;
var padDir = FileDir("pad", padSize, ("blob.bin", padSize));
var bigDisk = Dir("Ddisk", padSize + 300 * MB, hColl, padDir);
var bd = Analysis.GlobalExplode(bigDisk).Select(i => i.Name).ToList();   // порог по умолчанию
Check("порог зажат сверху: игра 200 МБ всплывает на диске ~900 ГБ", bd.Contains("BigGame"));
Check("порог: игра 100 МБ уходит в остаток", !bd.Contains("SmallGame"));

// Мелкие/пустые папки ВЕРХНЕГО УРОВНЯ тоже подчиняются порогу (раньше просачивались как «единицы»).
var tinyTop = Dir("root3", 1000,
    FileDir("BigThing", 900, ("b.bin", 900)),
    Dir("Work", 30, Dir("WorkSub", 30)),   // 30 Б < порога 50
    Dir("EmptyTop", 0));                     // 0 Б (напр. нет доступа)
var tt = Analysis.GlobalExplode(tinyTop, 50).Select(i => i.Name).ToList();
Check("мелкая папка верхнего уровня (30 Б) не всплывает", !tt.Contains("Work"));
Check("пустая папка (0 Б) не всплывает", !tt.Contains("EmptyTop"));
Check("крупное содержимое всё равно всплывает", tt.Contains("b.bin"));

Console.WriteLine();
Console.WriteLine("GameNameIndex tests:");
var idx = new Analysis.GameNameIndex();
idx.Add("Core Keeper");
idx.Add("Black Myth: Wukong");
idx.Add("Cyberpunk 2077");
idx.Add("Go");                 // короткое — не должно попасть в базу
Check("точное имя матчится", idx.Matches("Cyberpunk 2077"));
Check("имя с суффиксом версии матчится", idx.Matches("Core Keeper v1.0.0.10"));
Check("имя со спецсимволами матчится", idx.Matches("Black Myth Wukong"));
Check("неизвестная папка не матчится", !idx.Matches("SomeRandomFolder"));
Check("системная папка (block-list) не матчится", !idx.Matches("Program Files"));
Check("короткое имя не попало в базу", !idx.Matches("Going") && idx.Count == 3);

// интеграция: игра с внутренним делением
var ck = Dir("Core Keeper v1.0.0.10", 200, FileDir("DataA", 100, ("a", 100)), FileDir("DataB", 100, ("b", 100)));
var ckRoot = Dir("root", 360, Dir("commonC", 360, ck, FileDir("SomeGame Deluxe", 160, ("g", 160))));
var noBase = Analysis.GlobalExplode(ckRoot, 50).Select(i => i.Name).ToList();
Check("без базы игра с делением рассыпается", noBase.Contains("DataA") && noBase.Contains("DataB") && !noBase.Contains("Core Keeper v1.0.0.10"));
Func<DirNode, string?> byName = n => idx.Matches(n.Name) ? n.Name : null;
var withBase = Analysis.GlobalExplode(ckRoot, 50, byName).Select(i => i.Name).ToList();
Check("с базой игра НЕ рассыпается (Core Keeper целиком)", withBase.Contains("Core Keeper v1.0.0.10") && !withBase.Contains("DataA"));

Console.WriteLine();
Console.WriteLine("GlobalExplode resolver (путь + красивое имя) tests:");

// папка-программа с внутренним делением веса (Binaries + Content)
var progDir = Dir("BlackMythWukong", 200, FileDir("Binaries", 100, ("g.exe", 100)), FileDir("Content", 100, ("a.pak", 100)));
var progRoot = Dir("root2", 360, Dir("commonX", 360, progDir, FileDir("Other Big App", 160, ("o", 160))));

var noRes = Analysis.GlobalExplode(progRoot, 50).Select(i => i.Name).ToList();
Check("без резолвера папка-программа рассыпается", !noRes.Contains("BlackMythWukong") && (noRes.Contains("Binaries") || noRes.Contains("Content")));

// резолвер «по пути» (как реестр установленных программ) возвращает красивое имя
Func<DirNode, string?> byPath = n => n.Path == @"D:\BlackMythWukong" ? "Black Myth: Wukong" : null;
var withRes = Analysis.GlobalExplode(progRoot, 50, byPath).ToList();
var resNames = withRes.Select(i => i.Name).ToList();
Check("по пути папка-программа показана целиком с красивым именем",
    resNames.Contains("Black Myth: Wukong") && !resNames.Contains("Binaries") && !resNames.Contains("Content"));
Check("узел сохраняет реальный путь для перехода/проводника",
    withRes.First(i => i.Name == "Black Myth: Wukong").Node!.Path == @"D:\BlackMythWukong");

Console.WriteLine();
Console.WriteLine("Steam-как-программа (разворачивать в игры, не одной плиткой) tests:");
// Steam зарегистрирован как программа (путь совпадает), но содержит steamapps → раскладываем в игры.
var steamGames = Dir("common", 600,
    FileDir("GameA", 300, ("a.pak", 300)),
    FileDir("GameB", 200, ("b.pak", 200)),
    FileDir("GameC", 100, ("c.pak", 100)));
var steamApps = Dir("steamapps", 610, steamGames, FileDir("workshop", 10, ("w", 10)));
var steamRoot = Dir("Steam", 640, steamApps, FileDir("clientbin", 30, ("steam.exe", 30)));
var diskRoot = Dir("disk", 640, steamRoot);
// Резолвер как в приложении: программа по пути, НО не игровая библиотека (внутри есть steamapps).
Func<DirNode, string?> steamRes = n =>
{
    bool isLib = n.Children.Any(c => c.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase));
    return (n.Path == @"D:\Steam" && !isLib) ? "Steam" : null;
};
var se = Analysis.GlobalExplode(diskRoot, 50, steamRes).Select(i => i.Name).ToList();
Check("Steam НЕ показан одной плиткой", !se.Contains("Steam"));
Check("игры из Steam всплыли по отдельности", se.Contains("GameA") && se.Contains("GameB") && se.Contains("GameC"));

Console.WriteLine(failed == 0 ? "\nИТОГ: все тесты прошли ✔" : $"\nИТОГ: провалено {failed}");
return failed == 0 ? 0 : 1;

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

Console.WriteLine(failed == 0 ? "\nИТОГ: все тесты прошли ✔" : $"\nИТОГ: провалено {failed}");
return failed == 0 ? 0 : 1;

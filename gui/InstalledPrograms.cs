using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace NoahDisk.Gui;

// Установленные программы из реестра Windows (ключи Uninstall): точный путь установки
// (InstallLocation) → отображаемое имя (DisplayName). Используется в глобальном скане,
// чтобы узнавать папку-программу ПО ПУТИ и показывать её целиком с правильным именем
// («System Informer», а не папку «ProcHack»). Только чтение реестра, без прав администратора.
static class InstalledPrograms
{
    static readonly object _lock = new();
    static Dictionary<string, string>? _byPath;   // нормализованный путь -> имя

    // Слишком широкие расположения не считаем «программой» — иначе InstallLocation вида
    // «C:\Program Files» или «…\steamapps\common» проглотил бы кучу всего одной плиткой.
    static readonly HashSet<string> BroadNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "program files", "program files (x86)", "windows", "system32", "syswow64",
        "programdata", "users", "public", "appdata", "local", "locallow", "roaming",
        "temp", "steam", "steamapps", "common", "steamlibrary", "downloads", "documents",
        "desktop", "$recycle.bin", "windowsapps", "microsoft", "package cache"
    };

    /// <summary>Имя программы, если папка по этому пути — установленная программа; иначе null.</summary>
    public static string? Resolve(string path)
    {
        Ensure();
        var key = Normalize(path);
        return key.Length > 0 && _byPath!.TryGetValue(key, out var n) ? n : null;
    }

    public static int Count { get { Ensure(); return _byPath!.Count; } }

    static void Ensure()
    {
        if (_byPath != null) return;
        lock (_lock)
        {
            if (_byPath != null) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Read(RegistryHive.LocalMachine, RegistryView.Registry64, map);
                Read(RegistryHive.LocalMachine, RegistryView.Registry32, map);
                Read(RegistryHive.CurrentUser, RegistryView.Registry64, map);
            }
            catch { /* реестр недоступен — пустая карта, распознавание по пути просто выключится */ }
            _byPath = map;
        }
    }

    static void Read(RegistryHive hive, RegistryView view, Dictionary<string, string> map)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall == null) return;

        foreach (var subName in uninstall.GetSubKeyNames())
        {
            try
            {
                using var k = uninstall.OpenSubKey(subName);
                if (k == null) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue; // скрытые системные
                if (k.GetValue("ParentKeyName") != null) continue;                 // записи обновлений (KB…)

                if (k.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;

                // Путь: сперва InstallLocation; если пуст (Unity, многие MSI/NSIS) — каталог .exe
                // из DisplayIcon, иначе из UninstallString.
                var loc = k.GetValue("InstallLocation") as string;
                if (string.IsNullOrWhiteSpace(loc))
                    loc = FolderFromExe(k.GetValue("DisplayIcon") as string); // только по «настоящему» exe
                if (string.IsNullOrWhiteSpace(loc)) continue;

                var key = Normalize(loc);
                if (key.Length == 0 || IsBroad(key)) continue;
                map[key] = name.Trim();   // при дубле пути — последнее имя, не критично
            }
            catch { /* битая запись — пропускаем */ }
        }
    }

    // Каталог программы из DisplayIcon/UninstallString (когда InstallLocation пуст):
    // вытаскиваем путь к .exe и берём его папку.
    static string? FolderFromExe(string? s)
    {
        var exe = ExtractExePath(s);
        if (exe == null) return null;
        try { var dir = Path.GetDirectoryName(exe); return string.IsNullOrEmpty(dir) ? null : dir; }
        catch { return null; }
    }

    static string? ExtractExePath(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase)) return null; // {GUID}, реального пути нет
        if (s.StartsWith("\"")) { int e = s.IndexOf('"', 1); if (e > 1) s = s.Substring(1, e - 1); } // "путь.exe" /флаги
        int i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) s = s.Substring(0, i + 4);                                      // отрезаем флаги после .exe
        else { int c = s.LastIndexOf(','); if (c > 3 && int.TryParse(s.Substring(c + 1).Trim(), out _)) s = s.Substring(0, c); } // "путь,индекс"
        s = s.Trim().Trim('"').Trim();
        if (s.IndexOf(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase) >= 0) return null; // кэш установщика, не папка программы
        if (!s.Contains(":\\") || s.Length < 4) return null;
        // Деинсталлятор часто лежит НЕ в папке программы (у Steam — в родительской: D:\Games\uninstall.exe),
        // поэтому по нему каталог не выводим — иначе можно пометить как программу пол-диска.
        var file = Path.GetFileNameWithoutExtension(s);
        if (file.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
            file.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        return s;
    }

    // Канонический вид пути для сравнения: без кавычек/хвостовых слэшей, полный путь.
    static string Normalize(string path)
    {
        try
        {
            var p = path.Trim().Trim('"').Trim();
            if (p.Length == 0) return "";
            p = Path.GetFullPath(p).TrimEnd('\\', '/');
            return p;
        }
        catch { return ""; }
    }

    // Широкое расположение: корень диска или папка-контейнер (Program Files, steamapps\common, …).
    static bool IsBroad(string normPath)
    {
        var name = Path.GetFileName(normPath);            // basename без хвостового слэша
        if (string.IsNullOrEmpty(name)) return true;      // корень диска ("C:\") -> ""
        return BroadNames.Contains(name);
    }
}

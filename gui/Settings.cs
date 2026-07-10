using System;
using System.IO;
using System.Text.Json;

namespace NoahDisk.Gui;

// Небольшие настройки, переживающие перезапуск: тема и последняя папка.
// Хранятся в %LOCALAPPDATA%\NoahDisk\settings.json. Любые ошибки — тихо игнорируются.
static class Settings
{
    sealed class Data
    {
        public bool Dark { get; set; } = true;
        public string? LastPath { get; set; }
    }

    static string FilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoahDisk");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public static bool Dark { get; set; } = true;
    public static string? LastPath { get; set; }

    public static void Load()
    {
        try
        {
            var f = FilePath();
            if (!File.Exists(f)) return;
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(f));
            if (d != null) { Dark = d.Dark; LastPath = d.LastPath; }
        }
        catch { }
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath(), JsonSerializer.Serialize(new Data { Dark = Dark, LastPath = LastPath })); }
        catch { }
    }
}

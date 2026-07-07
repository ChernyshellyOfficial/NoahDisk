using System;
using System.IO;
using System.IO.Compression;
using SpaceSaver;

namespace SpaceSaver.Gui;

// Встроенная база имён игр — снимок списка приложений Steam. Нужна, чтобы в глобальном
// скане распознавать папки-игры (например «Metal Gear Rising - Revengeance») и показывать
// их целиком, а не дробить на внутренние папки. Работает ПОЛНОСТЬЮ офлайн: список зашит
// в сборку (gzip) и разворачивается при первом обращении — никаких обращений к сети,
// потому что официальный Steam API в части сетей недоступен/отдаёт «Method not found».
static class GameDb
{
    const string ResourceName = "steam_names.txt.gz";
    static readonly object _lock = new();
    static Analysis.GameNameIndex? _index;

    /// <summary>Индекс имён (лениво разворачивается из встроенного ресурса). Никогда не null;
    /// если ресурс почему-то недоступен — вернётся пустой индекс, и распознавание просто выключится.</summary>
    public static Analysis.GameNameIndex Ensure()
    {
        if (_index != null) return _index;
        lock (_lock)
        {
            if (_index != null) return _index;
            var idx = new Analysis.GameNameIndex();
            try
            {
                var asm = typeof(GameDb).Assembly;
                using var s = asm.GetManifestResourceStream(ResourceName);
                if (s != null)
                {
                    using var gz = new GZipStream(s, CompressionMode.Decompress);
                    using var reader = new StreamReader(gz);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        idx.AddNormalized(line); // строки уже нормализованы при сборке базы
                }
            }
            catch { /* ресурс недоступен — пустой индекс, матчинг выключится */ }
            _index = idx;
            return _index;
        }
    }
}

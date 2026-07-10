using System;
using System.IO;
using System.IO.Compression;
using NoahDisk;

namespace NoahDisk.Gui;

// Встроенный список имён популярных программ (снимок Chocolatey). Дополняет реестр
// (InstalledPrograms): реестр ловит установленные программы по пути, а этот список —
// портативные/незарегистрированные по имени папки. Работает офлайн: список зашит в сборку.
// Короткие имена (<8 символов после нормализации) сюда не попадают — их надёжно
// покрывает реестр; так избегаем ложных совпадений по коротким префиксам.
static class ProgramDb
{
    const string ResourceName = "program_names.txt.gz";
    static readonly object _lock = new();
    static Analysis.GameNameIndex? _index;

    public static Analysis.GameNameIndex Ensure()
    {
        if (_index != null) return _index;
        lock (_lock)
        {
            if (_index != null) return _index;
            var idx = new Analysis.GameNameIndex();
            try
            {
                var asm = typeof(ProgramDb).Assembly;
                using var s = asm.GetManifestResourceStream(ResourceName);
                if (s != null)
                {
                    using var gz = new GZipStream(s, CompressionMode.Decompress);
                    using var reader = new StreamReader(gz);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        idx.Add(line); // хранятся «сырые» имена — нормализуем при загрузке, как имена папок
                }
            }
            catch { /* ресурс недоступен — пустой индекс, слой имён просто выключится */ }
            _index = idx;
            return _index;
        }
    }
}

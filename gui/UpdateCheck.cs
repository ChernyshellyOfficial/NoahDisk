using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace NoahDisk.Gui;

// Тихая проверка обновлений через GitHub Releases API. Полностью в фоне и не блокирует старт.
// Любая осечка — офлайн, ошибка сети, отсутствие релизов (404), черновик/pre-release, кривой тег —
// трактуется как «обновлений нет» (возвращаем null). Ничего не качает и не устанавливает: только
// сравнивает версию и, если на GitHub новее, отдаёт ссылку на страницу релиза.
static class UpdateCheck
{
    const string LatestApi    = "https://api.github.com/repos/ChernyshellyOfficial/NoahDisk/releases/latest";
    const string ReleasesPage = "https://github.com/ChernyshellyOfficial/NoahDisk/releases/latest";

    public sealed record Result(string Version, string Url);

    /// <summary>Данные более новой версии, если она есть; иначе null.</summary>
    public static async Task<Result?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub без User-Agent отвечает 403; Accept — рекомендованный заголовок API.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NoahDisk-update-check");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.GetAsync(LatestApi);
            if (!resp.IsSuccessStatusCode) return null; // 404 = релизов ещё нет и т.п.

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            // /releases/latest и так пропускает draft/pre-release, но подстрахуемся.
            if (IsTrue(root, "prerelease") || IsTrue(root, "draft")) return null;

            if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not string tag) return null;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            if (!IsUpdate(tag, current)) return null; // не новее / нераспознаваемо — молчим

            var url = root.TryGetProperty("html_url", out var urlEl) && urlEl.GetString() is string u && u.Length > 0
                ? u : ReleasesPage;

            return new Result(tag.Trim().TrimStart('v', 'V'), url);
        }
        catch { return null; }
    }

    static bool IsTrue(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.True;

    // Пара (тег релиза, текущая версия) → предлагать ли обновление. Выделено ради тестируемости
    // (сетевую часть в песочнице не прогнать, а тут вся логика сравнения версий).
    internal static bool IsUpdate(string tag, Version current)
    {
        var latest = ParseVersion(tag);
        return latest != null && latest > Normalize(current);
    }

    // Тег вида "v1.2.0" / "1.2" / "1.2.0-beta" → нормализованная Version (Major.Minor.Build), либо null.
    static Version? ParseVersion(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOf('-');
        if (dash >= 0) t = t.Substring(0, dash); // отрезаем суффикс -beta/-rc и т.п.
        return Version.TryParse(t, out var v) ? Normalize(v) : null;
    }

    // Приводим к трём компонентам, чтобы 1.0.0 и 1.0.0.0 сравнивались одинаково
    // (иначе неуказанная revision = -1 ломает сравнение).
    static Version Normalize(Version v) =>
        new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}

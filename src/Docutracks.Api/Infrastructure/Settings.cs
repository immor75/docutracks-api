using System.Globalization;
using Microsoft.Extensions.Caching.Memory;

namespace Docutracks.Api.Infrastructure;

/// <summary>
/// Docutracks settings (Conf.DocutracksConfiguration: Value, else DefaultValue), as
/// IConfigurationProvider.GetPropertyValue reads them. Loaded in one query and cached.
/// </summary>
public interface ISettings
{
    Task<IReadOnlyDictionary<string, string>> All(CancellationToken ct);
}

public sealed class Settings(IDb db, IMemoryCache cache, IOptions<ApiOptions> options) : ISettings
{
    private sealed class Row
    {
        public string ConfKey { get; set; } = "";
        public string? Value { get; set; }
        public string? DefaultValue { get; set; }
    }

    public async Task<IReadOnlyDictionary<string, string>> All(CancellationToken ct) =>
        await cache.GetOrCreateAsync("settings", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(options.Value.ReferenceCacheSeconds);
            var rows = await db.Query<Row>("SELECT ConfKey, Value, DefaultValue FROM Conf.DocutracksConfiguration", ct: ct);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) d[r.ConfKey] = r.Value ?? r.DefaultValue ?? "";
            return (IReadOnlyDictionary<string, string>)d;
        }) ?? new Dictionary<string, string>();
}

public static class SettingsExtensions
{
    public static async Task<bool> Bool(this ISettings s, string key, bool fallback, CancellationToken ct)
    {
        var v = (await s.All(ct)).GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(v)) return fallback;
        if (bool.TryParse(v.Trim(), out var b)) return b;
        return v.Trim() == "1" || (v.Trim() != "0" && fallback);
    }

    public static async Task<int> Int(this ISettings s, string key, int fallback, CancellationToken ct)
    {
        var v = (await s.All(ct)).GetValueOrDefault(key);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
    }

    public static async Task<string> Text(this ISettings s, string key, string fallback, CancellationToken ct)
    {
        var v = (await s.All(ct)).GetValueOrDefault(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    /// <summary>A comma-separated list setting (HideDocumentCatalogColumns, …).</summary>
    public static async Task<string[]> List(this ISettings s, string key, CancellationToken ct) =>
        (await s.Text(key, "", ct)).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

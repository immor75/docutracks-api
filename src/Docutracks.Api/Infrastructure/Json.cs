using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Docutracks.Api.Infrastructure;

public static class Json
{
    /// <summary>
    /// The contracts use PascalCase names (as ServiceStack and the TypeScript models), null fields
    /// are left out, Greek is written as is.
    /// </summary>
    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = null;
        o.DictionaryKeyPolicy = null;
        o.PropertyNameCaseInsensitive = true;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
        o.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    }

    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions();
        Configure(o);
        return o;
    }

    /// <summary>ISO 8601 for the contracts; null stays null.</summary>
    public static string? Iso(DateTime? d) => d?.ToString("yyyy-MM-ddTHH:mm:ss");

    /// <summary>yyyy-mm-dd for dates without time.</summary>
    public static string? Day(DateTime? d) => d?.ToString("yyyy-MM-dd");

    /// <summary>Parses yyyy-mm-dd (the requests); null on empty or invalid.</summary>
    public static DateTime? ParseDay(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}

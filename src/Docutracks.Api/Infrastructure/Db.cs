using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Docutracks.Api.Infrastructure;

/// <summary>Opens connections to the Docutracks database (read-only login).</summary>
public interface IDbConnectionFactory
{
    DbConnection Create();
}

/// <summary>
/// Reads from the Docutracks database. A small mapper instead of Dapper: columns map to
/// settable properties of the same name (case-insensitive); `@Name` parameters come from the
/// properties of `args`; a list (`IEnumerable<int>`, `IEnumerable<string>`) expands to `(@Name0, @Name1, …)`
/// where the SQL says `IN @Name`.
/// </summary>
public interface IDb
{
    Task<List<T>> Query<T>(string sql, object? args = null, CancellationToken ct = default) where T : new();
    Task<T?> First<T>(string sql, object? args = null, CancellationToken ct = default) where T : new();
    Task<T?> Scalar<T>(string sql, object? args = null, CancellationToken ct = default);
}

public sealed class Db(IDbConnectionFactory factory, ILogger<Db> log) : IDb
{
    private const int CommandTimeoutSeconds = 60;

    public async Task<List<T>> Query<T>(string sql, object? args = null, CancellationToken ct = default) where T : new()
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);
        await using var cmd = Build(conn, sql, args);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await Map<T>(reader, ct);
        }
        catch (DbException e)
        {
            log.LogError(e, "SQL failed: {Sql}", sql);
            throw;
        }
    }

    public async Task<T?> First<T>(string sql, object? args = null, CancellationToken ct = default) where T : new() =>
        (await Query<T>(sql, args, ct)).FirstOrDefault();

    public async Task<T?> Scalar<T>(string sql, object? args = null, CancellationToken ct = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);
        await using var cmd = Build(conn, sql, args);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? default : Convert<T>(v);
    }

    internal static DbCommand Build(DbConnection conn, string sql, object? args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = CommandTimeoutSeconds;
        var text = sql;
        if (args is not null)
        {
            foreach (var p in args.GetType().GetProperties())
            {
                var value = p.GetValue(args);
                if (value is IEnumerable list and not string)
                {
                    var items = list.Cast<object?>().ToList();
                    // An empty list matches nothing: IN (NULL).
                    var names = items.Count == 0 ? ["NULL"] : items.Select((_, i) => $"@{p.Name}{i}").ToList();
                    text = Regex.Replace(text, $@"IN\s+@{p.Name}\b", $"IN ({string.Join(", ", names)})", RegexOptions.IgnoreCase);
                    for (var i = 0; i < items.Count; i++) Add(cmd, $"@{p.Name}{i}", items[i]);
                }
                else
                {
                    Add(cmd, "@" + p.Name, value);
                }
            }
        }
        cmd.CommandText = text;
        return cmd;
    }

    private static void Add(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        if (value is string s)
        {
            p.DbType = DbType.String;
            p.Size = s.Length <= 4000 ? 4000 : -1;
        }
        cmd.Parameters.Add(p);
    }

    private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> Props = new();

    internal static async Task<List<T>> Map<T>(DbDataReader reader, CancellationToken ct) where T : new()
    {
        var props = Props.GetOrAdd(typeof(T), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase));
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => (Index: i, Prop: props.GetValueOrDefault(reader.GetName(i))))
            .Where(c => c.Prop is not null)
            .ToList();
        var list = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            var row = new T();
            foreach (var (i, prop) in columns)
            {
                if (reader.IsDBNull(i)) continue;
                prop!.SetValue(row, ConvertTo(reader.GetValue(i), prop.PropertyType));
            }
            list.Add(row);
        }
        return list;
    }

    private static T Convert<T>(object v) => (T)ConvertTo(v, typeof(T))!;

    internal static object? ConvertTo(object v, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsInstanceOfType(v)) return v;
        if (target == typeof(bool)) return System.Convert.ToBoolean(v);
        if (target.IsEnum) return Enum.ToObject(target, v);
        return System.Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>SQL fragments that recur.</summary>
public static class Sql
{
    /// <summary>
    /// The Greek title of a classifier from its `…Resources` table (e.g. `[Document].[PriorityTitleResources]`):
    /// the Greek culture first, then a neutral one, then any.
    /// </summary>
    public static string Title(string resourcesTable, string ownerIdExpr) =>
        $"""
        (SELECT TOP 1 r.Value FROM {resourcesTable} tr
           INNER JOIN Resources.Resources r ON r.Id = tr.IdResource
           WHERE tr.IdPropertyOwner = {ownerIdExpr}
           ORDER BY CASE WHEN r.Culture LIKE 'el%' THEN 0 WHEN r.Culture IS NULL OR r.Culture = '' THEN 1 ELSE 2 END)
        """;
}

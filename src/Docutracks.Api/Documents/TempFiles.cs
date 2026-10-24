using System.Text.Json;

namespace Docutracks.Api.Documents;

/// <summary>
/// Files uploaded before registration (`POST files`), sent with the form of the registration.
/// One `{TempId}` file plus `{TempId}.json` with its name and owner, in Files:TempPath; deleted
/// after registration or after Files:KeepHours.
/// </summary>
public sealed class TempFiles(IOptions<ApiOptions> options, ILogger<TempFiles> log)
{
    private readonly FilesOptions opt = options.Value.Files;

    public string Folder
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(opt.TempPath) ? Path.Combine(Path.GetTempPath(), "docutracks-api") : opt.TempPath;
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public sealed record Meta(string Name, long Size, int UserId, DateTime At);

    public async Task<UploadedFile> Save(IFormFile file, int userId, CancellationToken ct)
    {
        var name = Path.GetFileName(file.FileName ?? "").Trim();
        if (name.Length == 0) throw new ApiException("Δεν στάλθηκε αρχείο.");
        if (file.Length == 0) throw new ApiException($"Το αρχείο «{name}» είναι κενό.");
        if (file.Length > opt.MaxSizeMb * 1024L * 1024L) throw new ApiException($"Το αρχείο ξεπερνά τα {opt.MaxSizeMb} MB.");
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (opt.Blocked.Contains(ext)) throw new ApiException("Ο τύπος αρχείου δεν επιτρέπεται.");

        Cleanup();
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Folder, id);
        await using (var to = File.Create(path))
            await file.CopyToAsync(to, ct);
        await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(new Meta(name, file.Length, userId, DateTime.UtcNow)), ct);
        return new UploadedFile { TempId = id, Name = name, Size = file.Length, IsPdf = ext == ".pdf" };
    }

    /// <summary>The metadata of an upload of this user, or null (unknown, expired or someone else's).</summary>
    public Meta? Find(string? tempId, int userId)
    {
        if (string.IsNullOrEmpty(tempId) || tempId.Length != 32 || !tempId.All(Uri.IsHexDigit)) return null;
        var meta = Path.Combine(Folder, tempId + ".json");
        if (!File.Exists(meta) || !File.Exists(Path.Combine(Folder, tempId))) return null;
        try
        {
            var m = JsonSerializer.Deserialize<Meta>(File.ReadAllText(meta));
            return m?.UserId == userId ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string PathOf(string tempId) => Path.Combine(Folder, tempId);

    /// <summary>Removes uploads once the document is saved.</summary>
    public void Delete(IEnumerable<string> tempIds)
    {
        foreach (var id in tempIds)
        {
            try
            {
                File.Delete(PathOf(id));
                File.Delete(PathOf(id) + ".json");
            }
            catch (IOException e)
            {
                log.LogDebug(e, "Temp file in use: {Id}", id);
            }
        }
    }

    private DateTime lastCleanup = DateTime.MinValue;

    private void Cleanup()
    {
        if (DateTime.UtcNow - lastCleanup < TimeSpan.FromMinutes(10)) return;
        lastCleanup = DateTime.UtcNow;
        var limit = DateTime.UtcNow.AddHours(-opt.KeepHours);
        foreach (var f in Directory.EnumerateFiles(Folder))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(f) < limit) File.Delete(f);
            }
            catch (IOException e)
            {
                log.LogDebug(e, "Temp file in use: {File}", f);
            }
        }
    }
}

namespace Docutracks.Api.Infrastructure;

/// <summary>Settings of the service (appsettings.json, environment variables).</summary>
public sealed class ApiOptions
{
    /// <summary>Where the service is mounted in IIS, e.g. "/api". Empty when it runs on its own.</summary>
    public string PathBase { get; set; } = "";

    public V1Options V1 { get; set; } = new();
    public ClassicOptions Classic { get; set; } = new();
    public FilesOptions Files { get; set; } = new();

    /// <summary>How long the identity of a set of credentials is kept (seconds).</summary>
    public int IdentityCacheSeconds { get; set; } = 60;

    /// <summary>Settings (Conf.DocutracksConfiguration) and classifiers are kept this long (seconds).</summary>
    public int ReferenceCacheSeconds { get; set; } = 300;
}

public sealed class V1Options
{
    /// <summary>Base of the Docutracks web application, e.g. "https://localhost/". `/services/...` is appended.</summary>
    public string BaseUrl { get; set; } = "https://localhost/";

    /// <summary>Only for test servers with a self-signed certificate.</summary>
    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>Timeout of a call to v1 or to a form of the classic UI (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 100;
}

/// <summary>The classic UI (ASP.NET MVC of Docman), whose forms the writes post.</summary>
public sealed class ClassicOptions
{
    /// <summary>The culture segment of its URLs (`/el/Document/...`).</summary>
    public string Culture { get; set; } = "el";

    /// <summary>How long the anti-forgery token of a user is reused (minutes).</summary>
    public int TokenMinutes { get; set; } = 20;

    /// <summary>Contact type of contacts created from a name (`contacts/getcontactid`); 0 = the most used one.</summary>
    public int NewContactTypeId { get; set; }
}

public sealed class FilesOptions
{
    /// <summary>Folder for files uploaded before registration (sent with the form). Default: the system temp folder.</summary>
    public string? TempPath { get; set; }

    public int MaxSizeMb { get; set; } = 50;

    /// <summary>Temporary files older than this are deleted (hours).</summary>
    public int KeepHours { get; set; } = 6;

    /// <summary>Extensions that are refused (lower case, with the dot).</summary>
    public string[] Blocked { get; set; } = [".exe", ".bat", ".cmd", ".com", ".js", ".vbs", ".ps1", ".msi", ".scr", ".dll"];
}

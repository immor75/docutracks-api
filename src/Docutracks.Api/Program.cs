using Microsoft.Extensions.Caching.Memory;
using System.Net;
using Docutracks.Api.Documents;
using Docutracks.Api.Folders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiOptions>(builder.Configuration);
// A relative Files:TempPath is relative to the project (content root), e.g. "../../.temp" in Development.
builder.Services.PostConfigure<ApiOptions>(o =>
{
    if (!string.IsNullOrWhiteSpace(o.Files.TempPath) && !Path.IsPathRooted(o.Files.TempPath))
        o.Files.TempPath = Path.GetFullPath(o.Files.TempPath, builder.Environment.ContentRootPath);
});
builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(V1Client.HttpClientName, (sp, http) =>
    {
        http.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<ApiOptions>>().Value.V1.TimeoutSeconds);
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var opt = sp.GetRequiredService<IOptions<ApiOptions>>().Value;
        var handler = new HttpClientHandler
        {
            // Credentials are forwarded by hand (UserContext): no cookie jar shared between users.
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (opt.V1.IgnoreCertificateErrors)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return handler;
    });

builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<IDb, Db>();
builder.Services.AddSingleton<ISettings, Settings>();
builder.Services.AddSingleton<IIdentityResolver, V1IdentityResolver>();
builder.Services.AddSingleton<IReference, Reference>();
builder.Services.AddScoped<UserContext>();
builder.Services.AddScoped<IGroupAccess, GroupAccess>();
builder.Services.AddScoped(sp => new V1Client(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(V1Client.HttpClientName),
    sp.GetRequiredService<UserContext>(),
    sp.GetRequiredService<IOptions<ApiOptions>>(),
    sp.GetRequiredService<ILogger<V1Client>>()));
builder.Services.AddScoped(sp => new ClassicClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(ClassicClient.HttpClientName),
    sp.GetRequiredService<UserContext>(),
    sp.GetRequiredService<IOptions<ApiOptions>>(),
    sp.GetRequiredService<IMemoryCache>(),
    sp.GetRequiredService<ILogger<ClassicClient>>(),
    sp.GetRequiredService<IHostEnvironment>()));
builder.Services.AddDocuments();
builder.Services.AddFolders();

var app = builder.Build();

var options = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;
if (!string.IsNullOrEmpty(options.PathBase)) app.UsePathBase(options.PathBase);

app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<IdentityMiddleware>();

app.MapGet("/health", () => Results.Ok(new { Status = "ok" }));
var v2 = app.MapGroup("/v2");
v2.MapDocuments();
v2.MapFolders();

app.Run();

/// <summary>For the tests (WebApplicationFactory-like hosting).</summary>
public partial class Program;

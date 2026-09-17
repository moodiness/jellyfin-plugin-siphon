using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Serves fixed embedded public UI assets without external requests or authentication tokens.</summary>
[ApiController]
[AllowAnonymous]
[Route("Siphon")]
public sealed class SiphonAssetsController : ControllerBase
{
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private static readonly Lazy<(byte[] Bytes, EntityTagHeaderValue EntityTag)> Icon = new(LoadIcon);
    private static readonly IReadOnlyDictionary<string, Lazy<(byte[] Bytes, EntityTagHeaderValue EntityTag)>> Scripts =
        new[] { "shared.js", "admin-locale.js", "user-locale.js", "siphon-admin.js", "siphon-user.js" }
            .ToDictionary(name => name, name => new Lazy<(byte[], EntityTagHeaderValue)>(() =>
                LoadResource("Jellyfin.Plugin.Siphon.Web." + name)), StringComparer.Ordinal);

    /// <summary>Gets the unmodified, embedded Siphon icon.</summary>
    [HttpGet("Icon")]
    [Produces("image/png")]
    public FileContentResult GetIcon()
    {
        var icon = Icon.Value;
        Response.Headers.CacheControl = "public, max-age=86400";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return new FileContentResult(icon.Bytes, "image/png")
        {
            EntityTag = icon.EntityTag
        };
    }

    [HttpGet("Assets/{name}")]
    [Produces("text/javascript")]
    public IActionResult GetScript(string name)
    {
        if (!Scripts.TryGetValue(name, out var resource)) return NotFound();
        var script = resource.Value;
        // Stable URLs must be revalidated after an upgrade; bodies contain no configuration or credentials.
        Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return new FileContentResult(script.Bytes, "text/javascript; charset=utf-8") { EntityTag = script.EntityTag };
    }

    private static (byte[] Bytes, EntityTagHeaderValue EntityTag) LoadIcon() => LoadResource(Plugin.IconResourceName);

    private static (byte[] Bytes, EntityTagHeaderValue EntityTag) LoadResource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("An embedded Siphon UI asset is missing.");
        if (stream.Length is <= 0 or > MaxIconBytes)
        {
            throw new InvalidDataException("An embedded Siphon UI asset exceeds its size limit.");
        }

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var entityTag = new EntityTagHeaderValue('"' + Convert.ToHexString(SHA256.HashData(bytes)) + '"');
        return (bytes, entityTag);
    }
}

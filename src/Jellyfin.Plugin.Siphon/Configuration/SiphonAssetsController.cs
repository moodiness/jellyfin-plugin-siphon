using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Siphon.Configuration;

/// <summary>Serves the public branding asset without external requests or authentication tokens.</summary>
[ApiController]
[AllowAnonymous]
[Route("Siphon")]
public sealed class SiphonAssetsController : ControllerBase
{
    private const int MaxIconBytes = 2 * 1024 * 1024;
    private static readonly Lazy<(byte[] Bytes, EntityTagHeaderValue EntityTag)> Icon = new(LoadIcon);

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

    private static (byte[] Bytes, EntityTagHeaderValue EntityTag) LoadIcon()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(Plugin.IconResourceName)
            ?? throw new InvalidOperationException("The embedded Siphon icon is missing.");
        if (stream.Length is <= 0 or > MaxIconBytes)
        {
            throw new InvalidDataException("The embedded Siphon icon exceeds its size limit.");
        }

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var entityTag = new EntityTagHeaderValue('"' + Convert.ToHexString(SHA256.HashData(bytes)) + '"');
        return (bytes, entityTag);
    }
}

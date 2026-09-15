using System.Security.Cryptography;

namespace Jellyfin.Plugin.Siphon.Infrastructure;

public sealed class SiphonSecretStore(SiphonPaths paths)
{
    private readonly object _gate = new();
    private byte[]? _key;

    public byte[] GetSigningKey()
    {
        lock (_gate)
        {
            _key ??= LoadOrCreate();
            return (byte[])_key.Clone();
        }
    }

    private byte[] LoadOrCreate()
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var path = Path.Combine(paths.DataDirectory, "signing.key");
        if (File.Exists(path))
        {
            return ReadKey(path);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(key);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another instance won creation; never replace its established key.
                return ReadKey(path);
            }

            return key;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static byte[] ReadKey(string path)
    {
        if (new FileInfo(path).Length != 32)
        {
            throw new InvalidDataException("Siphon signing key is corrupt; restore the original key from backup.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var key = File.ReadAllBytes(path);
        if (key.Length != 32)
        {
            throw new InvalidDataException("Siphon signing key changed while being read.");
        }

        return key;
    }
}

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

// Read CLI metadata directly: never load an assembly or execute plugin initializers.
try
{
    if (args.Length == 0)
    {
        throw new ArgumentException("Supply one or more managed PE assembly paths.");
    }

    var assemblies = new List<object>(args.Length);
    foreach (var path in args)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            throw new BadImageFormatException($"No managed metadata: {path}");
        }

        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly)
        {
            throw new BadImageFormatException($"Not an assembly: {path}");
        }

        var definition = reader.GetAssemblyDefinition();
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in definition.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            var typeName = reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
            if (typeName is not ("System.Reflection.AssemblyMetadataAttribute"
                or "System.Reflection.AssemblyInformationalVersionAttribute"
                or "System.Runtime.Versioning.TargetFrameworkAttribute"))
            {
                continue;
            }

            var value = reader.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
            {
                throw new BadImageFormatException($"Invalid attribute prolog: {path}");
            }

            var key = typeName == "System.Reflection.AssemblyMetadataAttribute"
                ? value.ReadSerializedString() : typeName;
            var text = value.ReadSerializedString();
            if (key is null || text is null || !attributes.TryAdd(key, text))
            {
                throw new BadImageFormatException($"Missing or duplicate assembly attribute: {path}");
            }
        }

        var references = reader.AssemblyReferences.Select(handle =>
        {
            var reference = reader.GetAssemblyReference(handle);
            return Identity(reader, reference.Name, reference.Version, reference.Culture,
                reference.PublicKeyOrToken, (reference.Flags & AssemblyFlags.PublicKey) != 0);
        }).ToArray();
        assemblies.Add(new
        {
            path = Path.GetFullPath(path),
            identity = Identity(reader, definition.Name, definition.Version, definition.Culture,
                definition.PublicKey, true),
            attributes,
            references
        });
    }

    Console.WriteLine(JsonSerializer.Serialize(assemblies));
    return 0;
}
catch (Exception exception) when (exception is IOException or BadImageFormatException or ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"Assembly inspection failed: {exception.Message}");
    return 1;
}

static object Identity(MetadataReader reader, StringHandle name, Version version, StringHandle culture,
    BlobHandle key, bool fullKey)
{
    var bytes = reader.GetBlobBytes(key);
    var token = bytes.Length == 0 ? string.Empty : fullKey
        ? Convert.ToHexString(SHA1.HashData(bytes).AsSpan()[^8..].ToArray().Reverse().ToArray()).ToLowerInvariant()
        : Convert.ToHexString(bytes).ToLowerInvariant();
    return new
    {
        name = reader.GetString(name),
        version = version.ToString(),
        culture = reader.GetString(culture),
        publicKeyToken = token
    };
}

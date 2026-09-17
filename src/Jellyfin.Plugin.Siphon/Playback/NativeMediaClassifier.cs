using System.Buffers.Binary;

namespace Jellyfin.Plugin.Siphon.Playback;

/// <summary>
/// A root playback URL must identify a supported binary media container, or pass
/// through the separate HLS parser. MIME labels and filenames are not evidence.
/// This does not claim to sandbox references embedded inside binary containers.
/// </summary>
internal static class NativeMediaClassifier
{
    internal static void RequireSupported(ReadOnlySpan<byte> prefix)
    {
        if (!IsSupported(prefix))
            throw new InvalidDataException("The source is not a supported binary media container or HLS playlist.");
    }

    internal static bool IsSupported(ReadOnlySpan<byte> prefix)
    {
        // ISO base media / QuickTime. Leading padding and media atoms are legal;
        // validate their size encoding rather than matching an arbitrary filename.
        if (prefix.Length >= 12)
        {
            var type = prefix.Slice(4, 4);
            var size = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            var validSize = size == 0 || size >= 8;
            if (size == 1) validSize = prefix.Length >= 16 && BinaryPrimitives.ReadUInt64BigEndian(prefix[8..]) >= 16;
            if (validSize && (type.SequenceEqual("ftyp"u8) || type.SequenceEqual("styp"u8)
                || type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8)
                || type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8) || type.SequenceEqual("wide"u8))) return true;
        }

        if (prefix.StartsWith<byte>([0x1a, 0x45, 0xdf, 0xa3])) return true; // Matroska / WebM EBML header.
        if (prefix.Length >= 377 && prefix[0] == 0x47 && prefix[188] == 0x47 && prefix[376] == 0x47) return true;
        if (prefix.Length >= 389 && prefix[4] == 0x47 && prefix[196] == 0x47 && prefix[388] == 0x47) return true;
        if (prefix.StartsWith<byte>([0, 0, 1, 0xba]) || prefix.StartsWith<byte>([0, 0, 1, 0xb3])) return true;
        if (prefix.Length >= 12 && (prefix.StartsWith("RIFF"u8) || prefix.StartsWith("RF64"u8))
            && (prefix.Slice(8, 4).SequenceEqual("AVI "u8) || prefix.Slice(8, 4).SequenceEqual("WAVE"u8))) return true;
        if (prefix.Length >= 12 && prefix.StartsWith("FORM"u8)
            && (prefix.Slice(8, 4).SequenceEqual("AIFF"u8) || prefix.Slice(8, 4).SequenceEqual("AIFC"u8))) return true;
        if (prefix.StartsWith<byte>([0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0, 0xaa, 0, 0x62, 0xce, 0x6c])) return true;
        if (prefix.Length >= 9 && prefix.StartsWith("FLV"u8) && prefix[3] == 1) return true;
        if (prefix.Length >= 27 && prefix.StartsWith("OggS"u8) && prefix[4] == 0) return true;
        if (prefix.StartsWith("fLaC"u8)) return true;

        // ID3-prefixed MPEG audio, raw MPEG audio frames, and AAC ADTS.
        if (prefix.Length >= 10 && prefix.StartsWith("ID3"u8) && prefix[3] is >= 2 and <= 4
            && (prefix[6] | prefix[7] | prefix[8] | prefix[9]) < 128) return true;
        if (prefix.Length >= 4 && prefix[0] == 0xff && (prefix[1] & 0xe0) == 0xe0
            && (prefix[1] & 0x18) != 0x08 && (prefix[1] & 0x06) != 0
            && (prefix[2] & 0xf0) is not (0 or 0xf0) && (prefix[2] & 0x0c) != 0x0c) return true;
        return prefix.Length >= 7 && prefix[0] == 0xff && (prefix[1] & 0xf6) == 0xf0 && (prefix[2] & 0x3c) != 0x3c;
    }
}

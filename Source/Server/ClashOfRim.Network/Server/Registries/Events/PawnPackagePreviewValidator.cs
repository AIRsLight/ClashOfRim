using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AIRsLight.ClashOfRim.Network;

internal static class PawnPackagePreviewValidator
{
    public const int MaximumBytes = 64 * 1024;
    public const int MaximumDimension = 128;

    private static readonly byte[] PngSignature =
    {
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a
    };

    public static bool TryValidate(
        byte[]? bytes,
        out int width,
        out int height,
        out string contentSha256,
        out string error)
    {
        width = 0;
        height = 0;
        contentSha256 = string.Empty;
        error = string.Empty;

        if (bytes is null || bytes.Length < 33 || bytes.Length > MaximumBytes)
        {
            error = "Pawn preview PNG size is invalid.";
            return false;
        }

        if (!bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            error = "Pawn preview payload is not a PNG image.";
            return false;
        }

        bool sawHeader = false;
        bool sawImageData = false;
        bool sawEnd = false;
        int offset = PngSignature.Length;
        while (offset + 12 <= bytes.Length)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (chunkLength > int.MaxValue
                || chunkLength > bytes.Length - offset - 12)
            {
                error = "Pawn preview PNG contains an invalid chunk length.";
                return false;
            }

            int dataLength = (int)chunkLength;
            ReadOnlySpan<byte> chunkType = bytes.AsSpan(offset + 4, 4);
            ReadOnlySpan<byte> chunkData = bytes.AsSpan(offset + 8, dataLength);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                bytes.AsSpan(offset + 8 + dataLength, 4));
            if (ComputeCrc32(bytes.AsSpan(offset + 4, dataLength + 4)) != expectedCrc)
            {
                error = "Pawn preview PNG contains a corrupt chunk.";
                return false;
            }

            if (!sawHeader)
            {
                if (!chunkType.SequenceEqual("IHDR"u8) || dataLength != 13)
                {
                    error = "Pawn preview PNG has an invalid IHDR chunk.";
                    return false;
                }

                width = BinaryPrimitives.ReadInt32BigEndian(chunkData[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(chunkData.Slice(4, 4));
                if (width <= 0
                    || height <= 0
                    || width > MaximumDimension
                    || height > MaximumDimension
                    || chunkData[10] != 0
                    || chunkData[11] != 0
                    || chunkData[12] > 1)
                {
                    error = "Pawn preview PNG dimensions or encoding are invalid.";
                    return false;
                }

                sawHeader = true;
            }
            else if (chunkType.SequenceEqual("IDAT"u8))
            {
                sawImageData = true;
            }
            else if (chunkType.SequenceEqual("IEND"u8))
            {
                if (dataLength != 0)
                {
                    error = "Pawn preview PNG has an invalid IEND chunk.";
                    return false;
                }

                sawEnd = true;
                offset += dataLength + 12;
                break;
            }

            offset += dataLength + 12;
        }

        if (!sawHeader || !sawImageData || !sawEnd || offset != bytes.Length)
        {
            error = "Pawn preview PNG is incomplete.";
            return false;
        }

        contentSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return true;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xedb88320u
                    : crc >> 1;
            }
        }

        return ~crc;
    }
}

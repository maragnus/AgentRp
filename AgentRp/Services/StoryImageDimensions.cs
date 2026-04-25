using System.Text;

namespace AgentRp.Services;

internal sealed record StoryImageDimensions(int Width, int Height)
{
    public static StoryImageDimensions? TryRead(byte[] bytes, string contentType)
    {
        try
        {
            if (contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) && bytes.Length >= 24)
                return new StoryImageDimensions(ReadBigEndianInt(bytes, 16), ReadBigEndianInt(bytes, 20));

            if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
                return TryReadJpeg(bytes);

            if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
                return TryReadWebp(bytes);
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static StoryImageDimensions? TryReadJpeg(byte[] bytes)
    {
        var index = 2;
        while (index + 9 < bytes.Length)
        {
            if (bytes[index] != 0xFF)
                return null;

            var marker = bytes[index + 1];
            var length = (bytes[index + 2] << 8) + bytes[index + 3];
            if (marker is >= 0xC0 and <= 0xC3)
                return new StoryImageDimensions((bytes[index + 7] << 8) + bytes[index + 8], (bytes[index + 5] << 8) + bytes[index + 6]);

            index += 2 + length;
        }

        return null;
    }

    private static StoryImageDimensions? TryReadWebp(byte[] bytes)
    {
        if (bytes.Length < 30 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WEBP")
            return null;

        var chunk = Encoding.ASCII.GetString(bytes, 12, 4);
        if (chunk == "VP8X" && bytes.Length >= 30)
            return new StoryImageDimensions(1 + ReadLittleEndian24(bytes, 24), 1 + ReadLittleEndian24(bytes, 27));

        return null;
    }

    private static int ReadBigEndianInt(byte[] bytes, int offset) =>
        (bytes[offset] << 24) + (bytes[offset + 1] << 16) + (bytes[offset + 2] << 8) + bytes[offset + 3];

    private static int ReadLittleEndian24(byte[] bytes, int offset) =>
        bytes[offset] + (bytes[offset + 1] << 8) + (bytes[offset + 2] << 16);
}

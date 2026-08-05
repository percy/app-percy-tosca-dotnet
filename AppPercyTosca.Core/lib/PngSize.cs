namespace AppPercyTosca.Core
{
    /// <summary>
    /// Reads the pixel dimensions out of a PNG's header.
    ///
    /// This exists so the device screen size does not have to be typed into a test sheet. A Tosca
    /// mobile session usually reports no screen size, and every other source is a guess — but the
    /// screenshot we just captured *is* the screen, and a PNG states its own size in the 24 bytes at
    /// the front. No decoding, no image library, no dependency.
    /// </summary>
    public static class PngSize
    {
        // 8-byte signature, then a 4-byte chunk length, then "IHDR", then width and height as
        // big-endian 32-bit integers. Width therefore starts at 16 and height at 20, so 24 bytes is
        // all that ever needs reading.
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private const int HeaderLength = 24;
        private const int WidthOffset = 16;
        private const int HeightOffset = 20;

        /// <summary>
        /// Reads the dimensions of the PNG at <paramref name="path"/>, or null when the file is
        /// missing, unreadable, or not a PNG. Never throws: a failure here should cost the tag its
        /// dimensions, not fail the snapshot.
        /// </summary>
        public static (int Width, int Height)? TryReadFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                using FileStream stream = File.OpenRead(path);
                byte[] header = new byte[HeaderLength];
                int read = 0;
                while (read < HeaderLength)
                {
                    int got = stream.Read(header, read, HeaderLength - read);
                    if (got <= 0) break;
                    read += got;
                }
                return read < HeaderLength ? null : TryRead(header);
            }
            catch (Exception e)
            {
                Utils.Log($"Could not read the screenshot dimensions from {path}: {e.Message}", "debug");
                return null;
            }
        }

        /// <summary>
        /// Reads the dimensions from PNG bytes, or null when they are not a PNG header. A zero in
        /// either axis is rejected: it is not a usable dimension and would read as "unknown" anyway.
        /// </summary>
        public static (int Width, int Height)? TryRead(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < HeaderLength) return null;

            for (int i = 0; i < Signature.Length; i++)
            {
                if (bytes[i] != Signature[i]) return null;
            }

            int width = BigEndianInt(bytes, WidthOffset);
            int height = BigEndianInt(bytes, HeightOffset);
            return width > 0 && height > 0 ? (width, height) : null;
        }

        private static int BigEndianInt(byte[] bytes, int offset) =>
            (bytes[offset] << 24) | (bytes[offset + 1] << 16) |
            (bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}

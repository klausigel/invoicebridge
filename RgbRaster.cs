// SPDX-License-Identifier: AGPL-3.0-or-later
// Deliberately limited reader for the bundled Ghostscript ppmraw (P6, 8-bit RGB) output.
namespace Kig.InvoiceBridge;

internal sealed record RgbRaster(int Width, int Height, byte[] Data)
{
    internal byte[] ToPng()
    {
        using var result = new MemoryStream();
        result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            var stride = Width * 3;
            for (int row = 0; row < Height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write(Data, row * stride, stride);
            }
        }
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, Width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[4..], Height);
        header[8] = 8; header[9] = 2;
        Chunk("IHDR"u8, header);
        Chunk("IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        Chunk("IEND"u8, []);
        return result.ToArray();

        void Chunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> content)
        {
            Span<byte> number = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, content.Length);
            result.Write(number);
            result.Write(type);
            result.Write(content);
            var crc = uint.MaxValue;
            foreach (var value in type) Update(value);
            foreach (var value in content) Update(value);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
            result.Write(number);
            void Update(byte value)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
        }
    }

    internal static RgbRaster Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int position = 0;
        string Token()
        {
            while (position < bytes.Length)
            {
                if (bytes[position] == '#') { while (position < bytes.Length && bytes[position] != '\n') position++; }
                else if (char.IsWhiteSpace((char)bytes[position])) position++;
                else break;
            }
            var start = position;
            while (position < bytes.Length && !char.IsWhiteSpace((char)bytes[position])) position++;
            if (start == position) throw new InvalidDataException("Unvollständiger Rasterkopf.");
            return System.Text.Encoding.ASCII.GetString(bytes, start, position - start);
        }
        if (Token() != "P6" || !int.TryParse(Token(), out var width) || !int.TryParse(Token(), out var height) || Token() != "255" || width <= 0 || height <= 0 || (long)width * height > 20_000_000)
            throw new InvalidDataException("Unerwartetes ppmraw-Rasterformat.");
        if (position >= bytes.Length || !char.IsWhiteSpace((char)bytes[position])) throw new InvalidDataException("Rastertrenner fehlt.");
        if (bytes[position++] == '\r' && position < bytes.Length && bytes[position] == '\n') position++;
        var length = checked(width * height * 3);
        if (bytes.Length - position != length) throw new InvalidDataException("Rasterdatenlänge stimmt nicht überein.");
        return new(width, height, bytes[position..]);
    }
}

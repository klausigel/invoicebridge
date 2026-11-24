// SPDX-License-Identifier: AGPL-3.0-or-later
// Deliberately limited reader for the bundled Ghostscript ppmraw (P6, 8-bit RGB) output.
namespace Kig.InvoiceBridge;

internal sealed record RgbRaster(int Width, int Height, byte[] Data)
{
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

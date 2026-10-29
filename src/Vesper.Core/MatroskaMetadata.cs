namespace Vesper.Core;

public sealed record VideoDimensions(uint Width, uint Height, bool Interlaced = false)
{
    public string Label => $"{Height}{(Interlaced ? 'i' : 'p')}";
}

// 只读取 Matroska/EBML 的 Tracks 元数据，跳过媒体块，不依赖 Windows 编解码器或全文扫描。
public static class MatroskaMetadata
{
    public static VideoDimensions? TryReadDimensions(string path)
    {
        if (!Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            && !Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return ReadDimensions(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static VideoDimensions? ReadDimensions(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead) return null;
        try { return new Reader(stream).Read(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException) { return null; }
    }

    private sealed class Reader(Stream stream)
    {
        private int elements, bytes;
        private readonly record struct Element(ulong Id, long Start, long End);
        private readonly record struct Track(VideoDimensions? Dimensions, bool Video, bool Enabled, bool Default);

        public VideoDimensions? Read()
        {
            stream.Position = 0;
            Element header = Next(stream.Length);
            if (header.Id != 0x1A45DFA3) return null;
            stream.Position = header.End;
            while (stream.Position < stream.Length)
            {
                Element element = Next(stream.Length);
                if (element.Id == 0x18538067) return Segment(element.End);
                stream.Position = element.End;
            }
            return null;
        }

        private VideoDimensions? Segment(long end)
        {
            while (stream.Position < end)
            {
                Element element = Next(end);
                if (element.Id == 0x1654AE6B) return Tracks(element.End);
                stream.Position = element.End;
            }
            return null;
        }

        private VideoDimensions? Tracks(long end)
        {
            VideoDimensions? first = null;
            while (stream.Position < end)
            {
                Element element = Next(end);
                if (element.Id == 0xAE)
                {
                    Track track = ReadTrack(element.End);
                    if (track.Video && track.Enabled && track.Dimensions is not null)
                    {
                        if (track.Default) return track.Dimensions;
                        first ??= track.Dimensions;
                    }
                }
                stream.Position = element.End;
            }
            return first;
        }

        private Track ReadTrack(long end)
        {
            bool video = false, enabled = true, preferred = true;
            VideoDimensions? dimensions = null;
            while (stream.Position < end)
            {
                Element element = Next(end);
                switch (element.Id)
                {
                    case 0x83: video = Unsigned(element) == 1; break;
                    case 0xB9: enabled = Unsigned(element) != 0; break;
                    case 0x88: preferred = Unsigned(element) != 0; break;
                    case 0xE0: dimensions = ReadVideo(element.End); break;
                }
                stream.Position = element.End;
            }
            return new(dimensions, video, enabled, preferred);
        }

        private VideoDimensions? ReadVideo(long end)
        {
            ulong width = 0, height = 0, top = 0, bottom = 0, left = 0, right = 0;
            bool interlaced = false;
            while (stream.Position < end)
            {
                Element element = Next(end);
                switch (element.Id)
                {
                    case 0xB0: width = Unsigned(element); break;
                    case 0xBA: height = Unsigned(element); break;
                    case 0x54BB: top = Unsigned(element); break;
                    case 0x54AA: bottom = Unsigned(element); break;
                    case 0x54CC: left = Unsigned(element); break;
                    case 0x54DD: right = Unsigned(element); break;
                    case 0x9A: interlaced = Unsigned(element) == 1; break;
                }
                stream.Position = element.End;
            }
            if (width is 0 or > 131072 || height is 0 or > 131072
                || top >= height || bottom >= height - top || left >= width || right >= width - left) return null;
            return new((uint)(width - left - right), (uint)(height - top - bottom), interlaced);
        }

        private Element Next(long boundary)
        {
            if (++elements > 4096 || stream.Position >= boundary) throw new InvalidDataException("元数据超出读取范围。");
            ulong id = Vint(true, out _);
            ulong size = Vint(false, out bool unknown);
            long start = stream.Position;
            if (start > boundary || (!unknown && size > (ulong)(boundary - start))) throw new InvalidDataException("EBML 元素长度无效。");
            return new(id, start, unknown ? boundary : checked(start + (long)size));
        }

        private ulong Vint(bool id, out bool unknown)
        {
            int first = Byte(), mask = 0x80, length = 1;
            while (mask > 0 && (first & mask) == 0) { mask >>= 1; length++; }
            if (mask == 0 || length > (id ? 4 : 8)) throw new InvalidDataException("EBML 整数无效。");
            ulong value = id ? (ulong)first : (ulong)(first & (mask - 1));
            for (int i = 1; i < length; i++) value = (value << 8) | (uint)Byte();
            unknown = !id && value == ((1UL << (7 * length)) - 1);
            return value;
        }

        private ulong Unsigned(Element element)
        {
            long length = element.End - element.Start;
            if (length is < 1 or > 8) throw new InvalidDataException("视频属性长度无效。");
            ulong value = 0;
            for (int i = 0; i < length; i++) value = (value << 8) | (uint)Byte();
            return value;
        }

        private int Byte()
        {
            if (++bytes > 262144) throw new InvalidDataException("元数据读取预算已用尽。");
            int value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            return value;
        }
    }
}

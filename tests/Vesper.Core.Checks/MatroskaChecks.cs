using Vesper.Core;

internal static class MatroskaChecks
{
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    private static byte[] Id(uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value); Array.Reverse(bytes);
        return bytes.SkipWhile(b => b == 0).ToArray();
    }
    private static byte[] Element(uint id, byte[] data)
        => Join(Id(id), Size(data.Length), data);
    private static byte[] Size(int dataLength)
    {
        int length = 1;
        while ((ulong)dataLength >= (1UL << (7 * length)) - 1) length++;
        ulong value = (ulong)(uint)dataLength | (1UL << (7 * length));
        byte[] size = new byte[length];
        for (int i = length - 1; i >= 0; i--) { size[i] = (byte)value; value >>= 8; }
        return size;
    }
    private static byte[] UInt(uint id, uint value) => Element(id, value == 0 ? [0] : Id(value));
    private static byte[] Track(uint width, uint height, bool video = true, bool preferred = true, bool interlaced = false, uint crop = 0) =>
        Element(0xAE, Join(UInt(0x83, video ? 1u : 2u), UInt(0x88, preferred ? 1u : 0u),
            Element(0xE0, Join(UInt(0xB0, width), UInt(0xBA, height), UInt(0x9A, interlaced ? 1u : 2u), UInt(0x54AA, crop)))));
    private static byte[] File(byte[] payload, bool unknownSize = false) => Join(Element(0x1A45DFA3, []), Id(0x18538067),
        unknownSize ? [1, 255, 255, 255, 255, 255, 255, 255] : Size(payload.Length), payload);
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override int ReadByte() { BytesRead++; return base.ReadByte(); }
    }

    public static void Run(Action<bool, string> check)
    {
        using var normal = new MemoryStream(File(Element(0x1654AE6B, Track(1920, 1080))));
        check(MatroskaMetadata.ReadDimensions(normal) is { Width: 1920, Height: 1080, Label: "1080p" },
            "直接读取 MKV 视频轨道宽高，不依赖 Windows 属性处理器或解码器");
        using var unknown = new MemoryStream(File(Element(0x1654AE6B,
            Join(Track(640, 480, video: false), Track(1280, 720, preferred: false), Track(1920, 1080))), true));
        check(MatroskaMetadata.ReadDimensions(unknown)?.Height == 1080,
            "支持未知长度 Segment，跳过音轨并优先读取默认视频轨道");
        using var cropped = new MemoryStream(File(Element(0x1654AE6B, Track(1920, 1088, interlaced: true, crop: 8))));
        check(MatroskaMetadata.ReadDimensions(cropped)?.Label == "1080i", "正确处理视频裁剪尺寸及隔行标记");
        byte[] media = Element(0x1F43B675, new byte[4 * 1024 * 1024]);
        using var skipped = new CountingStream(File(Join(media, Element(0x1654AE6B, Track(1280, 720)))));
        check(MatroskaMetadata.ReadDimensions(skipped)?.Height == 720 && skipped.BytesRead < 512,
            "通过容器长度跳过媒体块，仅读取轨道头，不重新扫描视频全文");
        using var broken = new MemoryStream([0x1A, 0x45, 0xDF, 0xA3, 0xFF, 0xBA]);
        check(MatroskaMetadata.ReadDimensions(broken) is null, "损坏或不完整的容器不会伪造画质或抛出未处理异常");
        using var audioOnly = new MemoryStream(File(Element(0x1654AE6B, Track(640, 480, video: false))));
        check(MatroskaMetadata.ReadDimensions(audioOnly) is null, "没有视频轨道的文件不显示虚假的画质标签");
    }
}

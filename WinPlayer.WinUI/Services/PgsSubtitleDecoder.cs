using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Storage;
using WinPlayer.WinUI.Models;

namespace WinPlayer.WinUI.Services;

/// <summary>
/// 解析 Blu-ray Presentation Graphics Stream。解析阶段只保存 RLE 压缩对象；
/// 字幕进入显示时间段后才把对应对象解码为 BGRA 位图。
/// </summary>
public sealed class PgsSubtitleDocument
{
    private readonly IReadOnlyList<PgsFrame> frames;
    private PgsSubtitleDocument(string label, IReadOnlyList<PgsFrame> frames)
    {
        Label = label;
        this.frames = frames;
    }

    public string Label { get; }
    public int FrameCount => frames.Count;
    public TimeSpan FirstStart => frames.Count == 0 ? TimeSpan.Zero : frames[0].Start;
    public TimeSpan LastEnd => frames.Count == 0 ? TimeSpan.Zero : frames[^1].End;

    public int FindFrameIndex(TimeSpan position)
    {
        int low = 0, high = frames.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            PgsFrame frame = frames[middle];
            if (position < frame.Start) high = middle - 1;
            else if (position >= frame.End) low = middle + 1;
            else return middle;
        }
        return -1;
    }

    public static async Task<PgsSubtitleDocument> LoadAsync(StorageFile file)
    {
        await using Stream input = await file.OpenStreamForReadAsync();
        return await Task.Run(() => Parse(input, file.Name));
    }

    public TimedMetadataTrack CreateTrack()
    {
        var track = new TimedMetadataTrack(
            $"external-pgs-{Guid.NewGuid():N}", string.Empty, TimedMetadataKind.Data)
        {
            Label = Label
        };
        for (int index = 0; index < frames.Count; index++)
        {
            PgsFrame frame = frames[index];
            track.AddCue(new DataCue
            {
                Id = $"pgs:{index}",
                // WinRT 要求 DataCue.Data 必须包含数据；同时保存帧索引便于诊断。
                Data = BitConverter.GetBytes(index).AsBuffer(),
                StartTime = frame.Start,
                Duration = frame.End > frame.Start
                    ? frame.End - frame.Start : TimeSpan.FromSeconds(5)
            });
        }
        return track;
    }

    public PgsSubtitleImage? Decode(int frameIndex)
    {
        if ((uint)frameIndex >= frames.Count) return null;
        PgsFrame frame = frames[frameIndex];
        if (frame.Objects.Count == 0) return null;

        int left = frame.Objects.Min(item => item.X);
        int top = frame.Objects.Min(item => item.Y);
        int right = frame.Objects.Max(item => item.X + item.VisibleWidth);
        int bottom = frame.Objects.Max(item => item.Y + item.VisibleHeight);
        left = Math.Clamp(left, 0, frame.CanvasWidth);
        top = Math.Clamp(top, 0, frame.CanvasHeight);
        right = Math.Clamp(right, left, frame.CanvasWidth);
        bottom = Math.Clamp(bottom, top, frame.CanvasHeight);
        int width = right - left;
        int height = bottom - top;
        if (width <= 0 || height <= 0) return null;

        byte[] destination = new byte[checked(width * height * 4)];
        foreach (PgsPlacedObject placed in frame.Objects)
        {
            byte[] indices = DecodeRle(placed.Bitmap);
            int sourceX = placed.CropX;
            int sourceY = placed.CropY;
            int visibleWidth = placed.VisibleWidth;
            int visibleHeight = placed.VisibleHeight;
            for (int y = 0; y < visibleHeight; y++)
            {
                int sourceRow = (sourceY + y) * placed.Bitmap.Width + sourceX;
                int destinationRow = (placed.Y - top + y) * width + placed.X - left;
                for (int x = 0; x < visibleWidth; x++)
                {
                    uint color = frame.Palette[indices[sourceRow + x]];
                    BlendPixel(destination, (destinationRow + x) * 4, color);
                }
            }
        }
        return new PgsSubtitleImage(destination, width, height, left, top,
            frame.CanvasWidth, frame.CanvasHeight);
    }

    private static PgsSubtitleDocument Parse(Stream stream, string label)
    {
        var palettes = new Dictionary<byte, uint[]>();
        var bitmaps = new Dictionary<ushort, PgsBitmap>();
        var builders = new Dictionary<ushort, ObjectBuilder>();
        var frames = new List<PgsFrame>();
        PgsComposition? composition = null;

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        while (TryReadSegmentHeader(reader, out uint pts, out byte type, out ushort length))
        {
            byte[] payload = reader.ReadBytes(length);
            if (payload.Length != length) throw new InvalidDataException("SUP 文件在数据段中提前结束");
            TimeSpan timestamp = TimeSpan.FromSeconds(pts / 90000d);
            switch (type)
            {
                case 0x14: ParsePalette(payload, palettes); break;
                case 0x15: ParseObject(payload, builders, bitmaps); break;
                case 0x16:
                    if (frames.Count > 0 && frames[^1].End <= frames[^1].Start)
                        frames[^1].End = timestamp;
                    composition = ParseComposition(payload, timestamp);
                    break;
                case 0x80:
                    if (composition is not null && composition.Objects.Count > 0 &&
                        palettes.TryGetValue(composition.PaletteId, out uint[]? palette))
                    {
                        var objects = new List<PgsPlacedObject>();
                        foreach (PgsCompositionObject item in composition.Objects)
                        {
                            if (!bitmaps.TryGetValue(item.ObjectId, out PgsBitmap? bitmap)) continue;
                            int cropWidth = item.Cropped ? item.CropWidth : bitmap.Width;
                            int cropHeight = item.Cropped ? item.CropHeight : bitmap.Height;
                            int cropX = item.Cropped ? Math.Clamp(item.CropX, 0, bitmap.Width) : 0;
                            int cropY = item.Cropped ? Math.Clamp(item.CropY, 0, bitmap.Height) : 0;
                            objects.Add(new PgsPlacedObject(bitmap, item.X, item.Y,
                                cropX, cropY,
                                Math.Min(cropWidth, bitmap.Width - cropX),
                                Math.Min(cropHeight, bitmap.Height - cropY)));
                        }
                        if (objects.Count > 0)
                            frames.Add(new PgsFrame(composition.Timestamp, composition.Timestamp,
                                composition.CanvasWidth, composition.CanvasHeight,
                                (uint[])palette.Clone(), objects));
                    }
                    break;
            }
        }

        if (frames.Count == 0) throw new InvalidDataException("SUP 中没有找到可显示的 PGS 图像");
        for (int index = 0; index < frames.Count; index++)
        {
            if (frames[index].End > frames[index].Start) continue;
            frames[index].End = index + 1 < frames.Count
                ? frames[index + 1].Start : frames[index].Start + TimeSpan.FromSeconds(5);
        }
        return new PgsSubtitleDocument(label, frames);
    }

    private static bool TryReadSegmentHeader(BinaryReader reader,
        out uint pts, out byte type, out ushort length)
    {
        pts = 0; type = 0; length = 0;
        int first = reader.BaseStream.ReadByte();
        if (first < 0) return false;
        int second = reader.BaseStream.ReadByte();
        if (first != 'P' || second != 'G')
            throw new InvalidDataException("不是有效的 Blu-ray SUP/PGS 文件");
        pts = ReadUInt32BigEndian(reader);
        _ = ReadUInt32BigEndian(reader); // DTS
        type = reader.ReadByte();
        length = ReadUInt16BigEndian(reader);
        return true;
    }

    private static void ParsePalette(byte[] data, Dictionary<byte, uint[]> palettes)
    {
        if (data.Length < 2) return;
        byte paletteId = data[0];
        if (!palettes.TryGetValue(paletteId, out uint[]? palette))
            palette = new uint[256];
        for (int offset = 2; offset + 4 < data.Length; offset += 5)
        {
            byte id = data[offset];
            int y = data[offset + 1];
            int cr = data[offset + 2] - 128;
            int cb = data[offset + 3] - 128;
            byte alpha = data[offset + 4];
            int red = ClampColor(1.164 * (y - 16) + 1.596 * cr);
            int green = ClampColor(1.164 * (y - 16) - 0.813 * cr - 0.391 * cb);
            int blue = ClampColor(1.164 * (y - 16) + 2.018 * cb);
            palette[id] = PackPremultiplied(blue, green, red, alpha);
        }
        palettes[paletteId] = palette;
    }

    private static void ParseObject(byte[] data, Dictionary<ushort, ObjectBuilder> builders,
        Dictionary<ushort, PgsBitmap> bitmaps)
    {
        if (data.Length < 4) return;
        ushort id = ReadUInt16BigEndian(data, 0);
        byte sequence = data[3];
        int offset;
        ObjectBuilder builder;
        if ((sequence & 0x80) != 0)
        {
            if (data.Length < 11) return;
            int expectedLength = ReadUInt24BigEndian(data, 4) - 4;
            builder = new ObjectBuilder(ReadUInt16BigEndian(data, 7),
                ReadUInt16BigEndian(data, 9), Math.Max(0, expectedLength));
            builders[id] = builder;
            offset = 11;
        }
        else
        {
            if (!builders.TryGetValue(id, out builder!)) return;
            offset = 4;
        }
        builder.Data.Write(data, offset, data.Length - offset);
        if ((sequence & 0x40) == 0) return;
        bitmaps[id] = new PgsBitmap(builder.Width, builder.Height, builder.Data.ToArray());
        builders.Remove(id);
        builder.Data.Dispose();
    }

    private static PgsComposition? ParseComposition(byte[] data, TimeSpan timestamp)
    {
        if (data.Length < 11) return null;
        int canvasWidth = ReadUInt16BigEndian(data, 0);
        int canvasHeight = ReadUInt16BigEndian(data, 2);
        byte paletteId = data[9];
        int count = data[10];
        int offset = 11;
        var objects = new List<PgsCompositionObject>(count);
        for (int index = 0; index < count && offset + 7 < data.Length; index++)
        {
            ushort objectId = ReadUInt16BigEndian(data, offset);
            byte flags = data[offset + 3];
            int x = ReadUInt16BigEndian(data, offset + 4);
            int y = ReadUInt16BigEndian(data, offset + 6);
            offset += 8;
            bool cropped = (flags & 0x80) != 0;
            int cropX = 0, cropY = 0, cropWidth = 0, cropHeight = 0;
            if (cropped && offset + 7 < data.Length)
            {
                cropX = ReadUInt16BigEndian(data, offset);
                cropY = ReadUInt16BigEndian(data, offset + 2);
                cropWidth = ReadUInt16BigEndian(data, offset + 4);
                cropHeight = ReadUInt16BigEndian(data, offset + 6);
                offset += 8;
            }
            objects.Add(new PgsCompositionObject(objectId, x, y, cropped,
                cropX, cropY, cropWidth, cropHeight));
        }
        return new PgsComposition(timestamp, canvasWidth, canvasHeight, paletteId, objects);
    }

    private static byte[] DecodeRle(PgsBitmap bitmap)
    {
        byte[] output = new byte[checked(bitmap.Width * bitmap.Height)];
        int source = 0, x = 0, y = 0;
        while (source < bitmap.Rle.Length && y < bitmap.Height)
        {
            byte value = bitmap.Rle[source++];
            if (value != 0)
            {
                if (x < bitmap.Width) output[y * bitmap.Width + x] = value;
                x++;
                continue;
            }
            if (source >= bitmap.Rle.Length) break;
            byte control = bitmap.Rle[source++];
            if (control == 0)
            {
                x = 0; y++;
                continue;
            }
            int run = control & 0x3F;
            if ((control & 0x40) != 0 && source < bitmap.Rle.Length)
                run = (run << 8) | bitmap.Rle[source++];
            byte color = (control & 0x80) != 0 && source < bitmap.Rle.Length
                ? bitmap.Rle[source++] : (byte)0;
            while (run-- > 0 && y < bitmap.Height)
            {
                if (x < bitmap.Width) output[y * bitmap.Width + x] = color;
                x++;
            }
        }
        return output;
    }

    private static void BlendPixel(byte[] target, int offset, uint color)
    {
        int alpha = (byte)(color >> 24);
        if (alpha == 0) return;
        int inverse = 255 - alpha;
        target[offset] = (byte)((byte)color + target[offset] * inverse / 255);
        target[offset + 1] = (byte)((byte)(color >> 8) + target[offset + 1] * inverse / 255);
        target[offset + 2] = (byte)((byte)(color >> 16) + target[offset + 2] * inverse / 255);
        target[offset + 3] = (byte)(alpha + target[offset + 3] * inverse / 255);
    }

    private static uint PackPremultiplied(int blue, int green, int red, byte alpha)
    {
        blue = blue * alpha / 255;
        green = green * alpha / 255;
        red = red * alpha / 255;
        return (uint)(blue | green << 8 | red << 16 | alpha << 24);
    }

    private static int ClampColor(double value) => Math.Clamp((int)Math.Round(value), 0, 255);
    private static ushort ReadUInt16BigEndian(BinaryReader reader) =>
        (ushort)(reader.ReadByte() << 8 | reader.ReadByte());
    private static uint ReadUInt32BigEndian(BinaryReader reader) =>
        (uint)(reader.ReadByte() << 24 | reader.ReadByte() << 16 |
            reader.ReadByte() << 8 | reader.ReadByte());
    private static ushort ReadUInt16BigEndian(byte[] data, int offset) =>
        (ushort)(data[offset] << 8 | data[offset + 1]);
    private static int ReadUInt24BigEndian(byte[] data, int offset) =>
        data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2];

    private sealed record PgsBitmap(int Width, int Height, byte[] Rle);
    private sealed class ObjectBuilder(int width, int height, int expectedLength)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public MemoryStream Data { get; } = new(Math.Max(0, expectedLength));
    }
    private sealed record PgsCompositionObject(ushort ObjectId, int X, int Y, bool Cropped,
        int CropX, int CropY, int CropWidth, int CropHeight);
    private sealed record PgsComposition(TimeSpan Timestamp, int CanvasWidth, int CanvasHeight,
        byte PaletteId, IReadOnlyList<PgsCompositionObject> Objects);
    private sealed record PgsPlacedObject(PgsBitmap Bitmap, int X, int Y,
        int CropX, int CropY, int VisibleWidth, int VisibleHeight);
    private sealed class PgsFrame(TimeSpan start, TimeSpan end, int canvasWidth, int canvasHeight,
        uint[] palette, IReadOnlyList<PgsPlacedObject> objects)
    {
        public TimeSpan Start { get; } = start;
        public TimeSpan End { get; set; } = end;
        public int CanvasWidth { get; } = canvasWidth;
        public int CanvasHeight { get; } = canvasHeight;
        public uint[] Palette { get; } = palette;
        public IReadOnlyList<PgsPlacedObject> Objects { get; } = objects;
    }
}

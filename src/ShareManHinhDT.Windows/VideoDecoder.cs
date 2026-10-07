using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ShareManHinhDT.Shared;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace ShareManHinhDT.Windows;

public sealed class DecodedFrame(int width, int height, byte[] pixels, int stride) : IDisposable
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
    public int Stride { get; } = stride;
    public void Dispose() => ArrayPool<byte>.Shared.Return(Pixels);
}

public sealed unsafe class VideoDecoder : IDisposable
{
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NotAccepting = unchecked((int)0xC00D36B0);
    private const int NoMoreTypes = unchecked((int)0xC00D36B9);
    private IMFTransform? decoder;
    private IMFTransform? converter;
    private int width;
    private int height;
    private int outputStride;
    private int codedWidth;
    private int codedHeight;
    private int cropX;
    private int cropY;
    private bool disposed;

    public VideoDecoder() => PInvoke.MFStartup(0x20070, 0).ThrowOnFailure();

    public void Configure(VideoConfiguration configuration)
    {
        configuration.Validate();
        Reset();
        width = configuration.Width;
        height = configuration.Height;
        decoder = CreateTransform("62CE7E72-4C71-4D20-B15D-452831A87D9D");
        decoder.GetAttributes(out var attributes);
        try
        {
            Guid latencyKey = PInvoke.MF_LOW_LATENCY;
            attributes.SetUINT32(&latencyKey, 1);
        }
        finally { Release(attributes); }
        var input = CreateType(PInvoke.MFVideoFormat_H264, width, height, configuration.FrameRate);
        try
        {
            Guid key = PInvoke.MF_MT_MPEG_SEQUENCE_HEADER;
            input.SetBlob(&key, configuration.CodecData, (uint)configuration.CodecData.Length);
            decoder.SetInputType(0, input, 0);
        }
        finally { Release(input); }
        ConfigureOutput();
        Begin(decoder);
    }

    private void ConfigureOutput()
    {
        Release(converter);
        converter = null;
        for (uint index = 0; ; index++)
        {
            IMFMediaType output;
            try { decoder!.GetOutputAvailableType(0, index, out output); }
            catch (COMException ex) when (ex.HResult == NoMoreTypes) { throw new NotSupportedException("Windows không cung cấp định dạng NV12.", ex); }
            try
            {
                Guid subtypeKey = PInvoke.MF_MT_SUBTYPE;
                Guid subtype;
                output.GetGUID(&subtypeKey, &subtype);
                if (subtype != PInvoke.MFVideoFormat_NV12) continue;
                Guid sizeKey = PInvoke.MF_MT_FRAME_SIZE;
                output.GetUINT64(&sizeKey, out var size);
                codedWidth = (int)(size >> 32);
                codedHeight = (int)(size & uint.MaxValue);
                cropX = cropY = 0;
                var aperture = ReadAperture(output, PInvoke.MF_MT_MINIMUM_DISPLAY_APERTURE)
                    ?? ReadAperture(output, PInvoke.MF_MT_GEOMETRIC_APERTURE);
                if (aperture is { } area)
                {
                    if (area.Width != width || area.Height != height)
                        throw new InvalidDataException("Vùng hình H.264 không khớp cấu hình phiên.");
                    cropX = area.X;
                    cropY = area.Y;
                }
                else if ((codedWidth != width && codedWidth != (width + 15) / 16 * 16) ||
                    (codedHeight != height && codedHeight != (height + 15) / 16 * 16))
                    throw new InvalidDataException($"Kích thước H.264 {codedWidth} × {codedHeight} không khớp {width} × {height}.");
                if (codedWidth < width || codedHeight < height || codedWidth > 1296 || codedHeight > 1296 ||
                    cropX < 0 || cropY < 0 || cropX + width > codedWidth || cropY + height > codedHeight)
                    throw new InvalidDataException("Vùng hình H.264 nằm ngoài bộ đệm giải mã.");
                decoder.SetOutputType(0, output, 0);
                converter = CreateTransform("98230571-0087-4204-B020-3282538E57D3");
                converter.SetInputType(0, output, 0);
                var rgb = CreateType(PInvoke.MFVideoFormat_RGB32, codedWidth, codedHeight, 30);
                try
                {
                    Guid strideKey = PInvoke.MF_MT_DEFAULT_STRIDE;
                    outputStride = codedWidth * 4;
                    rgb.SetUINT32(&strideKey, (uint)outputStride);
                    converter.SetOutputType(0, rgb, 0);
                }
                finally { Release(rgb); }
                Begin(converter);
                return;
            }
            finally { Release(output); }
        }
    }

    private static (int X, int Y, int Width, int Height)? ReadAperture(IMFMediaType type, Guid key)
    {
        uint length;
        try { type.GetBlobSize(&key, out length); }
        catch (COMException ex) when (ex.HResult == unchecked((int)0xC00D36E6)) { return null; }
        if (length != 16) throw new InvalidDataException("Vùng hình Media Foundation sai định dạng.");
        byte[] data = new byte[16];
        uint actual = 0;
        type.GetBlob(&key, data, 16, &actual);
        if (actual != 16 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) != 0)
            throw new InvalidDataException("Vùng hình Media Foundation có offset không hỗ trợ.");
        return (BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(2)), BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(6)),
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)));
    }

    public IReadOnlyList<DecodedFrame> Decode(byte[] data, long timestampUs)
    {
        if (decoder is null || converter is null) throw new InvalidOperationException("Chưa cấu hình bộ giải mã.");
        var frames = new List<DecodedFrame>();
        var sample = CreateSample(data, checked(timestampUs * 10));
        try
        {
            try { decoder.ProcessInput(0, sample, 0); }
            catch (COMException ex) when (ex.HResult == NotAccepting)
            {
                DrainDecoder(frames);
                decoder.ProcessInput(0, sample, 0);
            }
            DrainDecoder(frames);
            return frames;
        }
        catch
        {
            foreach (var frame in frames) frame.Dispose();
            throw;
        }
        finally { Release(sample); }
    }

    private void DrainDecoder(List<DecodedFrame> frames)
    {
        while (true)
        {
            IMFSample? decoded;
            try { decoded = ReadOutput(decoder!); }
            catch (COMException ex) when (ex.HResult == StreamChange) { ConfigureOutput(); continue; }
            if (decoded is null) return;
            try
            {
                converter!.ProcessInput(0, decoded, 0);
                while (ReadOutput(converter) is { } converted)
                {
                    try { frames.Add(CopyFrame(converted)); }
                    finally { Release(converted); }
                }
            }
            finally { Release(decoded); }
        }
    }

    private DecodedFrame CopyFrame(IMFSample sample)
    {
        sample.ConvertToContiguousBuffer(out var buffer);
        try
        {
            byte* pointer;
            uint length;
            buffer.Lock(&pointer, null, &length);
            try
            {
                int stride = Math.Abs(outputStride);
                if (length < (codedHeight - 1) * stride + codedWidth * 4) throw new InvalidDataException("Bộ đệm ảnh Windows không đủ dữ liệu.");
                var pixels = ArrayPool<byte>.Shared.Rent(width * height * 4);
                for (int y = 0; y < height; y++)
                {
                    int sourceY = outputStride >= 0 ? cropY + y : codedHeight - 1 - cropY - y;
                    new ReadOnlySpan<byte>(pointer + sourceY * stride + cropX * 4, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
                }
                return new(width, height, pixels, width * 4);
            }
            finally { buffer.Unlock(); }
        }
        finally { Release(buffer); }
    }

    internal static IMFTransform CreateTransform(string classId) =>
        (IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(classId), true)!)!;

    internal static IMFMediaType CreateType(Guid subtype, int width, int height, int frameRate)
    {
        PInvoke.MFCreateMediaType(out var type).ThrowOnFailure();
        Guid key = PInvoke.MF_MT_MAJOR_TYPE;
        Guid value = PInvoke.MFMediaType_Video;
        type.SetGUID(&key, &value);
        key = PInvoke.MF_MT_SUBTYPE;
        type.SetGUID(&key, &subtype);
        key = PInvoke.MF_MT_FRAME_SIZE;
        type.SetUINT64(&key, ((ulong)width << 32) | (uint)height);
        key = PInvoke.MF_MT_FRAME_RATE;
        type.SetUINT64(&key, ((ulong)frameRate << 32) | 1);
        key = PInvoke.MF_MT_PIXEL_ASPECT_RATIO;
        type.SetUINT64(&key, (1UL << 32) | 1);
        key = PInvoke.MF_MT_INTERLACE_MODE;
        type.SetUINT32(&key, 2);
        return type;
    }

    internal static IMFSample CreateSample(byte[] data, long timestamp)
    {
        PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
        PInvoke.MFCreateMemoryBuffer((uint)data.Length, out var buffer).ThrowOnFailure();
        try
        {
            byte* pointer;
            buffer.Lock(&pointer, null, null);
            try { data.AsSpan().CopyTo(new Span<byte>(pointer, data.Length)); }
            finally { buffer.Unlock(); }
            buffer.SetCurrentLength((uint)data.Length);
            sample.AddBuffer(buffer);
            sample.SetSampleTime(timestamp);
            sample.SetSampleDuration(10_000_000 / 30);
            return sample;
        }
        catch { Release(sample); throw; }
        finally { Release(buffer); }
    }

    internal static IMFSample? ReadOutput(IMFTransform transform)
    {
        MFT_OUTPUT_STREAM_INFO info;
        transform.GetOutputStreamInfo(0, &info);
        IMFSample? supplied = null;
        if ((info.dwFlags & 0x100) == 0)
        {
            PInvoke.MFCreateSample(out supplied).ThrowOnFailure();
            PInvoke.MFCreateMemoryBuffer(Math.Max(info.cbSize, 1u), out var buffer).ThrowOnFailure();
            try { supplied.AddBuffer(buffer); }
            finally { Release(buffer); }
        }
        MFT_OUTPUT_DATA_BUFFER[] output = [new() { pSample = supplied! }];
        try
        {
            transform.ProcessOutput(0, 1, output, out _);
            return output[0].pSample;
        }
        catch (COMException ex) when (ex.HResult == NeedMoreInput)
        {
            Release(output[0].pSample ?? supplied);
            return null;
        }
        catch
        {
            Release(output[0].pSample ?? supplied);
            throw;
        }
        finally { Release(output[0].pEvents); }
    }

    internal static void Begin(IMFTransform transform)
    {
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private void Reset()
    {
        Release(converter);
        Release(decoder);
        converter = null;
        decoder = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Reset();
        PInvoke.MFShutdown().ThrowOnFailure();
    }
}

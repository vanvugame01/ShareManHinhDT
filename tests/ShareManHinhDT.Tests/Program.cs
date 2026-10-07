using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ShareManHinhDT.Shared;
using System.Numerics;

int passed = 0;
await Check("Ảnh QR đúng góc, căn giữa, giữ tỷ lệ và không lật gương", () =>
{
    foreach (int sensor in new[] { 0, 90, 180, 270 })
        foreach (int rotation in new[] { 0, 90, 180, 270 })
            foreach (var buffer in new[] { (640, 480), (1280, 720) })
                foreach (var view in new[] { (360, 640), (640, 360), (450, 450) })
                {
                    float naturalWidth = sensor % 180 == 0 ? buffer.Item1 : buffer.Item2;
                    float naturalHeight = sensor % 180 == 0 ? buffer.Item2 : buffer.Item1;
                    var transform = ShareManHinhDT.Android.CameraPreviewTransform.Create(buffer.Item1, buffer.Item2,
                        view.Item1, view.Item2, sensor, rotation);
                    Vector2 center = new(view.Item1 / 2f, view.Item2 / 2f);
                    Assert(Vector2.Distance(Vector2.Transform(center, transform), center) < 0.001f);
                    // Mo phong anh TextureView da dinh huong cam bien va gian vao view.
                    var combined = Matrix3x2.CreateScale(view.Item1 / naturalWidth, view.Item2 / naturalHeight) * transform;
                    Vector2 origin = Vector2.Transform(Vector2.Zero, combined);
                    Vector2 axisX = Vector2.Transform(Vector2.UnitX, combined) - origin;
                    Vector2 axisY = Vector2.Transform(Vector2.UnitY, combined) - origin;
                    Assert(Math.Abs(axisX.Length() - axisY.Length()) < 0.001f);
                    Assert(Math.Abs(Vector2.Dot(axisX, axisY)) < 0.001f && combined.GetDeterminant() > 0);
                    Vector2 expectedX = rotation switch
                    {
                        0 => Vector2.UnitX,
                        90 => -Vector2.UnitY,
                        180 => -Vector2.UnitX,
                        _ => Vector2.UnitY
                    };
                    Assert(Vector2.Distance(Vector2.Normalize(axisX), expectedX) < 0.001f);
                    if (rotation == 0) Assert(transform.M12 == 0 && transform.M21 == 0);
                    Vector2[] corners = [Vector2.Zero, new(naturalWidth, 0), new(0, naturalHeight), new(naturalWidth, naturalHeight)];
                    var mapped = corners.Select(point => Vector2.Transform(point, combined)).ToArray();
                    Assert(mapped.Min(p => p.X) <= 0.001f && mapped.Max(p => p.X) >= view.Item1 - 0.001f);
                    Assert(mapped.Min(p => p.Y) <= 0.001f && mapped.Max(p => p.Y) >= view.Item2 - 0.001f);
                }
    return Task.CompletedTask;
});
await Check("Ma trận camera từ chối kích thước/góc không hợp lệ", async () =>
{
    await Throws<ArgumentOutOfRangeException>(() =>
    {
        ShareManHinhDT.Android.CameraPreviewTransform.Create(640, 480, 0, 640, 90, 0);
        return Task.CompletedTask;
    });
    await Throws<ArgumentOutOfRangeException>(() =>
    {
        ShareManHinhDT.Android.CameraPreviewTransform.Create(640, 480, 360, 640, 45, 0);
        return Task.CompletedTask;
    });
    await Throws<ArgumentOutOfRangeException>(() =>
    {
        ShareManHinhDT.Android.CameraPreviewTransform.Create(640, 480, 360, 640, 90, 360);
        return Task.CompletedTask;
    });
});
await Check("Lỗi chia sẻ đầu tiên được giữ qua dọn dẹp ở mọi bước", () =>
{
    foreach (string step in new[] { "foreground", "projection", "display size", "encoder", "virtual display", "configuration", "first frame", "network" })
    {
        var state = new ShareManHinhDT.Android.CaptureRunState();
        state.Trace(step);
        Assert(state.End($"failure: {step}", true, "original stack trace"));
        Assert(!state.End("Đã dừng chia sẻ"));
        Assert(!state.End("cleanup exception", true, "secondary stack"));
        Assert(state.Outcome!.IsError && state.Outcome.Reason == $"failure: {step}");
        Assert(state.Outcome.Details!.Contains(step) && state.Outcome.Details.Contains("original stack trace"));
        Assert(!state.Outcome.Details.Contains("secondary stack"));
    }
    var normal = new ShareManHinhDT.Android.CaptureRunState();
    Assert(normal.End("Dừng chủ động"));
    Assert(!normal.End("cleanup failure", true, "trace"));
    Assert(!normal.Outcome!.IsError && normal.Outcome.Details is null);
    var bounded = new ShareManHinhDT.Android.CaptureRunState();
    for (int i = 0; i < 1000; i++) bounded.Trace($"step-{i}");
    bounded.End("failure", true, "stack");
    Assert(!bounded.Outcome!.Details!.Contains("step-0\n") && bounded.Outcome.Details.Contains("step-999"));
    return Task.CompletedTask;
});
await Check("Quyền chia sẻ cũ hoặc đã dùng không khởi động phiên mới", () =>
{
    var gate = new ShareManHinhDT.Android.CapturePermissionGate();
    Assert(!gate.Consume("new-session"));
    gate.Begin("old-session");
    Assert(!gate.Consume("new-session"));
    Assert(!gate.Consume("old-session"));
    gate.Begin("current-session");
    Assert(gate.Consume("current-session"));
    Assert(!gate.Consume("current-session"));
    gate.Begin("disconnected");
    Assert(!gate.Consume(null));
    return Task.CompletedTask;
});
await Check("Chọn kích thước encoder theo căn chỉnh, 30 fps và tỷ lệ màn hình", () =>
{
    foreach (var alignment in new[] { (2, 2), (16, 16), (32, 8), (3, 3) })
        foreach (var source in new[] { (1220, 2712), (2712, 1220), (1080, 2400) })
        {
            var size = ShareManHinhDT.Android.EncoderSelection.FindSize(source.Item1, source.Item2, alignment.Item1,
                alignment.Item2, (w, h) => w >= 128 && h >= 128 && w * h <= 1280 * 720)!.Value;
            Assert(size.Width % alignment.Item1 == 0 && size.Height % alignment.Item2 == 0);
            Assert(size.Width % 2 == 0 && size.Height % 2 == 0);
            Assert(size.Width * size.Height <= 1280 * 720);
            Assert(Math.Abs(Math.Log((double)size.Width / size.Height / ((double)source.Item1 / source.Item2))) <= 0.03);
        }
    Assert(ShareManHinhDT.Android.EncoderSelection.FindSize(1080, 2400, 16, 16, (_, _) => false) is null);
    var native = ShareManHinhDT.Android.EncoderSelection.FindSize(1280, 720, 16, 16, (_, _) => true);
    Assert(native == (1280, 720));
    return Task.CompletedTask;
});
await Check("Codec từ chối khóa FPS được thử lại một lần với frame rate giữ nguyên", () =>
{
    var attempts = new List<bool>();
    int retries = 0;
    string result = ShareManHinhDT.Android.EncoderSelection.ConfigureWithFpsFallback(optional =>
    {
        attempts.Add(optional);
        if (optional) throw new InvalidOperationException("optional FPS rejected");
        return "30 fps, default profile";
    }, _ => retries++);
    Assert(result == "30 fps, default profile" && attempts.SequenceEqual(new[] { true, false }) && retries == 1);
    attempts.Clear();
    bool rejected = false;
    try
    {
        ShareManHinhDT.Android.EncoderSelection.ConfigureWithFpsFallback<string>(optional =>
        {
            attempts.Add(optional);
            throw new NotSupportedException("unsupported encoder");
        }, _ => { });
    }
    catch (NotSupportedException) { rejected = true; }
    Assert(rejected && attempts.SequenceEqual(new[] { true, false }));
    return Task.CompletedTask;
});
await Check("Giải mã QR chậm không xếp hàng và bỏ kết quả sau khi dừng", async () =>
{
    using var unblock = new ManualResetEventSlim();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int callbacks = 0, failures = 0;
    byte[] snapshot = [1, 2, 3, 4];
    var worker = new ShareManHinhDT.Android.QrDecodeWorker((pixels, width, height) =>
    {
        started.SetResult();
        if (!unblock.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        Assert(pixels.SequenceEqual(snapshot) && width == 2 && height == 2);
        finished.SetResult();
        return "late QR";
    }, _ => Interlocked.Increment(ref callbacks), _ => Interlocked.Increment(ref failures));
    try
    {
        Assert(worker.TryDecode(snapshot.ToArray(), 2, 2));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 1000; i++) Assert(!worker.TryDecode([9], 1, 1));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        worker.Stop();
        Assert(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert(!worker.TryDecode([9], 1, 1));
    }
    finally { unblock.Set(); }
    await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(50);
    Assert(callbacks == 0 && failures == 0);
});
await Check("Giải mã QR tiếp tục sau ảnh trống và báo ngoại lệ", async () =>
{
    int calls = 0;
    var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var worker = new ShareManHinhDT.Android.QrDecodeWorker((_, _, _) =>
        Interlocked.Increment(ref calls) == 1 ? null : "valid QR", value => result.SetResult(value), result.SetException);
    Assert(worker.TryDecode([0], 1, 1));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (worker.IsBusy) await Task.Delay(1, timeout.Token);
    Assert(worker.TryDecode([1], 1, 1));
    Assert(await result.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "valid QR");
    worker.Stop();
    var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
    var broken = new ShareManHinhDT.Android.QrDecodeWorker((_, _, _) => throw new InvalidDataException("test"),
        _ => throw new Exception("Không được trả QR"), ex => error.SetResult(ex));
    Assert(broken.TryDecode([0], 1, 1));
    Assert(await error.Task.WaitAsync(TimeSpan.FromSeconds(5)) is InvalidDataException);
    broken.Stop();
});

await Check("Thông điệp bị chia nhỏ vẫn đọc đúng", async () =>
{
    using var memory = new MemoryStream();
    var writer = new WireConnection(memory);
    await writer.SendAsync(WireMessage.Json(MessageType.Authenticate, new Authentication("123456", "Thiết bị thử")));
    await writer.SendAsync(new(MessageType.VideoFrame, [0, 0, 0, 1, 0x65, 1, 2], 12345, WireMessage.KeyFrameFlag));
    memory.Position = 0;
    await using var reader = new WireConnection(new FragmentedStream(memory, 2));
    var auth = await reader.ReceiveAsync();
    Assert(auth!.ReadJson<Authentication>().DeviceName == "Thiết bị thử");
    var frame = await reader.ReceiveAsync();
    Assert(frame!.TimestampUs == 12345 && frame.Flags == 1 && frame.Payload.Length == 7);
    Assert(await reader.ReceiveAsync() is null);
});
await Check("Từ chối header sai trước khi cấp phát payload", async () =>
{
    byte[] baseline;
    using (var output = new MemoryStream())
    {
        await new WireConnection(output).SendAsync(new(MessageType.Ping, []));
        baseline = output.ToArray();
    }
    foreach (var mutation in new Action<byte[]>[]
    {
        b => b[0] = 0,
        b => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 2),
        b => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), 99),
        b => BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), int.MaxValue),
        b => BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), -1),
        b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 2),
        b => BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(12), -1)
    })
    {
        var data = baseline.ToArray();
        mutation(data);
        await Throws<InvalidDataException>(async () => await new WireConnection(new MemoryStream(data)).ReceiveAsync());
    }
});
await Check("Từ chối gói bị cắt dở", async () =>
{
    using var bytes = new MemoryStream();
    await new WireConnection(bytes).SendAsync(WireMessage.Json(MessageType.Error, new SessionError("Thử lỗi")));
    await Throws<EndOfStreamException>(async () => await new WireConnection(new MemoryStream(bytes.ToArray()[..^1])).ReceiveAsync());
    await Throws<EndOfStreamException>(async () => await new WireConnection(new MemoryStream([0x53])).ReceiveAsync());
});
await Check("Ghi đồng thời không trộn lẫn thông điệp", async () =>
{
    using var bytes = new MemoryStream();
    var connection = new WireConnection(bytes);
    await Task.WhenAll(Enumerable.Range(0, 100).Select(i => connection.SendAsync(WireMessage.Json(MessageType.Error, new SessionError(i.ToString()))).AsTask()));
    bytes.Position = 0;
    var seen = new HashSet<string>();
    for (int i = 0; i < 100; i++) seen.Add((await connection.ReceiveAsync())!.ReadJson<SessionError>().Message);
    Assert(seen.Count == 100);
});
await Check("Giới hạn video, chuyển Annex B, xoay và giữ tỷ lệ", async () =>
{
    new VideoConfiguration(720, 1280, 30, 2_500_000, [0, 0, 0, 1, 0x67]).Validate();
    await Throws<InvalidDataException>(() => Task.Run(() => new VideoConfiguration(1920, 1080, 30, 2_500_000, [0, 0, 0, 1]).Validate()));
    await Throws<InvalidDataException>(() => Task.Run(() => new VideoConfiguration(721, 1280, 30, 2_500_000, [0, 0, 0, 1]).Validate()));
    var nal = H264.ToAnnexB([0, 0, 0, 2, 0x67, 0x42, 0, 0, 0, 2, 0x68, 0xCE]);
    Assert(nal.SequenceEqual(new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xCE }));
    await Throws<InvalidDataException>(() => Task.Run(() => H264.ToAnnexB([0, 0, 0, 9, 1])));
    Assert(H264.Fit720p(1080, 1920) == (720, 1280));
    Assert(H264.Fit720p(1920, 1080) == (1280, 720));
    var odd = H264.Fit720p(1440, 3120);
    Assert(odd.Width % 2 == 0 && odd.Height == 1280 && odd.Width * odd.Height <= 1280 * 720);
});
await Check("Mã ghép đôi và vân tay chứng chỉ", () =>
{
    Assert(Enumerable.Range(0, 100).Select(_ => Pairing.CreateCode()).All(c => c.Length == 6 && c.All(char.IsAsciiDigit)));
    Assert(Pairing.Matches("012345", "012345") && !Pairing.Matches("012345", "12345"));
    Assert(Pairing.Fingerprint([1, 2, 3]).Length == 64);
    Assert(!Pairing.Matches(Pairing.Fingerprint([1, 2, 3]), Pairing.Fingerprint([1, 2, 4])));
    return Task.CompletedTask;
});
await Check("TLS pin đúng truyền được; chứng chỉ khác bị từ chối", async () =>
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    await TlsRoundTrip(certificate, Pairing.Fingerprint(certificate.RawData), true);
    await TlsRoundTrip(certificate, new string('0', 64), false);
});
await Check("QR tạo/đọc đúng, đổi kích thước và xoay 90/180/270 độ", () =>
{
    var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    var payload = new PairingQrPayload(1, "192.168.1.20", PairingQr.Port, "001234", new string('A', 64), now.AddMinutes(5));
    string text = PairingQr.Encode(payload, now);
    Assert(PairingQr.Parse(text, now) == payload);
    var matrix = PairingQr.CreateMatrix(payload, now);
    foreach (int scale in new[] { 3, 5, 7 })
    {
        int side = matrix.Width * scale;
        byte[] pixels = new byte[side * side];
        byte[] rotated = new byte[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                byte value = matrix[x / scale, y / scale] ? (byte)0 : (byte)255;
                pixels[y * side + x] = value;
                rotated[x * side + side - 1 - y] = value;
            }
        Assert(PairingQr.DecodeGrayscale(pixels, side, side) == text);
        Assert(PairingQr.DecodeGrayscale(rotated, side, side) == text);
        for (int angle = 180; angle <= 270; angle += 90)
        {
            byte[] next = new byte[side * side];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++) next[x * side + side - 1 - y] = rotated[y * side + x];
            Assert(PairingQr.DecodeGrayscale(next, side, side) == text);
            rotated = next;
        }
    }
    Assert(PairingQr.DecodeGrayscale(Enumerable.Repeat((byte)255, 640 * 480).ToArray(), 640, 480) is null);
    return Task.CompletedTask;
});
await Check("Từ chối QR không hợp lệ hoặc hết hạn", async () =>
{
    var now = DateTimeOffset.UtcNow;
    string text = PairingQr.Encode(new(1, "192.168.1.20", PairingQr.Port, "001234", new string('A', 64), now.AddMinutes(5)), now);
    foreach (string invalid in new[]
    {
        "https://example.com", "", new string('x', 1025),
        text.Replace("v=1", "v=2"), text.Replace("192.168.1.20", "127.0.0.1"),
        text.Replace("192.168.1.20", "not-an-ip"), text.Replace("192.168.1.20", "255.255.255.255"),
        text.Replace("port=48731", "port=443"), text.Replace("code=001234", "code=1234"),
        text.Replace("code=001234", "code=00X234"), text.Replace(new string('A', 64), new string('G', 64)),
        text.Replace("&fp=" + new string('A', 64), ""), text + "&code=123456", text + "#fragment",
        text[..text.LastIndexOf("exp=", StringComparison.Ordinal)] + "exp=999999999999999999"
    }) await Throws<InvalidDataException>(() => Task.Run(() => PairingQr.Parse(invalid, now)));
    await Throws<InvalidDataException>(() => Task.Run(() => PairingQr.Parse(text, now.AddMinutes(6))));
});
await Check("QR không thay pin đã tin cậy; quét lặp chỉ được nhận một lần", async () =>
{
    string pin = new string('A', 64);
    Assert(PairingQr.SelectFingerprint(null, pin) == pin);
    Assert(PairingQr.SelectFingerprint(pin, pin) == pin);
    Assert(PairingQr.SelectFingerprint(pin, null) == pin);
    await Throws<InvalidDataException>(() => Task.Run(() => PairingQr.SelectFingerprint(pin, new string('B', 64))));
    var gate = new QrScanGate();
    string text = PairingQr.Encode(new(1, "192.168.1.20", PairingQr.Port, "001234", pin, DateTimeOffset.UtcNow.AddMinutes(5)));
    int accepted = 0;
    await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() => { if (gate.TryAccept(text, out _)) Interlocked.Increment(ref accepted); })));
    Assert(accepted == 1 && gate.IsCompleted);
});
Console.WriteLine($"Đã đạt {passed} nhóm kiểm thử.");

async Task Check(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine($"ĐẠT: {name}");
}
static void Assert(bool condition) { if (!condition) throw new Exception("Kiểm thử thất bại."); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Không nhận được lỗi {typeof(T).Name}.");
}
static async Task TlsRoundTrip(X509Certificate2 certificate, string pin, bool accept)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
        using var ssl = new SslStream(tcp.GetStream());
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
            if (accept)
            {
                await using var wire = new WireConnection(ssl);
                Assert((await wire.ReceiveAsync(timeout.Token))!.Type == MessageType.Ping);
                await wire.SendAsync(new(MessageType.Pong, []), timeout.Token);
            }
        }
        catch (Exception ex) when (!accept && ex is AuthenticationException or IOException) { }
    });
    try
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert is not null && Pairing.Matches(pin, Pairing.Fingerprint(cert.GetRawCertData())));
        if (accept)
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token);
            await using var wire = new WireConnection(ssl);
            await wire.SendAsync(new(MessageType.Ping, []), timeout.Token);
            Assert((await wire.ReceiveAsync(timeout.Token))!.Type == MessageType.Pong);
        }
        else await Throws<AuthenticationException>(() => ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token));
        await server;
    }
    finally { listener.Stop(); }
}

sealed class FragmentedStream(Stream inner, int chunk) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunk));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], token);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShareManHinhDT.Shared;
using ShareManHinhDT.Windows;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace ShareManHinhDT.Windows.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var fixture = CreateFixture(1280, 720, 90);
            Console.WriteLine($"ĐẠT: Bộ mã hóa H.264 Windows tạo {fixture.Frames.Count} khung thử.");
            var watch = Stopwatch.StartNew();
            int frames = 0;
            using (var decoder = new VideoDecoder())
            {
                decoder.Configure(fixture.Configuration);
                foreach (var encoded in fixture.Frames)
                    foreach (var frame in decoder.Decode(encoded.Data, encoded.Timestamp))
                    {
                        using (frame)
                        {
                            Assert(frame.Width == 1280 && frame.Height == 720, "Kích thước ảnh sai.");
                            Assert(frame.Pixels[100 * frame.Stride] < frame.Pixels[600 * frame.Stride], "Ảnh bị lật hoặc màu sai.");
                            frames++;
                        }
                    }
            }
            Assert(frames >= 85, $"Bộ giải mã chỉ xuất {frames}/90 khung.");
            Console.WriteLine($"ĐẠT: Giải mã/chuyển màu {frames} khung 720p, {frames / watch.Elapsed.TotalSeconds:F1} fps (đo cục bộ, chưa gồm mạng/Android/WPF).");
            TestRotation();
            TestQrPresentation();
            TestReceiverAsync(fixture).GetAwaiter().GetResult();
            RenderWindow();
            Console.WriteLine("Đã đạt kiểm thử tích hợp Windows.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void TestRotation()
    {
        var landscape = CreateFixture(320, 240, 12);
        var portrait = CreateFixture(240, 320, 12);
        var tall = CreateFixture(574, 1280, 12);
        using var decoder = new VideoDecoder();
        foreach (var fixture in new[] { landscape, portrait, tall, landscape })
        {
            decoder.Configure(fixture.Configuration);
            int count = 0;
            foreach (var input in fixture.Frames)
                foreach (var frame in decoder.Decode(input.Data, input.Timestamp))
                    using (frame)
                    {
                        Assert(frame.Width == fixture.Configuration.Width && frame.Height == fixture.Configuration.Height, "Sai kích thước sau xoay.");
                        Assert(frame.Pixels[frame.Stride * (frame.Height / 4)] < frame.Pixels[frame.Stride * (frame.Height * 3 / 4)],
                            "Crop/stride làm sai chiều ảnh.");
                        count++;
                    }
            Assert(count >= 8, "Không phục hồi sau thay đổi cấu hình.");
        }
        Console.WriteLine("ĐẠT: Tái cấu hình ngang → dọc → 574 × 1280 → ngang.");
        bool rejected = false;
        try
        {
            decoder.Configure(tall.Configuration with { Width = 576 });
            foreach (var input in tall.Frames)
                foreach (var frame in decoder.Decode(input.Data, input.Timestamp)) frame.Dispose();
        }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Chấp nhận vùng hình khác cấu hình phiên.");
    }

    private static async Task TestReceiverAsync(Fixture fixture)
    {
        await using var receiver = new ReceiverServer();
        PairingInfo? pairing = null;
        int count = 0;
        var connectionStates = new List<bool>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retainedError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retainedPhoneError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.PairingChanged += info => { pairing = info; };
        receiver.ConnectionChanged += connected => connectionStates.Add(connected);
        receiver.StatusChanged += message =>
        {
            Console.WriteLine($"Bộ nhận: {message}");
            if (message.Contains("Lỗi phiên gần nhất") && message.Contains("Khung video không hợp lệ")) retainedError.TrySetResult();
            if (message.Contains("Lỗi phiên gần nhất") && message.Contains("encoder thử bị từ chối")) retainedPhoneError.TrySetResult();
        };
        receiver.FrameDecoded += frame => { frame.Dispose(); if (Interlocked.Increment(ref count) >= 10) ready.TrySetResult(); };
        receiver.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<(TcpClient Client, WireConnection Wire)> Connect(string? qrFingerprint = null)
        {
            var tcp = new TcpClient { NoDelay = true };
            SslStream? ssl = null;
            try
            {
                string pin = PairingQr.SelectFingerprint(null, qrFingerprint) ?? receiver.Fingerprint;
                await tcp.ConnectAsync(IPAddress.Loopback, ReceiverServer.Port, timeout.Token);
                ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert is not null && Pairing.Matches(pin, Pairing.Fingerprint(cert.GetRawCertData())));
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, timeout.Token);
                return (tcp, new WireConnection(ssl));
            }
            catch { ssl?.Dispose(); tcp.Dispose(); throw; }
        }
        bool wrongPinRejected = false;
        try { var unexpected = await Connect(new string('0', 64)); unexpected.Client.Dispose(); }
        catch (AuthenticationException) { wrongPinRejected = true; }
        Assert(wrongPinRejected && connectionStates.Count == 0, "Pin QR sai không bị chặn trước xác thực.");
        Console.WriteLine("ĐẠT: Pin QR sai bị từ chối tại TLS trước khi gửi mã ghép đôi.");
        var wrong = await Connect();
        using (wrong.Client)
        await using (wrong.Wire)
        {
            string incorrect = pairing!.Code == "000000" ? "111111" : "000000";
            await wrong.Wire.SendAsync(WireMessage.Json(MessageType.Authenticate, new Authentication(incorrect, "Thử mã sai")), timeout.Token);
            Assert((await wrong.Wire.ReceiveAsync(timeout.Token))!.Type == MessageType.Error, "Chấp nhận mã sai.");
        }
        var valid = await Connect();
        using (valid.Client)
        await using (valid.Wire)
        {
            await valid.Wire.SendAsync(WireMessage.Json(MessageType.Authenticate, new Authentication(pairing!.Code, "Điện thoại giả lập giao thức")), timeout.Token);
            Assert((await valid.Wire.ReceiveAsync(timeout.Token))!.Type == MessageType.Accepted, "Không chấp nhận mã đúng.");
            await valid.Wire.SendAsync(WireMessage.Json(MessageType.VideoConfiguration, fixture.Configuration), timeout.Token);
            Assert((await valid.Wire.ReceiveAsync(timeout.Token))!.Type == MessageType.RequestKeyFrame, "Không yêu cầu khung khóa.");
            int index = 0;
            foreach (var frame in fixture.Frames.Take(20))
                await valid.Wire.SendAsync(new(MessageType.VideoFrame, frame.Data, frame.Timestamp, index++ == 0 ? WireMessage.KeyFrameFlag : 0), timeout.Token);
            await ready.Task.WaitAsync(timeout.Token);
            await valid.Wire.SendAsync(new(MessageType.Ping, []), timeout.Token);
            Assert((await valid.Wire.ReceiveAsync(timeout.Token))!.Type == MessageType.Pong, "Heartbeat không hoạt động.");
            await valid.Wire.SendAsync(new(MessageType.Stop, []), timeout.Token);
        }
        var invalidVideo = await Connect();
        using (invalidVideo.Client)
        await using (invalidVideo.Wire)
        {
            await invalidVideo.Wire.SendAsync(WireMessage.Json(MessageType.Authenticate, new Authentication(pairing!.Code, "Thử lỗi bộ nhận")), timeout.Token);
            Assert((await invalidVideo.Wire.ReceiveAsync(timeout.Token))!.Type == MessageType.Accepted, "Không ghép đôi lại được.");
            await invalidVideo.Wire.SendAsync(new(MessageType.VideoFrame, [0, 0, 0, 1, 0x65], 0, WireMessage.KeyFrameFlag), timeout.Token);
            var error = await invalidVideo.Wire.ReceiveAsync(timeout.Token);
            Assert(error!.Type == MessageType.Error && error.ReadJson<SessionError>().Message.Contains("Khung video không hợp lệ"),
                "PC không gửi lỗi gốc về điện thoại trước khi đóng TLS.");
            await retainedError.Task.WaitAsync(timeout.Token);
        }
        var phoneError = await Connect();
        using (phoneError.Client)
        await using (phoneError.Wire)
        {
            await phoneError.Wire.SendAsync(WireMessage.Json(MessageType.Authenticate, new Authentication(pairing!.Code, "Thử lỗi encoder")), timeout.Token);
            Assert((await phoneError.Wire.ReceiveAsync(timeout.Token))!.Type == MessageType.Accepted, "Không ghép đôi sau lỗi được.");
            await phoneError.Wire.SendAsync(WireMessage.Json(MessageType.Error, new SessionError("encoder thử bị từ chối")), timeout.Token);
            var error = await phoneError.Wire.ReceiveAsync(timeout.Token);
            Assert(error!.ReadJson<SessionError>().Message.Contains("encoder thử bị từ chối"), "PC làm mất lỗi điện thoại.");
            await retainedPhoneError.Task.WaitAsync(timeout.Token);
        }
        await receiver.StopAsync();
        Assert(connectionStates.SequenceEqual(new[] { true, false, true, false, true, false }), "Trạng thái kết nối không khớp hiển thị QR.");
        Console.WriteLine("ĐẠT: Bộ nhận thật qua TCP/TLS — mã sai, mã đúng, cấu hình, khung khóa, giải mã, heartbeat và dừng.");
        Console.WriteLine("ĐẠT: Lỗi PC gửi về điện thoại; lỗi encoder được giữ khi PC quay lại chờ kết nối.");
    }

    private static void RenderWindow()
    {
        var app = new ShareManHinhDT.Windows.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow { ShowActivated = false, Left = -20000, Top = -20000 };
        window.Show();
        ((System.Windows.Controls.Button)window.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        PumpDispatcher(window);
        Assert(((System.Windows.Controls.StackPanel)window.FindName("QrPanel")).Visibility == Visibility.Visible, "Không hiển thị QR khi bắt đầu nhận.");
        var qrImage = (BitmapSource)((System.Windows.Controls.Image)window.FindName("PairingQrImage")).Source;
        byte[] qrPixels = new byte[qrImage.PixelWidth * qrImage.PixelHeight];
        qrImage.CopyPixels(qrPixels, qrImage.PixelWidth, 0);
        var qr = PairingQr.Parse(PairingQr.DecodeGrayscale(qrPixels, qrImage.PixelWidth, qrImage.PixelHeight)!);
        Assert(qr.Port == PairingQr.Port && qr.Code.Length == 6, "QR giao diện không giải mã được.");
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)window.Content);
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "qa");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, "windows-main.png"));
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        png.Save(output);
        TestFullScreen(window, directory);
        window.Close();
        PumpDispatcher(window);
        Console.WriteLine("ĐẠT: Render giao diện WPF ra artifacts/qa/windows-main.png.");
    }

    private static void TestFullScreen(MainWindow window, string directory)
    {
        window.Hide();
        var button = (System.Windows.Controls.Button)window.FindName("FullScreenButton");
        var root = (System.Windows.Controls.Grid)window.FindName("RootLayout");
        var screen = (System.Windows.Controls.Border)window.FindName("ScreenPanel");
        var image = (System.Windows.Controls.Image)window.FindName("ScreenImage");
        foreach (var state in new[] { WindowState.Normal, WindowState.Maximized })
        {
            window.WindowState = state;
            var before = window.RestoreBounds;
            bool topmost = window.Topmost;
            var style = window.WindowStyle;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpDispatcher(window);
            foreach (string name in new[] { "HeaderPanel", "PairingPanel", "StatusPanel", "PlaceholderText" })
                Assert(((UIElement)window.FindName(name)).Visibility == Visibility.Collapsed, $"Toàn màn hình chưa ẩn {name}.");
            Assert(root.Margin == new Thickness(0) && screen.CornerRadius == new CornerRadius(0), "Vùng xem còn khung ngoài.");
            Assert(System.Windows.Controls.Grid.GetRow(screen) == 0 && System.Windows.Controls.Grid.GetRowSpan(screen) == 4,
                "Vùng xem không chiếm toàn bộ bố cục.");
            Assert(window.WindowStyle == WindowStyle.None && image.Stretch == Stretch.Uniform, "Toàn màn hình sai kiểu cửa sổ/tỷ lệ ảnh.");
            // Render trong cua so ngoai man hinh de khong che man hinh nguoi dung.
            window.Content = null;
            var renderHost = new Window { Content = root, Width = 1280, Height = 720,
                WindowStyle = WindowStyle.None, ShowActivated = false, Left = -20000, Top = -20000 };
            renderHost.Show();
            PumpDispatcher(renderHost);
            foreach (var size in new[] { new Size(720, 1280), new Size(1280, 720) })
            {
                var pixels = new byte[(int)(size.Width * size.Height * 4)];
                for (int y = 0; y < (int)size.Height; y++)
                    for (int x = 0; x < (int)size.Width; x++)
                    {
                        int offset = (y * (int)size.Width + x) * 4;
                        pixels[offset] = (byte)(255 * x / size.Width);
                        pixels[offset + 1] = (byte)(255 * y / size.Height);
                        pixels[offset + 2] = 96;
                    }
                image.Source = BitmapSource.Create((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Bgr32,
                    null, pixels, (int)size.Width * 4);
                root.Measure(new Size(1280, 720));
                root.Arrange(new Rect(0, 0, 1280, 720));
                root.UpdateLayout();
                var preview = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
                preview.Render(root);
                byte[] rendered = new byte[1280 * 720 * 4];
                preview.CopyPixels(rendered, 1280 * 4, 0);
                Assert(rendered[(360 * 1280 + 640) * 4 + 2] == 96, "Hình điện thoại không hiển thị ở giữa.");
                if (size.Height > size.Width)
                    Assert(rendered[(360 * 1280 + 10) * 4] == 0 && rendered[(360 * 1280 + 10) * 4 + 2] == 0,
                        "Phần dư không có nền đen.");
            }
            var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            using (var file = File.Create(Path.Combine(directory, "windows-fullscreen.png")))
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                png.Save(file);
            }
            renderHost.Content = null;
            renderHost.Close();
            window.Content = root;
            image.Source = null;
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, 0, state == WindowState.Normal ? System.Windows.Input.Key.Escape : System.Windows.Input.Key.F11)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            PumpDispatcher(window);
            Assert(window.WindowState == state && window.WindowStyle == style && window.Topmost == topmost,
                "Không khôi phục trạng thái cửa sổ.");
            Assert(Math.Abs(window.RestoreBounds.Width - before.Width) < 1 && Math.Abs(window.RestoreBounds.Left - before.Left) < 1,
                "Không khôi phục kích thước/vị trí cửa sổ.");
            Assert(root.Margin == new Thickness(20) && System.Windows.Controls.Grid.GetRow(screen) == 2,
                "Không khôi phục bố cục.");
        }
        Console.WriteLine("ĐẠT: Toàn màn hình ẩn khung ngoài, giữ tỷ lệ dọc/ngang, Esc khôi phục cửa sổ thường/phóng to.");
    }

    private static void PumpDispatcher(Window window)
    {
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ContextIdle);
        Dispatcher.PushFrame(frame);
    }

    private static void TestQrPresentation()
    {
        var now = DateTimeOffset.UtcNow;
        var view = new PairingPresentation { Address = "192.168.1.20", Pairing = new("123456", now.AddMinutes(5), new string('A', 64)) };
        Assert(view.GetPayload(now) is null, "QR xuất hiện trước khi bắt đầu nhận.");
        view.Listening = true;
        var first = view.GetPayload(now)!;
        view.Address = "192.168.1.21";
        Assert(view.GetPayload(now)!.Address != first.Address, "Đổi IP không cập nhật QR.");
        view.Pairing = view.Pairing! with { Code = "654321" };
        Assert(view.GetPayload(now)!.Code != first.Code, "Đổi mã không cập nhật QR.");
        view.Connected = true;
        Assert(view.GetPayload(now) is null, "Không ẩn QR khi đang kết nối.");
        view.Connected = false;
        Assert(view.GetPayload(now) is not null, "Không phục hồi QR sau phiên.");
        Assert(view.GetPayload(now.AddMinutes(6)) is null, "Không ẩn QR hết hạn.");
        view.Listening = false;
        Assert(view.GetPayload(now) is null, "Không ẩn QR khi dừng.");
        Console.WriteLine("ĐẠT: Hiển thị QR theo IP, mã, hạn sử dụng và trạng thái bộ nhận.");
    }

    private static unsafe Fixture CreateFixture(int width, int height, int numberOfFrames)
    {
        PInvoke.MFStartup(0x20070, 0).ThrowOnFailure();
        var encoder = VideoDecoder.CreateTransform("6CA50344-051A-4DED-9779-A43305165E35");
        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;
        try
        {
            outputType = VideoDecoder.CreateType(PInvoke.MFVideoFormat_H264, width, height, 30);
            Guid bitrateKey = PInvoke.MF_MT_AVG_BITRATE;
            outputType.SetUINT32(&bitrateKey, 2_500_000);
            Guid profileKey = PInvoke.MF_MT_MPEG2_PROFILE;
            outputType.SetUINT32(&profileKey, 66);
            encoder.SetOutputType(0, outputType, 0);
            inputType = VideoDecoder.CreateType(PInvoke.MFVideoFormat_NV12, width, height, 30);
            encoder.SetInputType(0, inputType, 0);
            VideoDecoder.Begin(encoder);
            var frames = new List<EncodedFrame>();
            void Drain()
            {
                while (VideoDecoder.ReadOutput(encoder) is { } sample)
                {
                    sample.ConvertToContiguousBuffer(out var buffer);
                    try
                    {
                        byte* pointer;
                        uint length;
                        buffer.Lock(&pointer, null, &length);
                        try
                        {
                            sample.GetSampleTime(out long timestamp);
                            frames.Add(new(H264.ToAnnexB(new ReadOnlySpan<byte>(pointer, (int)length)), timestamp / 10));
                        }
                        finally { buffer.Unlock(); }
                    }
                    finally { VideoDecoder.Release(buffer); VideoDecoder.Release(sample); }
                }
            }
            for (int i = 0; i < numberOfFrames; i++)
            {
                byte[] pixels = new byte[width * height * 3 / 2];
                pixels.AsSpan(0, width * height / 2).Fill((byte)(40 + i % 10));
                pixels.AsSpan(width * height / 2, width * height / 2).Fill((byte)(180 + i % 10));
                pixels.AsSpan(width * height).Fill(128);
                var sample = VideoDecoder.CreateSample(pixels, i * 10_000_000L / 30);
                try { encoder.ProcessInput(0, sample, 0); }
                finally { VideoDecoder.Release(sample); }
                Drain();
            }
            encoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, 0);
            Drain();
            encoder.GetOutputCurrentType(0, out var finalType);
            byte[] codec;
            try
            {
                Guid key = PInvoke.MF_MT_MPEG_SEQUENCE_HEADER;
                finalType.GetBlobSize(&key, out var size);
                codec = new byte[size];
                finalType.GetBlob(&key, codec, size, null);
            }
            finally { VideoDecoder.Release(finalType); }
            Assert(frames.Count > 0, "Không mã hóa được khung thử.");
            if (!frames[0].Data.AsSpan().StartsWith(codec))
                frames[0] = frames[0] with { Data = codec.Concat(frames[0].Data).ToArray() };
            return new(new VideoConfiguration(width, height, 30, 2_500_000, codec), frames);
        }
        finally
        {
            VideoDecoder.Release(inputType);
            VideoDecoder.Release(outputType);
            VideoDecoder.Release(encoder);
            PInvoke.MFShutdown().ThrowOnFailure();
        }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record EncodedFrame(byte[] Data, long Timestamp);
    private sealed record Fixture(VideoConfiguration Configuration, List<EncodedFrame> Frames);
}

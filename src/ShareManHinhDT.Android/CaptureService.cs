using System.Collections.Concurrent;
using Android.Content.PM;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.Util;
using Android.Views;
using ShareManHinhDT.Shared;

namespace ShareManHinhDT.Android;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public sealed class CaptureService : Service
{
    private const string ChannelId = "screen-sharing";
    private const string StopAction = "vn.sharemanhinhdt.STOP";
    private MediaProjection? projection;
    private ProjectionCallback? projectionCallback;
    private VirtualDisplay? display;
    private MediaCodec? encoder;
    private Surface? surface;
    private CancellationTokenSource? cancellation;
    private Task? captureTask;
    private SenderSession? session;
    private ScreenLockReceiver? screenLockReceiver;
    private readonly ConcurrentQueue<(int Width, int Height)> sizeChanges = new();
    private int requestedKeyFrame;
    private bool stopping;
    private int density;
    private int bitRate = 2_500_000;
    private string stage = "Khởi động";
    private Handler? projectionHandler;
    private bool receiverRegistered;
    private Task? reportTask;

    private void Trace(string message)
    {
        session?.Diagnostics.Trace(message);
        Log.Info("ShareManHinhDT.Capture", message);
    }

    private void Step(string value) { stage = value; Trace(value); }

    private void FailCapture(Exception ex)
    {
        string detail = $"Bước: {stage}\n{DescribeError(ex)}";
        Log.Error("ShareManHinhDT.Capture", detail);
        if (session is null) return;
        string reason = $"Không thể chia sẻ ({stage}): {ex.Message}";
        if (!session.Diagnostics.End(reason, true, detail)) return;
        AppSession.PublishFailure(session);
        var failedSession = session;
        reportTask = Task.Run(async () =>
        {
            try
            {
                string message = reason[..Math.Min(reason.Length, 1000)];
                await failedSession.SendAsync(WireMessage.Json(MessageType.Error, new SessionError(message)));
            }
            catch (Exception reportError) { Log.Warn("ShareManHinhDT.Capture", reportError.ToString()); }
        });
    }

    private static string DescribeError(Exception error)
    {
        string report = error.ToString();
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is MediaCodec.CodecException codecError)
                report += $"\nCodec diagnostic: {codecError.DiagnosticInfo}";
        return report;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            session?.Diagnostics.End("Đã dừng từ thông báo chia sẻ.");
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (captureTask is not null || AppSession.Current is null || intent is null)
        {
            if (captureTask is null) StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (intent.GetStringExtra("connectionId") != AppSession.Current.ConnectionId)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        session = AppSession.Current;
        try
        {
            Step("Dịch vụ foreground");
            Trace($"Android {Build.VERSION.Release}, API {(int)Build.VERSION.SdkInt}; {Build.Manufacturer} {Build.Model}");
            var manager = AndroidServices.Require<NotificationManager>(this, NotificationService);
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Chia sẻ màn hình", NotificationImportance.Low));
            var stopIntent = new Intent(this, typeof(CaptureService)).SetAction(StopAction);
            var stopAction = PendingIntent.GetService(this, 0, stopIntent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var openIntent = PendingIntent.GetActivity(this, 1, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var notification = new Notification.Builder(this, ChannelId)
                .SetContentTitle("Đang chia sẻ màn hình với PC")!
                .SetContentText("Chạm Dừng để kết thúc phiên chia sẻ")!
                .SetSmallIcon(global::Android.Resource.Drawable.IcMenuView)!
                .SetContentIntent(openIntent)!
                .SetOngoing(true)!
                .AddAction(new Notification.Action.Builder(global::Android.Graphics.Drawables.Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMediaPause), "Dừng", stopAction).Build())!
                .Build();
            StartForeground(1, notification, ForegroundService.TypeMediaProjection);
            Step("MediaProjection");
            var permissionData = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? (Intent?)intent.GetParcelableExtra("permissionData", Java.Lang.Class.FromType(typeof(Intent)))
                : (Intent?)intent.GetParcelableExtra("permissionData");
            if (permissionData is null) throw new InvalidOperationException("Thiếu quyền chia sẻ màn hình.");
            var projectionManager = AndroidServices.Require<MediaProjectionManager>(this, MediaProjectionService);
            projection = projectionManager.GetMediaProjection(intent.GetIntExtra("resultCode", 0), permissionData);
            if (projection is null) throw new InvalidOperationException("Không tạo được phiên chia sẻ.");
            projectionCallback = new ProjectionCallback(this);
            projectionHandler = new Handler(Looper.MainLooper!);
            projection.RegisterCallback(projectionCallback, projectionHandler);
            session.KeyFrameRequested += RequestKeyFrame;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            Step("Kích thước màn hình");
            var physicalSize = GetDisplaySize();
            Trace($"Màn hình {physicalSize.Width} × {physicalSize.Height}");
            density = (int)Resources!.DisplayMetrics!.DensityDpi;
            Step("Cấu hình encoder");
            var size = ConfigureEncoder(physicalSize.Width, physicalSize.Height);
            Step("VirtualDisplay");
            display = projection.CreateVirtualDisplay("ShareManHinhDT", size.Width, size.Height, density,
                (DisplayFlags)VirtualDisplayFlags.AutoMirror, surface, null, null);
            if (display is null) throw new InvalidOperationException("Không tạo được VirtualDisplay.");
            screenLockReceiver = new ScreenLockReceiver(this);
            RegisterReceiver(screenLockReceiver, new IntentFilter(Intent.ActionScreenOff));
            receiverRegistered = true;
            AppSession.Capturing = true;
            AppSession.Update("Đang chia sẻ màn hình. Có thể chuyển sang ứng dụng khác.");
            captureTask = Task.Run(() => CaptureLoopAsync(size.Width, size.Height, physicalSize.Width, physicalSize.Height, cancellation.Token));
        }
        catch (Exception ex)
        {
            FailCapture(ex);
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    private (int Width, int Height) ConfigureEncoder(int sourceWidth, int sourceHeight)
    {
        using var list = new MediaCodecList(MediaCodecListKind.RegularCodecs);
        Exception? lastError = null;
        foreach (var codec in list.GetCodecInfos() ?? [])
        {
            using (codec)
            {
                if (!codec.IsEncoder || !codec.GetSupportedTypes().Contains("video/avc")) continue;
                try
                {
                    using var caps = codec.GetCapabilitiesForType("video/avc");
                    if (caps?.ColorFormats?.Contains((int)MediaCodecCapabilities.Formatsurface) != true) continue;
                    using var video = caps.VideoCapabilities;
                    if (video is null) continue;
                    var size = EncoderSelection.FindSize(sourceWidth, sourceHeight, video.WidthAlignment,
                        video.HeightAlignment, (w, h) => video.AreSizeAndRateSupported(w, h, 30));
                    if (size is null) { Trace($"Codec {codec.Name}: không có kích thước 30 fps phù hợp."); continue; }
                    bool baseline = caps.ProfileLevels?.Any(p => p.Profile == MediaCodecProfileType.Avcprofilebaseline) == true;
                    Trace($"Codec {codec.Name}; {size.Value.Width} × {size.Value.Height}; alignment {video.WidthAlignment}/{video.HeightAlignment}; Baseline={baseline}");
                    using var rates = video.BitrateRange ?? throw new NotSupportedException("Codec không cung cấp giới hạn bitrate.");
                    using var minimum = rates.Lower as Java.Lang.Integer;
                    using var maximum = rates.Upper as Java.Lang.Integer;
                    bitRate = Math.Clamp(2_500_000, minimum!.IntValue(), maximum!.IntValue());
                    encoder = EncoderSelection.ConfigureWithFpsFallback(optionalFps =>
                    {
                        MediaCodec? candidate = null;
                        try
                        {
                            candidate = MediaCodec.CreateByCodecName(codec.Name)!;
                            using var format = MediaFormat.CreateVideoFormat("video/avc", size.Value.Width, size.Value.Height);
                            format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
                            format.SetInteger(MediaFormat.KeyBitRate, bitRate);
                            format.SetInteger(MediaFormat.KeyFrameRate, 30);
                            format.SetInteger(MediaFormat.KeyIFrameInterval, 1);
                            if (baseline) format.SetInteger(MediaFormat.KeyProfile, (int)MediaCodecProfileType.Avcprofilebaseline);
                            if (optionalFps) format.SetFloat(MediaFormat.KeyMaxFpsToEncoder, 30f);
                            candidate.Configure(format, null, null, MediaCodecConfigFlags.Encode);
                            return candidate;
                        }
                        catch
                        {
                            SafeRelease(candidate, () => candidate?.Release());
                            throw;
                        }
                    }, ex => Trace($"Thử lại codec không có khóa FPS tùy chọn: {DescribeError(ex)}"));
                    surface = encoder.CreateInputSurface();
                    encoder.Start();
                    Trace($"Encoder đã chạy; bitrate {bitRate}, 30 fps.");
                    return size.Value;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Trace($"Codec {codec.Name} thất bại: {DescribeError(ex)}");
                    ReleaseEncoder();
                }
            }
        }
        throw new NotSupportedException("Không có encoder H.264/Surface hỗ trợ kích thước màn hình ở 30 fps.", lastError);
    }

    private async Task CaptureLoopAsync(int width, int height, int sourceWidth, int sourceHeight, CancellationToken token)
    {
        using var info = new MediaCodec.BufferInfo();
        byte[]? codecData = null;
        bool configured = false;
        bool sentFirstFrame = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                (int Width, int Height)? changed = null;
                while (sizeChanges.TryDequeue(out var next)) changed = next;
                if (changed is { } size && (size.Width != sourceWidth || size.Height != sourceHeight))
                {
                    Step("Đổi kích thước encoder");
                    display!.Surface = null;
                    ReleaseEncoder();
                    sourceWidth = size.Width;
                    sourceHeight = size.Height;
                    var selected = ConfigureEncoder(sourceWidth, sourceHeight);
                    width = selected.Width;
                    height = selected.Height;
                    display.Resize(width, height, density);
                    display.Surface = surface;
                    codecData = null;
                    configured = false;
                }
                if (Interlocked.Exchange(ref requestedKeyFrame, 0) != 0)
                {
                    using var parameters = new Bundle();
                    parameters.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0);
                    encoder!.SetParameters(parameters);
                }
                stage = "Đọc encoder";
                int index = encoder!.DequeueOutputBuffer(info, 10_000);
                if (index == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    using var output = encoder.OutputFormat;
                    codecData = ReadCodecData(output);
                    Step("Gửi cấu hình video");
                    await session!.SendAsync(WireMessage.Json(MessageType.VideoConfiguration,
                        new VideoConfiguration(width, height, 30, bitRate, codecData)));
                    Trace("Đã gửi cấu hình video.");
                    configured = true;
                    RequestKeyFrame();
                }
                else if (index >= 0)
                {
                    WireMessage? packet = null;
                    try
                    {
                        if (info.Size > 0)
                        {
                            using var buffer = encoder.GetOutputBuffer(index)!;
                            buffer.Position(info.Offset);
                            byte[] encoded = new byte[info.Size];
                            buffer.Get(encoded);
                            encoded = H264.ToAnnexB(encoded);
                            if ((info.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                            {
                                codecData = encoded;
                                Step("Gửi cấu hình video");
                                await session!.SendAsync(WireMessage.Json(MessageType.VideoConfiguration,
                                    new VideoConfiguration(width, height, 30, bitRate, codecData)));
                                Trace("Đã gửi cấu hình video từ codec config.");
                                configured = true;
                                RequestKeyFrame();
                            }
                            else if (configured)
                            {
                                bool key = (info.Flags & MediaCodecBufferFlags.KeyFrame) != 0;
                                if (key && codecData is not null)
                                {
                                    byte[] combined = new byte[codecData.Length + encoded.Length];
                                    codecData.CopyTo(combined, 0);
                                    encoded.CopyTo(combined, codecData.Length);
                                    encoded = combined;
                                }
                                packet = new(MessageType.VideoFrame, encoded, Math.Max(0, info.PresentationTimeUs), key ? WireMessage.KeyFrameFlag : 0);
                            }
                        }
                    }
                    finally { encoder.ReleaseOutputBuffer(index, false); }
                    if (packet is not null)
                    {
                        stage = "Gửi khung video";
                        await session!.SendAsync(packet);
                        if (!sentFirstFrame) { sentFirstFrame = true; Trace("Đã gửi khung video đầu tiên."); }
                    }
                    if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
                        throw new IOException("Encoder kết thúc luồng video ngoài yêu cầu dừng.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (token.IsCancellationRequested) { Trace($"Dừng luồng encoder: {ex.GetType().Name}"); }
        catch (Exception ex) { FailCapture(ex); }
        finally
        {
            ReleaseEncoder();
            projectionHandler?.Post(StopSelf);
        }
    }

    private static byte[] ReadCodecData(MediaFormat format)
    {
        using var combined = new MemoryStream();
        for (int i = 0; i < 2; i++)
        {
            using var buffer = format.GetByteBuffer($"csd-{i}");
            if (buffer is null) continue;
            byte[] bytes = new byte[buffer.Remaining()];
            buffer.Get(bytes);
            combined.Write(H264.ToAnnexB(bytes));
        }
        byte[] data = combined.ToArray();
        if (!H264.IsAnnexB(data)) throw new InvalidDataException("Bộ mã hóa không cung cấp SPS/PPS H.264.");
        return data;
    }

    private void RequestKeyFrame() => Interlocked.Exchange(ref requestedKeyFrame, 1);

    private (int Width, int Height) GetDisplaySize()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var displayManager = AndroidServices.Require<DisplayManager>(this, DisplayService);
            using var defaultDisplay = displayManager.GetDisplay(Display.DefaultDisplay)
                ?? throw new InvalidOperationException("Không tìm thấy màn hình chính của điện thoại.");
            using var displayContext = CreateDisplayContext(defaultDisplay)
                ?? throw new InvalidOperationException("Không tạo được context màn hình.");
            using var windowContext = displayContext.CreateWindowContext((int)WindowManagerTypes.ApplicationOverlay, null)
                ?? throw new InvalidOperationException("Không tạo được context cửa sổ màn hình.");
            var windowManager = AndroidServices.Require<IWindowManager>(windowContext, WindowService);
            using var metrics = windowManager.MaximumWindowMetrics
                ?? throw new InvalidOperationException("Không đọc được MaximumWindowMetrics.");
            using var bounds = metrics.Bounds
                ?? throw new InvalidOperationException("Không đọc được bounds của màn hình.");
            return ValidateDisplaySize(bounds.Width(), bounds.Height());
        }
        using var legacyMetrics = new DisplayMetrics();
        var legacyWindowManager = AndroidServices.Require<IWindowManager>(this, WindowService);
        using var legacyDisplay = legacyWindowManager.DefaultDisplay
            ?? throw new InvalidOperationException("Không tìm thấy màn hình Android 10.");
        legacyDisplay.GetRealMetrics(legacyMetrics);
        return ValidateDisplaySize(legacyMetrics.WidthPixels, legacyMetrics.HeightPixels);
    }

    private static (int Width, int Height) ValidateDisplaySize(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new InvalidOperationException($"Kích thước màn hình không hợp lệ: {width} × {height}.");
        return (width, height);
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration? newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (OperatingSystem.IsAndroidVersionAtLeast(34) || stopping || session is null) return;
        try
        {
            stage = "Kích thước màn hình sau đổi cấu hình";
            sizeChanges.Enqueue(GetDisplaySize());
        }
        catch (Exception ex) { FailCapture(ex); StopSelf(); }
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    private void ReleaseEncoder()
    {
        SafeRelease(encoder, () => { Cleanup(() => encoder?.Stop()); Cleanup(() => encoder?.Release()); });
        encoder = null;
        SafeRelease(surface, () => surface?.Release());
        surface = null;
    }

    private void Cleanup(Action action)
    {
        try { action(); }
        catch (Exception ex) { Trace($"Lỗi dọn dẹp: {ex}"); }
    }

    private void SafeRelease(IDisposable? resource, Action release)
    {
        Cleanup(release);
        Cleanup(() => resource?.Dispose());
    }

    public override async void OnDestroy()
    {
        if (stopping) return;
        stopping = true;
        session?.Diagnostics.End("Đã dừng chia sẻ. Kết nối lại để bắt đầu phiên mới.");
        Trace("Dọn dẹp phiên chia sẻ.");
        try
        {
            Cleanup(() => cancellation?.Cancel());
            if (session is not null) session.KeyFrameRequested -= RequestKeyFrame;
            if (receiverRegistered && screenLockReceiver is not null) Cleanup(() => UnregisterReceiver(screenLockReceiver));
            if (projectionCallback is not null) Cleanup(() => projection?.UnregisterCallback(projectionCallback));
            Cleanup(() => projection?.Stop());
            if (captureTask is not null)
            {
                try { await captureTask; } catch (Exception ex) { Trace($"Lỗi kết thúc tác vụ: {ex}"); }
            }
            else ReleaseEncoder();
            SafeRelease(display, () => display?.Release());
            display = null;
            SafeRelease(projection, () => { });
            SafeRelease(projectionCallback, () => { });
            SafeRelease(screenLockReceiver, () => { });
            SafeRelease(projectionHandler, () => { });
            Cleanup(() => cancellation?.Dispose());
            Cleanup(() => StopForeground(StopForegroundFlags.Remove));
            if (reportTask is not null) await reportTask;
            // Dich vu khong so huu phien thi khong duoc ngat ket noi hien tai.
            if (session is not null)
                await AppSession.DisconnectAsync("Đã dừng chia sẻ. Kết nối lại để bắt đầu phiên mới.", session);
        }
        finally { base.OnDestroy(); }
    }

    private sealed class ProjectionCallback(CaptureService owner) : MediaProjection.Callback
    {
        public override void OnStop()
        {
            owner.Trace("MediaProjection.OnStop");
            owner.session?.Diagnostics.End("Android đã kết thúc quyền chia sẻ màn hình. Kết nối lại để bắt đầu phiên mới.");
            owner.StopSelf();
        }
        public override void OnCapturedContentResize(int width, int height) => owner.sizeChanges.Enqueue((width, height));
        public override void OnCapturedContentVisibilityChanged(bool isVisible) => owner.Trace($"Nội dung được chia sẻ hiển thị: {isVisible}");
    }

    private sealed class ScreenLockReceiver(CaptureService owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == Intent.ActionScreenOff)
            {
                owner.session?.Diagnostics.End("Đã dừng chia sẻ vì điện thoại khóa màn hình.");
                owner.StopSelf();
            }
        }
    }
}

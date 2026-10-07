using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.Views;
using Java.Util.Concurrent;
using ShareManHinhDT.Shared;

namespace ShareManHinhDT.Android;

internal sealed class CameraQrScanner : IDisposable
{
    private readonly Context context;
    private readonly SurfaceTexture texture;
    private readonly Action<string> failed;
    private readonly Action<int, int, int> previewChanged;
    private readonly HandlerThread thread = new("ShareManHinhDT.QR");
    private readonly Handler handler;
    private readonly HandlerExecutor executor;
    private readonly DeviceCallback deviceCallback;
    private readonly SessionCallback sessionCallback;
    private readonly FrameListener frameListener;
    private CameraDevice? camera;
    private CameraCaptureSession? session;
    private ImageReader? imageReader;
    private Surface? previewSurface;
    private CaptureRequest.Builder? request;
    private readonly QrDecodeWorker decodeWorker;
    private byte[] row = [];
    private bool opening;
    private bool released;
    private int stopped;
    private long lastFrame;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Closed => closed.Task;

    public CameraQrScanner(Context context, SurfaceTexture texture, Action<string> decoded, Action<string> failed,
        Action<int, int, int> previewChanged)
    {
        this.context = context;
        this.texture = texture;
        this.failed = failed;
        this.previewChanged = previewChanged;
        thread.Start();
        handler = new Handler(thread.Looper!);
        decodeWorker = new QrDecodeWorker(PairingQr.DecodeGrayscale, decoded, ex =>
        {
            global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString());
            handler.Post(() => Fail("Không giải mã được ảnh camera. Hãy mở lại màn hình quét hoặc nhập thủ công."));
        });
        executor = new HandlerExecutor(handler);
        deviceCallback = new DeviceCallback(this);
        sessionCallback = new SessionCallback(this);
        frameListener = new FrameListener(this);
    }

    public void Start() => handler.Post(OpenCamera);

    private void OpenCamera()
    {
        if (Volatile.Read(ref stopped) != 0) return;
        try
        {
            if (context.CheckSelfPermission(global::Android.Manifest.Permission.Camera) != global::Android.Content.PM.Permission.Granted)
                throw new InvalidOperationException("Chưa được cấp quyền camera. Bạn vẫn có thể nhập IP và mã thủ công.");
            var manager = AndroidServices.Require<CameraManager>(context, Context.CameraService);
            string? selectedId = null;
            CameraCharacteristics? selected = null;
            foreach (string id in manager.GetCameraIdList())
            {
                var characteristics = manager.GetCameraCharacteristics(id);
                using var facing = characteristics.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer;
                if (facing?.IntValue() == (int)LensFacing.Back)
                {
                    selectedId = id;
                    selected = characteristics;
                    break;
                }
                characteristics.Dispose();
            }
            if (selectedId is null || selected is null) throw new InvalidOperationException("Không tìm thấy camera sau. Hãy nhập IP và mã thủ công.");
            using (selected)
            using (var map = selected.Get(CameraCharacteristics.ScalerStreamConfigurationMap) as StreamConfigurationMap)
            {
                var sizes = map?.GetOutputSizes((int)ImageFormatType.Yuv420888);
                var size = sizes?.OrderBy(s => Math.Abs((long)s.Width * s.Height - 640 * 480)).FirstOrDefault()
                    ?? throw new InvalidOperationException("Camera không hỗ trợ ảnh YUV để quét QR.");
                using var textureClass = Java.Lang.Class.FromType(typeof(SurfaceTexture));
                var previews = map!.GetOutputSizes(textureClass);
                var preview = previews?.Where(s => (long)s.Width * s.Height <= 1280 * 720)
                    .OrderBy(s => Math.Abs((double)s.Width / s.Height - (double)size.Width / size.Height))
                    .ThenBy(s => Math.Abs((long)s.Width * s.Height - (long)size.Width * size.Height)).FirstOrDefault()
                    ?? previews?.FirstOrDefault() ?? throw new InvalidOperationException("Camera không hỗ trợ xem trước.");
                texture.SetDefaultBufferSize(preview.Width, preview.Height);
                previewSurface = new Surface(texture);
                imageReader = ImageReader.NewInstance(size.Width, size.Height, ImageFormatType.Yuv420888, 2)!;
                imageReader.SetOnImageAvailableListener(frameListener, handler);
                using var orientation = selected.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer;
                previewChanged(preview.Width, preview.Height, orientation?.IntValue() ?? 90);
                opening = true;
                manager.OpenCamera(selectedId, deviceCallback, handler);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString());
            opening = false;
            Fail($"Không mở được camera: {ex.Message}");
        }
    }

    private void CameraOpened(CameraDevice device)
    {
        opening = false;
        if (Volatile.Read(ref stopped) != 0)
        {
            device.Close();
            device.Dispose();
            Release();
            return;
        }
        camera = device;
        try
        {
            using var previewOutput = new OutputConfiguration(previewSurface!);
            using var imageOutput = new OutputConfiguration(imageReader!.Surface!);
            using var configuration = new SessionConfiguration((int)SessionType.Regular,
                new List<OutputConfiguration> { previewOutput, imageOutput }, executor, sessionCallback);
            device.CreateCaptureSession(configuration);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString());
            Fail($"Không tạo được phiên camera: {ex.Message}");
        }
    }

    private void SessionConfigured(CameraCaptureSession captureSession)
    {
        if (Volatile.Read(ref stopped) != 0) { captureSession.Close(); captureSession.Dispose(); return; }
        session = captureSession;
        try
        {
            request = camera!.CreateCaptureRequest(CameraTemplate.Preview)!;
            request.AddTarget(previewSurface!);
            request.AddTarget(imageReader!.Surface!);
            var manager = AndroidServices.Require<CameraManager>(context, Context.CameraService);
            using var characteristics = manager.GetCameraCharacteristics(camera.Id);
            using var modes = characteristics.Get(CameraCharacteristics.ControlAfAvailableModes) as Java.Lang.Object;
            var supported = modes is null ? [] : (int[]?)modes.ToArray<int>() ?? [];
            if (supported.Contains((int)ControlAFMode.ContinuousPicture))
            {
                using var autofocus = Java.Lang.Integer.ValueOf((int)ControlAFMode.ContinuousPicture)!;
                request.Set(CaptureRequest.ControlAfMode!, autofocus);
            }
            using var captureRequest = request.Build()!;
            captureSession.SetRepeatingRequest(captureRequest, null, handler);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString());
            Fail($"Camera không thể bắt đầu quét: {ex.Message}");
        }
    }

    private void ReadFrame(ImageReader reader)
    {
        Image? image = null;
        byte[]? pixels = null;
        int width = 0, height = 0;
        try
        {
            image = reader.AcquireLatestImage();
            if (image is null || Volatile.Read(ref stopped) != 0 || System.Environment.TickCount64 - lastFrame < 200) return;
            if (decodeWorker.IsBusy) return;
            lastFrame = System.Environment.TickCount64;
            using var crop = image.CropRect!;
            width = crop.Width();
            height = crop.Height();
            using var plane = image.GetPlanes()![0];
            using var buffer = plane.Buffer!;
            int rowBytes = (width - 1) * plane.PixelStride + 1;
            pixels = new byte[checked(width * height)];
            if (row.Length < rowBytes) row = new byte[rowBytes];
            for (int y = 0; y < height; y++)
            {
                buffer.Position((crop.Top + y) * plane.RowStride + crop.Left * plane.PixelStride);
                buffer.Get(row, 0, rowBytes);
                for (int x = 0; x < width; x++) pixels[y * width + x] = row[x * plane.PixelStride];
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString());
            pixels = null;
            if (Volatile.Read(ref stopped) == 0)
                handler.Post(() => Fail("Không lấy được ảnh camera. Hãy mở lại màn hình quét hoặc nhập thủ công."));
        }
        finally
        {
            // Tra bo dem camera truoc khi giai ma, ke ca khi bo qua khung hinh.
            if (image is not null)
            {
                try { image.Close(); }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString());
                    pixels = null;
                    if (Volatile.Read(ref stopped) == 0)
                        handler.Post(() => Fail("Không trả được bộ đệm camera. Hãy mở lại màn hình quét hoặc nhập thủ công."));
                }
                finally { image.Dispose(); }
            }
        }
        if (pixels is not null && Volatile.Read(ref stopped) == 0) decodeWorker.TryDecode(pixels, width, height);
    }

    private void Fail(string message)
    {
        global::Android.Util.Log.Error("ShareManHinhDT.QR", message);
        decodeWorker.Stop();
        if (Interlocked.Exchange(ref stopped, 1) == 0) failed(message);
        Release();
    }

    private void Release()
    {
        if (released) return;
        ReleaseResource(session, () => session?.Close());
        session = null;
        ReleaseResource(camera, () => camera?.Close());
        camera = null;
        ReleaseResource(imageReader, () => imageReader?.Close());
        imageReader = null;
        ReleaseResource(request, () => { });
        request = null;
        ReleaseResource(previewSurface, () => previewSurface?.Release());
        previewSurface = null;
        // Cho callback mo camera hoan tat truoc khi dung HandlerThread.
        if (opening) return;
        released = true;
        thread.QuitSafely();
        _ = Task.Run(() =>
        {
            try
            {
                thread.Join();
                frameListener.Dispose();
                sessionCallback.Dispose();
                deviceCallback.Dispose();
                executor.Dispose();
                handler.Dispose();
                thread.Dispose();
            }
            finally { closed.TrySetResult(); }
        });
    }

    private static void ReleaseResource(IDisposable? resource, Action close)
    {
        try { close(); }
        catch (Exception ex) { global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString()); }
        finally
        {
            try { resource?.Dispose(); }
            catch (Exception ex) { global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString()); }
        }
    }

    public void Dispose()
    {
        decodeWorker.Stop();
        if (Interlocked.Exchange(ref stopped, 1) == 0) handler.Post(Release);
    }

    private sealed class HandlerExecutor(Handler handler) : Java.Lang.Object, IExecutor
    {
        public void Execute(Java.Lang.IRunnable? command) { if (command is not null) handler.Post(command); }
    }
    private sealed class DeviceCallback(CameraQrScanner owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => owner.CameraOpened(camera);
        public override void OnDisconnected(CameraDevice camera)
        {
            owner.opening = false;
            owner.camera ??= camera;
            owner.Fail("Camera đã bị ngắt. Đóng màn hình quét rồi mở lại hoặc nhập thủ công.");
        }
        public override void OnError(CameraDevice camera, CameraError error)
        {
            owner.opening = false;
            owner.camera ??= camera;
            owner.Fail("Camera đang bận hoặc không khả dụng. Hãy thử lại hoặc nhập thủ công.");
        }
    }
    private sealed class SessionCallback(CameraQrScanner owner) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => owner.SessionConfigured(session);
        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            session.Close();
            owner.Fail("Không cấu hình được camera. Hãy thử lại hoặc nhập thủ công.");
        }
    }
    private sealed class FrameListener(CameraQrScanner owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader) { if (reader is not null) owner.ReadFrame(reader); }
    }
}

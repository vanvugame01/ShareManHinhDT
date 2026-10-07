using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Views;
using ShareManHinhDT.Shared;

namespace ShareManHinhDT.Android;

[Activity(Label = "Quét QR ghép đôi", Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class QrScannerActivity : Activity, TextureView.ISurfaceTextureListener
{
    private const int CameraPermissionRequest = 201;
    private TextureView preview = null!;
    private TextView hint = null!;
    private CameraQrScanner? scanner;
    private readonly QrScanGate scanGate = new();
    private bool resumed;
    private bool permissionRequested;
    private bool starting;
    private Task cameraClosed = Task.CompletedTask;
    private (int Width, int Height, int Orientation)? previewFormat;
    private DisplayManager? displayManager;
    private DisplayChangeListener? displayListener;
    private Handler? displayHandler;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(16, 16, 16, 16);
        layout.AddView(new TextView(this) { Text = "Quét QR trên màn hình PC", TextSize = 24 });
        preview = new TextureView(this) { SurfaceTextureListener = this };
        layout.AddView(preview, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        hint = new TextView(this) { Text = "Đưa toàn bộ QR và viền trắng vào khung hình. Ứng dụng sẽ tự kết nối.", TextSize = 16 };
        layout.AddView(hint);
        var cancel = new Button(this) { Text = "Hủy — Nhập thủ công" };
        cancel.Click += (_, _) => Finish();
        layout.AddView(cancel);
        SetContentView(layout);
    }

    protected override void OnResume()
    {
        base.OnResume();
        resumed = true;
        WatchDisplay();
        RefreshPreview();
        if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted)
        {
            if (!permissionRequested)
            {
                permissionRequested = true;
                RequestPermissions([global::Android.Manifest.Permission.Camera], CameraPermissionRequest);
            }
            else hint.Text = "Chưa có quyền camera. Cấp quyền trong Cài đặt hoặc quay lại nhập thủ công.";
        }
        else StartScanner();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != CameraPermissionRequest) return;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted) StartScanner();
        else hint.Text = "Bạn đã từ chối quyền camera. Bấm Hủy để nhập IP và mã ghép đôi thủ công.";
    }

    private async void StartScanner()
    {
        if (!resumed || starting || scanner is not null || !preview.IsAvailable || scanGate.IsCompleted ||
            CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted) return;
        starting = true;
        try
        {
            await cameraClosed.WaitAsync(TimeSpan.FromSeconds(5));
            if (!resumed || !preview.IsAvailable || scanGate.IsCompleted) return;
            CameraQrScanner? current = null;
            current = new CameraQrScanner(this, preview.SurfaceTexture!,
                text => RunOnUiThread(() => { if (ReferenceEquals(scanner, current)) AcceptQr(text); }),
                error => RunOnUiThread(() => { if (resumed && ReferenceEquals(scanner, current)) hint.Text = error; }),
                (width, height, orientation) => RunOnUiThread(() =>
                {
                    if (ReferenceEquals(scanner, current)) ConfigurePreview(width, height, orientation);
                }));
            scanner = current;
            scanner.Start();
        }
        catch (Exception ex) { hint.Text = $"Không mở lại được camera: {ex.Message}"; }
        finally { starting = false; }
    }

    private async void AcceptQr(string text)
    {
        if (!resumed || scanGate.IsCompleted || IsFinishing) return;
        try
        {
            if (!scanGate.TryAccept(text, out _)) return;
            StopScanner();
            hint.Text = "Đã đọc QR. Đang đóng camera để kết nối…";
            await cameraClosed.WaitAsync(TimeSpan.FromSeconds(5));
            if (IsFinishing) return;
            SetResult(Result.Ok, new Intent().PutExtra("pairingQr", text));
            Finish();
        }
        catch (InvalidDataException ex) { hint.Text = ex.Message; }
        catch (Exception ex) { hint.Text = $"Không kết thúc được phiên quét: {ex.Message}. Hãy quay lại nhập thủ công."; }
    }

    private void ConfigurePreview(int bufferWidth, int bufferHeight, int sensorOrientation)
    {
        previewFormat = (bufferWidth, bufferHeight, sensorOrientation);
        if (!resumed || preview.Width == 0 || preview.Height == 0) return;
        try
        {
            var currentDisplay = GetPreviewDisplay()
                ?? throw new InvalidOperationException("Không tìm thấy màn hình cho camera xem trước.");
            int rotationDegrees = (int)currentDisplay.Rotation * 90;
            var transform = CameraPreviewTransform.Create(bufferWidth, bufferHeight, preview.Width, preview.Height,
                sensorOrientation, rotationDegrees);
            using var matrix = new Matrix();
            // Matrix3x2 dung vector hang; Android Matrix dung vector cot.
            matrix.SetValues([transform.M11, transform.M21, transform.M31,
                transform.M12, transform.M22, transform.M32, 0, 0, 1]);
            preview.SetTransform(matrix);
            global::Android.Util.Log.Info("ShareManHinhDT.QR",
                $"Preview buffer {bufferWidth} × {bufferHeight}; view {preview.Width} × {preview.Height}; sensor {sensorOrientation}; display {rotationDegrees}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString());
            hint.Text = $"Không chỉnh được hướng camera: {ex.Message}. Bạn có thể quay lại nhập thủ công.";
            StopScanner();
        }
    }

    private Display? GetPreviewDisplay() => preview.Display ??
        (OperatingSystem.IsAndroidVersionAtLeast(30) ? Display : WindowManager?.DefaultDisplay);

    private void RefreshPreview()
    {
        if (previewFormat is { } format) ConfigurePreview(format.Width, format.Height, format.Orientation);
    }

    private void WatchDisplay()
    {
        if (displayListener is not null) return;
        try
        {
            displayManager = AndroidServices.Require<DisplayManager>(this, DisplayService);
            displayHandler = new Handler(Looper.MainLooper!);
            displayListener = new DisplayChangeListener(this);
            displayManager.RegisterDisplayListener(displayListener, displayHandler);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ShareManHinhDT.QR", ex.ToString());
            UnwatchDisplay();
            hint.Text = $"Không theo dõi được hướng màn hình: {ex.Message}";
        }
    }

    private void UnwatchDisplay()
    {
        try { if (displayListener is not null) displayManager?.UnregisterDisplayListener(displayListener); }
        catch (Exception ex) { global::Android.Util.Log.Warn("ShareManHinhDT.QR", ex.ToString()); }
        finally
        {
            displayListener?.Dispose();
            displayHandler?.Dispose();
            displayListener = null;
            displayHandler = null;
            displayManager = null;
        }
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        RefreshPreview();
    }

    private void StopScanner()
    {
        if (scanner is not null) cameraClosed = scanner.Closed;
        scanner?.Dispose();
        scanner = null;
    }

    protected override void OnPause()
    {
        resumed = false;
        UnwatchDisplay();
        StopScanner();
        base.OnPause();
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) => StartScanner();
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) { StopScanner(); return true; }
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
    {
        RefreshPreview();
    }
    public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

    protected override void OnDestroy()
    {
        resumed = false;
        UnwatchDisplay();
        StopScanner();
        base.OnDestroy();
    }

    private sealed class DisplayChangeListener(QrScannerActivity owner) : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        public void OnDisplayAdded(int displayId) { }
        public void OnDisplayRemoved(int displayId) { }
        public void OnDisplayChanged(int displayId)
        {
            if (owner.resumed && owner.GetPreviewDisplay()?.DisplayId == displayId) owner.RefreshPreview();
        }
    }
}

using Android.Content.PM;
using Android.Media.Projection;
using Android.Views;
using ShareManHinhDT.Shared;

namespace ShareManHinhDT.Android;

[Activity(Label = "ShareManHinhDT", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : Activity
{
    private const int CaptureRequest = 100;
    private const int QrScanRequest = 102;
    private EditText address = null!;
    private EditText code = null!;
    private TextView status = null!;
    private Button connect = null!;
    private Button capture = null!;
    private Button stop = null!;
    private Button scan = null!;
    private Button errorDetails = null!;
    private readonly CapturePermissionGate permissionGate = new();
    private bool scanning;
    private bool connecting;
    private bool notificationPermissionRequested;
    private bool requestingCapture;
    private readonly CancellationTokenSource lifetime = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int padding = (int)(20 * Resources!.DisplayMetrics!.Density);
        content.SetPadding(padding, padding, padding, padding);
        var title = new TextView(this) { Text = "ShareManHinhDT", TextSize = 28 };
        content.AddView(title);
        content.AddView(new TextView(this) { Text = "Xem màn hình Android trên Windows 11\nHai thiết bị cần kết nối trong cùng mạng nội bộ.", TextSize = 16 });
        address = new EditText(this) { Hint = "Địa chỉ IPv4 của PC, ví dụ 192.168.1.10", InputType = global::Android.Text.InputTypes.ClassText };
        using var prefs = GetSharedPreferences("settings", FileCreationMode.Private);
        address.Text = prefs!.GetString("address", "");
        code = new EditText(this) { Hint = "Mã ghép đôi 6 chữ số", InputType = global::Android.Text.InputTypes.ClassNumber | global::Android.Text.InputTypes.NumberVariationPassword };
        content.AddView(address);
        content.AddView(code);
        scan = AddButton(content, "Quét QR để kết nối");
        connect = AddButton(content, "Kết nối với PC");
        capture = AddButton(content, "Bắt đầu chia sẻ màn hình");
        stop = AddButton(content, "Dừng và ngắt kết nối");
        var forget = AddButton(content, "Quên PC đã tin cậy");
        status = new TextView(this) { TextSize = 16 };
        content.AddView(status);
        errorDetails = AddButton(content, "Chi tiết lỗi");
        errorDetails.Click += (_, _) =>
        {
            string report = AppSession.ErrorDetails ?? "Chưa có lỗi phiên chia sẻ.";
            new AlertDialog.Builder(this)!.SetTitle("Chi tiết lỗi chia sẻ màn hình")!
                .SetMessage(report)!
                .SetPositiveButton("Đóng", (_, _) => { })!
                .SetNeutralButton("Sao chép lỗi", (_, _) =>
                {
                    var clipboard = AndroidServices.Require<ClipboardManager>(this, ClipboardService);
                    clipboard.PrimaryClip = ClipData.NewPlainText("Lỗi ShareManHinhDT", report);
                    Toast.MakeText(this, "Đã sao chép chi tiết lỗi.", ToastLength.Short)!.Show();
                })!.Show();
        };
        content.AddView(new TextView(this) { Text = "Chỉ chia sẻ hình ảnh. Nội dung được ứng dụng bảo vệ có thể hiển thị màu đen. Mỗi phiên mới cần xác nhận quyền chia sẻ.", TextSize = 14 });
        var scroll = new ScrollView(this);
        scroll.AddView(content);
        SetContentView(scroll);
        connect.Click += async (_, _) => await ConnectAsync();
        scan.Click += (_, _) =>
        {
            if (connecting || scanning || AppSession.Current is not null) return;
            scanning = true;
            Refresh();
            StartActivityForResult(new Intent(this, typeof(QrScannerActivity)), QrScanRequest);
        };
        capture.Click += (_, _) => RequestCapture();
        stop.Click += async (_, _) =>
        {
            StopService(new Intent(this, typeof(CaptureService)));
            await AppSession.DisconnectAsync("Đã dừng chia sẻ.");
        };
        forget.Click += (_, _) =>
        {
            if (AppSession.Current is not null || connecting) return;
            using var trusted = GetSharedPreferences("trusted-receivers", FileCreationMode.Private);
            using var edit = trusted!.Edit();
            edit!.Remove(address.Text?.Trim());
            edit.Apply();
            AppSession.Update("Đã quên PC. Lần sau cần đối chiếu lại vân tay chứng chỉ.");
        };
        AppSession.Changed += Refresh;
        Refresh();
    }

    private Button AddButton(LinearLayout content, string text)
    {
        var button = new Button(this) { Text = text };
        content.AddView(button);
        return button;
    }

    private async Task ConnectAsync(string? qrFingerprint = null)
    {
        if (connecting || AppSession.Current is not null) return;
        connecting = true;
        AppSession.Update("Đang kết nối với PC…");
        try
        {
            string ip = address.Text?.Trim() ?? "";
            var session = await SenderSession.ConnectAsync(this, ip, code.Text?.Trim() ?? "", ConfirmFingerprintAsync, lifetime.Token, qrFingerprint);
            if (lifetime.IsCancellationRequested) { await session.DisposeAsync(); return; }
            AppSession.Attach(session);
            using var prefs = GetSharedPreferences("settings", FileCreationMode.Private);
            using var edit = prefs!.Edit();
            edit!.PutString("address", ip);
            edit.Apply();
            AppSession.Update("Đã kết nối. Bấm Bắt đầu chia sẻ màn hình.");
            session.Start();
        }
        catch (Exception ex) { AppSession.Update($"Không thể kết nối: {ex.Message}"); }
        finally { connecting = false; Refresh(); }
    }

    private Task<bool> ConfirmFingerprintAsync(string fingerprint)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            var dialog = new AlertDialog.Builder(this)!
                .SetTitle("Xác nhận đúng PC")!
                .SetMessage($"Đối chiếu toàn bộ vân tay dưới đây với ứng dụng Windows. Chỉ xác nhận khi giống nhau:\n\n{Pairing.DisplayFingerprint(fingerprint)}")!
                .SetPositiveButton("Giống nhau — Tin cậy", (_, _) => completion.TrySetResult(true))!
                .SetNegativeButton("Hủy", (_, _) => completion.TrySetResult(false))!.Create();
            dialog!.CancelEvent += (_, _) => completion.TrySetResult(false);
            lifetime.Token.Register(() => { completion.TrySetCanceled(); RunOnUiThread(() => dialog.Dismiss()); });
            dialog.Show();
        });
        return completion.Task;
    }

    private void RequestCapture()
    {
        if (AppSession.Current is null || AppSession.Current.Diagnostics.Outcome is not null || AppSession.Capturing || requestingCapture) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && !notificationPermissionRequested && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            notificationPermissionRequested = true;
            RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 101);
            return;
        }
        requestingCapture = true;
        permissionGate.Begin(AppSession.Current.ConnectionId);
        Refresh();
        try
        {
            var manager = AndroidServices.Require<MediaProjectionManager>(this, MediaProjectionService);
            StartActivityForResult(manager.CreateScreenCaptureIntent(), CaptureRequest);
        }
        catch (Exception ex)
        {
            requestingCapture = false;
            permissionGate.Consume(null);
            var failed = AppSession.Current;
            if (failed is null) return;
            failed.Diagnostics.End($"Không mở được hộp thoại chia sẻ: {ex.Message}", true, ex.ToString());
            AppSession.PublishFailure(failed);
            _ = AppSession.DisconnectAsync("Đã dừng chia sẻ.", failed);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 101) RequestCapture();
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == QrScanRequest)
        {
            scanning = false;
            Refresh();
            if (resultCode != Result.Ok || data?.GetStringExtra("pairingQr") is not { } text) return;
            try
            {
                var pairing = PairingQr.Parse(text);
                address.Text = pairing.Address;
                code.Text = pairing.Code;
                await ConnectAsync(pairing.Fingerprint);
            }
            catch (Exception ex) { AppSession.Update($"Không thể ghép đôi bằng QR: {ex.Message}"); }
            return;
        }
        if (requestCode != CaptureRequest) return;
        requestingCapture = false;
        Refresh();
        var requestedSession = AppSession.Current;
        if (!permissionGate.Consume(requestedSession?.ConnectionId)) return;
        if (resultCode != Result.Ok || data is null || AppSession.Current is null)
        {
            AppSession.Update("Chưa được cấp quyền chia sẻ. Có thể bấm Bắt đầu chia sẻ để thử lại.");
            return;
        }
        var intent = new Intent(this, typeof(CaptureService));
        intent.PutExtra("resultCode", (int)resultCode);
        intent.PutExtra("permissionData", data);
        intent.PutExtra("connectionId", requestedSession!.ConnectionId);
        try { StartForegroundService(intent); }
        catch (Exception ex)
        {
            requestedSession.Diagnostics.Trace("Khởi động dịch vụ foreground");
            requestedSession.Diagnostics.End($"Không khởi động được dịch vụ chia sẻ: {ex.Message}", true, ex.ToString());
            global::Android.Util.Log.Error("ShareManHinhDT.Capture", ex.ToString());
            AppSession.PublishFailure(requestedSession);
            await AppSession.DisconnectAsync("Đã dừng chia sẻ.", requestedSession);
        }
    }

    private void Refresh() => RunOnUiThread(() =>
    {
        status.Text = AppSession.Status;
        connect.Enabled = !connecting && !scanning && AppSession.Current is null;
        scan.Enabled = connect.Enabled;
        capture.Enabled = AppSession.Current is not null && AppSession.Current.Diagnostics.Outcome is null && !AppSession.Capturing && !requestingCapture;
        stop.Enabled = AppSession.Current is not null;
        address.Enabled = AppSession.Current is null && !connecting && !scanning;
        code.Enabled = address.Enabled;
        errorDetails.Visibility = AppSession.ErrorDetails is null ? ViewStates.Gone : ViewStates.Visible;
    });

    protected override void OnDestroy()
    {
        AppSession.Changed -= Refresh;
        lifetime.Cancel();
        if (IsFinishing && !AppSession.Capturing && AppSession.Current is not null)
            _ = AppSession.DisconnectAsync("Đã đóng ứng dụng.");
        base.OnDestroy();
    }
}

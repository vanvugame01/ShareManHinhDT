using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShareManHinhDT.Shared;

namespace ShareManHinhDT.Windows;

public partial class MainWindow : Window
{
    private readonly ReceiverServer receiver;
    private readonly DispatcherTimer displayTimer;
    private readonly Stopwatch metricsClock = Stopwatch.StartNew();
    private readonly PairingPresentation pairingPresentation = new();
    private string? currentQrText;
    private DecodedFrame? latestFrame;
    private WriteableBitmap? bitmap;
    private int displayedFrames;
    private int receivedFrames;
    private bool closing;
    private WindowStyle previousStyle;
    private WindowState previousState;
    private bool isFullScreen;
    private Rect previousBounds;
    private ResizeMode previousResizeMode;
    private bool previousTopmost;

    public MainWindow()
    {
        InitializeComponent();
        receiver = new ReceiverServer();
        receiver.StatusChanged += status => Dispatcher.BeginInvoke(() => StatusText.Text = status);
        receiver.PairingChanged += info => Dispatcher.BeginInvoke(() =>
        {
            pairingPresentation.Pairing = info;
            CodeText.Text = $"Mã ghép đôi: {info.Code} · Hết hạn lúc {info.ExpiresAt.LocalDateTime:HH:mm:ss}";
            FingerprintText.Text = Pairing.DisplayFingerprint(info.Fingerprint);
            ClearScreen();
            UpdatePairingQr();
        });
        receiver.ConnectionChanged += connected => Dispatcher.BeginInvoke(() =>
        {
            pairingPresentation.Connected = connected;
            AddressSelector.IsEnabled = !connected;
            UpdatePairingQr();
        });
        receiver.FrameDecoded += frame =>
        {
            Interlocked.Increment(ref receivedFrames);
            Interlocked.Exchange(ref latestFrame, frame)?.Dispose();
        };
        FingerprintText.Text = Pairing.DisplayFingerprint(receiver.Fingerprint);
        displayTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
        displayTimer.Tick += DisplayTick;
        displayTimer.Start();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11 || (e.Key == Key.Escape && isFullScreen))
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        };
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            closing = true;
            pairingPresentation.Listening = false;
            UpdatePairingQr();
            displayTimer.Stop();
            await receiver.DisposeAsync();
            _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
        };
    }

    private void StartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshAddresses();
            if (AddressSelector.SelectedItem is null) throw new InvalidOperationException("PC chưa có địa chỉ IPv4 để ghép đôi.");
            receiver.Start();
            pairingPresentation.Listening = true;
            pairingPresentation.Connected = false;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
        catch (Exception ex) { StatusText.Text = $"Không thể bắt đầu: {ex.Message}"; }
    }

    private async void StopClick(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        pairingPresentation.Listening = false;
        UpdatePairingQr();
        await receiver.StopAsync();
        ClearScreen();
        CodeText.Text = "Đã dừng nhận";
        StatusText.Text = "Đã dừng. Điện thoại cần xác nhận lại cho phiên chia sẻ mới.";
        StartButton.IsEnabled = true;
    }

    private void ClearScreen()
    {
        Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        ScreenImage.Source = null;
        bitmap = null;
        PlaceholderText.Visibility = isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        displayedFrames = 0;
        Interlocked.Exchange(ref receivedFrames, 0);
        metricsClock.Restart();
        MetricsText.Text = "";
    }

    private void DisplayTick(object? sender, EventArgs e)
    {
        if (QrPanel.Visibility == Visibility.Visible && pairingPresentation.Pairing?.ExpiresAt <= DateTimeOffset.UtcNow)
            UpdatePairingQr();
        var frame = Interlocked.Exchange(ref latestFrame, null);
        if (frame is not null)
        {
            using (frame)
            {
            if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
            {
                bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                ScreenImage.Source = bitmap;
                PlaceholderText.Visibility = Visibility.Collapsed;
            }
            bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
            displayedFrames++;
            }
        }
        if (metricsClock.Elapsed.TotalSeconds >= 2)
        {
            double seconds = metricsClock.Elapsed.TotalSeconds;
            MetricsText.Text = $"Nhận {Interlocked.Exchange(ref receivedFrames, 0) / seconds:F1} fps · Hiển thị {displayedFrames / seconds:F1} fps";
            displayedFrames = 0;
            metricsClock.Restart();
        }
    }

    private void RefreshAddresses()
    {
        string? previousAddress = pairingPresentation.Address;
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && a.Address.GetAddressBytes()[0] is > 0 and < 224)
                .Select(a => new ReceiverAddress(a.Address.ToString(), n.Name,
                    n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                    n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))))
            .OrderByDescending(a => a.Preferred).ThenBy(a => a.AdapterName, StringComparer.Ordinal)
            .DistinctBy(a => a.Address).ToArray();
        AddressSelector.ItemsSource = addresses;
        AddressSelector.SelectedItem = addresses.FirstOrDefault(a => a.Address == previousAddress) ?? addresses.FirstOrDefault();
    }

    private void AddressSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        pairingPresentation.Address = (AddressSelector.SelectedItem as ReceiverAddress)?.Address;
        AddressText.Text = pairingPresentation.Address is { } address ? $"Địa chỉ PC: {address} · Cổng {ReceiverServer.Port}" : "Chưa chọn địa chỉ IPv4 của PC.";
        UpdatePairingQr();
    }

    private void UpdatePairingQr()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = pairingPresentation.GetPayload(now);
        if (payload is null)
        {
            currentQrText = null;
            PairingQrImage.Source = null;
            QrPanel.Visibility = Visibility.Collapsed;
            return;
        }
        string text = PairingQr.Encode(payload, now);
        if (text == currentQrText) return;
        var matrix = PairingQr.CreateMatrix(payload, now);
        const int scale = 4;
        int side = matrix.Width * scale;
        byte[] pixels = new byte[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                pixels[y * side + x] = matrix[x / scale, y / scale] ? (byte)0 : (byte)255;
        var image = BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, pixels, side);
        image.Freeze();
        PairingQrImage.Source = image;
        QrPanel.Visibility = Visibility.Visible;
        currentQrText = text;
    }

    private void FullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (!isFullScreen)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            previousStyle = WindowStyle;
            previousState = WindowState;
            previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            previousResizeMode = ResizeMode;
            previousTopmost = Topmost;
            isFullScreen = true;
            HeaderPanel.Visibility = PairingPanel.Visibility = StatusPanel.Visibility = Visibility.Collapsed;
            RootLayout.Margin = new Thickness(0);
            RootLayout.Background = ScreenPanel.Background = Brushes.Black;
            ScreenPanel.CornerRadius = new CornerRadius(0);
            Grid.SetRow(ScreenPanel, 0);
            Grid.SetRowSpan(ScreenPanel, 4);
            PlaceholderText.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            // Dung toa do vat ly de phu ca thanh tac vu tren man hinh hien tai.
            var bounds = info.Monitor;
            SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top, 0x0004);
            Focus();
        }
        else
        {
            isFullScreen = false;
            WindowState = WindowState.Normal;
            WindowStyle = previousStyle;
            ResizeMode = previousResizeMode;
            Topmost = previousTopmost;
            Left = previousBounds.Left;
            Top = previousBounds.Top;
            Width = previousBounds.Width;
            Height = previousBounds.Height;
            WindowState = previousState;
            HeaderPanel.Visibility = PairingPanel.Visibility = StatusPanel.Visibility = Visibility.Visible;
            RootLayout.Margin = new Thickness(20);
            RootLayout.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x27));
            ScreenPanel.Background = new SolidColorBrush(Color.FromRgb(0x06, 0x0B, 0x12));
            ScreenPanel.CornerRadius = new CornerRadius(10);
            Grid.SetRow(ScreenPanel, 2);
            Grid.SetRowSpan(ScreenPanel, 1);
            PlaceholderText.Visibility = ScreenImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

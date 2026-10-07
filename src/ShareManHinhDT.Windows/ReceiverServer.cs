using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ShareManHinhDT.Shared;

namespace ShareManHinhDT.Windows;

public sealed record PairingInfo(string Code, DateTimeOffset ExpiresAt, string Fingerprint);

public sealed class ReceiverServer : IAsyncDisposable
{
    public const int Port = PairingQr.Port;
    private readonly X509Certificate2 certificate = CertificateIdentity.LoadOrCreate();
    private CancellationTokenSource? cancellation;
    private Task? serverTask;
    private TcpListener? listener;
    private PairingInfo? pairing;
    private int rejectedAttempts;
    private string? lastSessionError;
    public event Action<string>? StatusChanged;
    public event Action<PairingInfo>? PairingChanged;
    public event Action<DecodedFrame>? FrameDecoded;
    public event Action<bool>? ConnectionChanged;
    public string Fingerprint => Pairing.Fingerprint(certificate.RawData);

    public void Start()
    {
        if (serverTask is not null) throw new InvalidOperationException("Bộ nhận đang chạy.");
        listener = new TcpListener(IPAddress.Any, Port);
        listener.Start(1);
        cancellation = new CancellationTokenSource();
        lastSessionError = null;
        RenewPairing();
        serverTask = RunAsync(cancellation.Token);
    }

    private void RenewPairing()
    {
        rejectedAttempts = 0;
        pairing = new(Pairing.CreateCode(), DateTimeOffset.UtcNow.AddMinutes(5), Fingerprint);
        PairingChanged?.Invoke(pairing);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string waiting = "Đang chờ điện thoại. Nếu không kết nối được, kiểm tra mạng và Windows Firewall.";
                StatusChanged?.Invoke(lastSessionError is null ? waiting : $"{waiting}\nLỗi phiên gần nhất: {lastSessionError}");
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromSeconds(5));
                TcpClient client;
                try { client = await listener!.AcceptTcpClientAsync(wait.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (pairing!.ExpiresAt <= DateTimeOffset.UtcNow) RenewPairing();
                    continue;
                }
                using (client)
                {
                    client.NoDelay = true;
                    await ReceiveClientAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                lastSessionError = ex.Message;
                StatusChanged?.Invoke($"Kết nối đã kết thúc: {lastSessionError}");
            }
        }
    }

    private async Task ReceiveClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var ssl = new SslStream(client.GetStream(), false);
        using var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authenticationTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, authenticationTimeout.Token);
        await using var connection = new WireConnection(ssl);
        var hello = await connection.ReceiveAsync(authenticationTimeout.Token);
        if (hello?.Type != MessageType.Authenticate) throw new InvalidDataException("Thiếu yêu cầu ghép đôi.");
        var credentials = hello.ReadJson<Authentication>();
        if (credentials.Code is not { Length: 6 } || credentials.DeviceName is not { Length: > 0 and <= 128 } ||
            pairing!.ExpiresAt <= DateTimeOffset.UtcNow || !Pairing.Matches(pairing.Code, credentials.Code))
        {
            await connection.SendAsync(WireMessage.Json(MessageType.Error, new SessionError("Mã ghép đôi sai hoặc đã hết hạn.")), authenticationTimeout.Token);
            if (++rejectedAttempts >= 5 || pairing!.ExpiresAt <= DateTimeOffset.UtcNow) RenewPairing();
            return;
        }
        ConnectionChanged?.Invoke(true);
        lastSessionError = null;
        try
        {
            await connection.SendAsync(WireMessage.Json(MessageType.Accepted, new SessionAccepted(Guid.NewGuid().ToString("N"))), authenticationTimeout.Token);
            StatusChanged?.Invoke($"Đã ghép đôi: {credentials.DeviceName}. Đang chờ quyền chia sẻ trên điện thoại.");
            await Task.Run(async () =>
            {
                using var decoder = new VideoDecoder();
                bool configured = false;
                bool waitingForKeyFrame = true;
                long lastTimestamp = -1;
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idleTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var message = await connection.ReceiveAsync(idleTimeout.Token);
                    if (message is null || message.Type == MessageType.Stop) break;
                    switch (message.Type)
                    {
                        case MessageType.VideoConfiguration:
                            var format = message.ReadJson<VideoConfiguration>();
                            format.Validate();
                            decoder.Configure(format);
                            configured = true;
                            waitingForKeyFrame = true;
                            lastTimestamp = -1;
                            StatusChanged?.Invoke($"Đang xem {credentials.DeviceName} — {format.Width} × {format.Height}");
                            await connection.SendAsync(new(MessageType.RequestKeyFrame, []), cancellationToken);
                            break;
                        case MessageType.VideoFrame:
                            if (!configured || !H264.IsAnnexB(message.Payload) || message.TimestampUs < lastTimestamp)
                                throw new InvalidDataException("Khung video không hợp lệ hoặc chưa có cấu hình.");
                            if (waitingForKeyFrame && (message.Flags & WireMessage.KeyFrameFlag) == 0) break;
                            waitingForKeyFrame = false;
                            lastTimestamp = message.TimestampUs;
                            foreach (var frame in decoder.Decode(message.Payload, message.TimestampUs))
                            {
                                if (FrameDecoded is { } handler) handler(frame); else frame.Dispose();
                            }
                            break;
                        case MessageType.Ping:
                            await connection.SendAsync(new(MessageType.Pong, []), cancellationToken);
                            break;
                        case MessageType.Error:
                            throw new IOException($"Điện thoại báo lỗi: {message.ReadJson<SessionError>().Message}");
                        default:
                            throw new InvalidDataException("Thông điệp không đúng trạng thái phiên.");
                    }
                }
            }, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Gui loi bo nhan ve dien thoai truoc khi dong TLS.
            using var reportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                string reason = ex.Message[..Math.Min(ex.Message.Length, 1000)];
                await connection.SendAsync(WireMessage.Json(MessageType.Error, new SessionError($"PC: {reason}")), reportTimeout.Token);
            }
            catch (Exception) { }
            throw;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) RenewPairing();
            ConnectionChanged?.Invoke(false);
        }
    }

    public async Task StopAsync()
    {
        cancellation?.Cancel();
        listener?.Stop();
        if (serverTask is not null)
        {
            try { await serverTask; }
            catch (OperationCanceledException) { }
        }
        serverTask = null;
        cancellation?.Dispose();
        cancellation = null;
        listener = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        certificate.Dispose();
    }
}

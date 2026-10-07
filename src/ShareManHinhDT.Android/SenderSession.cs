using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ShareManHinhDT.Shared;

namespace ShareManHinhDT.Android;

public sealed class SenderSession : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly WireConnection connection;
    private readonly CancellationTokenSource cancellation = new();
    private Task? reader;
    private Task? heartbeat;
    private int disposed;
    internal string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    internal CaptureRunState Diagnostics { get; } = new();
    public event Action? KeyFrameRequested;
    public event Action<string>? Ended;
    public CancellationToken Token => cancellation.Token;

    private SenderSession(TcpClient client, SslStream ssl)
    {
        this.client = client;
        connection = new WireConnection(ssl);
    }

    public static async Task<SenderSession> ConnectAsync(Context context, string address, string code,
        Func<string, Task<bool>> confirmFingerprint, CancellationToken cancellationToken, string? qrFingerprint = null)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Nhập địa chỉ IPv4 hiển thị trên PC.");
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) throw new ArgumentException("Mã ghép đôi gồm 6 chữ số.");
        using var preferences = context.GetSharedPreferences("trusted-receivers", FileCreationMode.Private)!;
        string? expected = PairingQr.SelectFingerprint(preferences.GetString(address, null), qrFingerprint);
        if (expected is null)
        {
            string? candidate = null;
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var probeClient = new TcpClient { NoDelay = true };
            await probeClient.ConnectAsync(ip, PairingQr.Port, probeTimeout.Token);
            using var probe = new SslStream(probeClient.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is not null) candidate = Pairing.Fingerprint(certificate.GetRawCertData());
                return false;
            });
            try { await probe.AuthenticateAsClientAsync(TlsOptions(address), probeTimeout.Token); }
            catch (AuthenticationException) when (candidate is not null) { }
            if (candidate is null) throw new AuthenticationException("Không đọc được chứng chỉ PC.");
            if (!await confirmFingerprint(candidate)) throw new OperationCanceledException("Đã hủy xác nhận PC.");
            expected = candidate;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var tcp = new TcpClient { NoDelay = true };
        SenderSession? session = null;
        try
        {
            await tcp.ConnectAsync(ip, PairingQr.Port, timeout.Token);
            string? observed = null;
            var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                observed = Pairing.Fingerprint(certificate.GetRawCertData());
                return Pairing.Matches(expected, observed);
            });
            session = new SenderSession(tcp, ssl);
            try { await ssl.AuthenticateAsClientAsync(TlsOptions(address), timeout.Token); }
            catch (AuthenticationException ex) when (observed is not null && !Pairing.Matches(expected, observed))
            {
                throw new AuthenticationException("Chứng chỉ PC đã thay đổi. Chỉ chọn Quên PC và ghép đôi lại sau khi kiểm tra trực tiếp trên PC.", ex);
            }
            string name = $"{Build.Manufacturer} {Build.Model}";
            await session.connection.SendAsync(WireMessage.Json(MessageType.Authenticate, new Authentication(code, name[..Math.Min(name.Length, 128)])), timeout.Token);
            var response = await session.connection.ReceiveAsync(timeout.Token);
            if (response?.Type == MessageType.Error) throw new AuthenticationException(response.ReadJson<SessionError>().Message);
            if (response?.Type != MessageType.Accepted) throw new AuthenticationException("PC không chấp nhận phiên kết nối.");
            using var edit = preferences.Edit();
            edit!.PutString(address, expected);
            edit.Apply();
            return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync(); else tcp.Dispose();
            throw;
        }
    }

    private static SslClientAuthenticationOptions TlsOptions(string address) => new()
    {
        TargetHost = address,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
    };

    public void Start()
    {
        reader = ReadControlAsync();
        heartbeat = HeartbeatAsync();
    }

    public async Task SendAsync(WireMessage message)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await connection.SendAsync(message, timeout.Token);
    }

    private async Task ReadControlAsync()
    {
        try
        {
            while (!Token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var message = await connection.ReceiveAsync(timeout.Token);
                if (message is null || message.Type == MessageType.Stop) break;
                switch (message.Type)
                {
                    case MessageType.RequestKeyFrame: KeyFrameRequested?.Invoke(); break;
                    case MessageType.Pong: break;
                    case MessageType.Error: throw new IOException(message.ReadJson<SessionError>().Message);
                    default: throw new InvalidDataException("PC gửi thông điệp không hợp lệ.");
                }
            }
            End("PC đã dừng nhận màn hình.");
        }
        catch (Exception ex) when (!Token.IsCancellationRequested) { End($"Mất kết nối: {ex.Message}", ex); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) when (Token.IsCancellationRequested) { }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), Token);
                await SendAsync(new(MessageType.Ping, []));
            }
        }
        catch (Exception ex) when (!Token.IsCancellationRequested) { End($"Kết nối không ổn định: {ex.Message}", ex); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) when (Token.IsCancellationRequested) { }
    }

    private void End(string reason, Exception? error = null)
    {
        if (Token.IsCancellationRequested) return;
        Diagnostics.End(reason, error is not null, error?.ToString());
        cancellation.Cancel();
        client.Dispose();
        Ended?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (!Token.IsCancellationRequested)
        {
            try { await SendAsync(new(MessageType.Stop, [])); } catch (Exception) { }
        }
        cancellation.Cancel();
        client.Dispose();
        if (reader is not null) await reader;
        if (heartbeat is not null) await heartbeat;
        await connection.DisposeAsync();
    }
}

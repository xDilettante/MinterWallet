using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MinterWallet.Api;

namespace MinterWallet.Checks;

// A source's "do not check the TLS certificate" switch: a local HTTPS source with a self-signed certificate is refused
// by default and read when the user turned the check off for it.
internal static class TlsChecks
{
    private const string Status = """{"version":"3.3.0-testnet","network":"minter-devnet","initial_height":"1","latest_block_hash":"38F6","latest_app_hash":"AF64","latest_block_height":"10","latest_block_time":"2026-10-27T00:03:57.268023243+03:00","keep_last_states":"120"}""";

    public static async Task Run()
    {
        using var certificate = SelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var serving = Serve(listener, certificate, stop.Token);
        try
        {
            var url = new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");

            using (var strict = new NodeApiClient(url))
            {
                Exception? refused = null;
                try { await strict.StatusAsync(); }
                catch (HttpRequestException error) { refused = error; }
                Assert.True(refused?.InnerException is AuthenticationException, $"self-signed certificate refused by default ({refused?.InnerException?.GetType().Name ?? "no error"})");
            }

            using var lenient = new NodeApiClient(url, skipCertificateCheck: true);
            var status = await lenient.StatusAsync();
            Assert.Equal(("minter-devnet", 10L), (status.Network, status.Height), "status read with the check off");
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception) { }
        }
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel on Windows wants a key it can find again; a PKCS#12 round trip gives one.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    private static async Task Serve(TcpListener listener, X509Certificate2 certificate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var tls = new SslStream(client.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(certificate);
                var request = new StringBuilder();
                var buffer = new byte[4096];
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await tls.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                var body = Encoding.UTF8.GetBytes(Status);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await tls.WriteAsync(head, ct);
                await tls.WriteAsync(body, ct);
                await tls.FlushAsync(ct);
            }
            catch (Exception error) when (error is AuthenticationException or IOException) { } // the refused handshake
        }
    }
}

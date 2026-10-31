using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MinterWallet;
using MinterWallet.Core;

public static class TelemetryScenario
{
    public static async Task<int> Run()
    {
        var first = Directory.CreateTempSubdirectory("usage-client-one-").FullName;
        var second = Directory.CreateTempSubdirectory("usage-client-two-").FullName;
        using var server = new PingServer();
        try
        {
            var a = new Shell(first) { UsageEndpoint = server.Endpoint };
            var b = new Shell(second) { UsageEndpoint = server.Endpoint };
            a.Settings.Telemetry = false;
            await a.PingUsageAsync();
            Check(a.Settings.InstallId == null && server.Clients.Count == 0, "disabled telemetry sends nothing");
            a.Settings.Telemetry = true;
            a.Settings.Save(first);
            if (OperatingSystem.IsWindows())
            {
                using var held = new FileStream(Path.Combine(first, WalletSettings.FileName), FileMode.Open, FileAccess.Read, FileShare.None);
                await a.PingUsageAsync();
                Check(server.Clients.Count == 0 && a.Settings.InstallId == null && a.UsageStatus.StartsWith("Статистика не отправлена", StringComparison.Ordinal), "failed id persistence fails closed and can retry");
            }
            else
            {
                var mode = File.GetUnixFileMode(first);
                try
                {
                    File.SetUnixFileMode(first, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    await a.PingUsageAsync();
                    Check(server.Clients.Count == 0 && a.Settings.InstallId == null && a.UsageStatus.StartsWith("Статистика не отправлена", StringComparison.Ordinal), "failed id persistence fails closed and can retry");
                }
                finally { File.SetUnixFileMode(first, mode); }
            }
            await a.PingUsageAsync();
            Check(server.Clients.Count == 1 && a.UsageStatus.StartsWith("Гейт подтвердил", StringComparison.Ordinal), "first launch registered and acknowledged");
            var id = a.Settings.InstallId;
            Check(id is { Length: 32 } && WalletSettings.Load(first).InstallId == id, "id persisted before sending");
            await a.PingUsageAsync();
            var restarted = new Shell(first) { UsageEndpoint = server.Endpoint };
            await restarted.PingUsageAsync();
            Check(server.Clients.Count == 1 && restarted.Settings.InstallId == id, "retry and restart reuse same install");
            b.StartUsage();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!b.UsageStatus.StartsWith("Гейт подтвердил", StringComparison.Ordinal) && DateTime.UtcNow < until) await Task.Delay(20);
            Check(server.Clients.Count == 2 && b.Settings.InstallId != id, "independent fresh client registered separately");
            b.UsageEndpoint = new Uri("http://127.0.0.1:1/");
            await b.PingUsageAsync();
            Check(b.UsageStatus.StartsWith("Статистика не отправлена", StringComparison.Ordinal), "transport failure is visible");
            Console.WriteLine("PASS telemetry client: opt-out, durable independent ids, restart deduplication, acknowledgement and visible errors");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL telemetry client: " + e); return 1; }
        finally { Directory.Delete(first, true); Directory.Delete(second, true); }
    }
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private sealed class PingServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public HashSet<string> Clients { get; } = [];
        public Uri Endpoint { get; }
        public PingServer()
        {
            listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            _ = Serve();
        }
        private async Task Serve()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                    var request = await reader.ReadLineAsync(stop.Token);
                    var length = 0;
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                    var body = new char[length];
                    Check(await reader.ReadBlockAsync(body, stop.Token) == length, "complete request body");
                    if (request?.StartsWith("POST /gate/v1/ping ", StringComparison.Ordinal) == true)
                    {
                        using var json = JsonDocument.Parse(new string(body));
                        Check(json.RootElement.EnumerateObject().Count() == 5, "payload has only the five anonymous fields");
                        Clients.Add(json.RootElement.GetProperty("id").GetString()!);
                    }
                    await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), stop.Token);
                }
            }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
        public void Dispose() { stop.Cancel(); listener.Stop(); stop.Dispose(); }
    }
}

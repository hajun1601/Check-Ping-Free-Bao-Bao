using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SpeedTestPro;

public sealed record PingResult(double Avg, double Min, double Max, double Jitter, double LossPercent);

public sealed record NetworkInfo(string Ip, string Location, string Isp, string Server);

public sealed class SpeedTestService : IDisposable
{
    private const string PingHost = "1.1.1.1";
    private const string DownloadUrl = "https://speed.cloudflare.com/__down?bytes=50000000";
    private const string UploadUrl = "https://speed.cloudflare.com/__up";
    private const string MetaUrl = "https://speed.cloudflare.com/meta";
    private const string TraceUrl = "https://speed.cloudflare.com/cdn-cgi/trace";

    private readonly HttpClient _http;

    public SpeedTestService()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 16
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SpeedTestPro/1.0");
    }

    // ───────────────────────── PING ─────────────────────────

    public async Task<PingResult> MeasurePingAsync(int count, IProgress<double>? progress, CancellationToken ct)
    {
        var rtts = new List<double>();
        int lost = 0;

        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(PingHost, 2000);
                if (reply.Status == IPStatus.Success)
                {
                    rtts.Add(reply.RoundtripTime);
                    progress?.Report(reply.RoundtripTime);
                }
                else lost++;
            }
            catch (PingException) { lost++; }

            await Task.Delay(150, ct);
        }

        // ICMP bị chặn (tường lửa/công ty)? Dùng độ trễ HTTP thay thế.
        if (rtts.Count == 0)
        {
            rtts = await MeasureHttpLatencyAsync(count, progress, ct);
            lost = count - rtts.Count;
        }

        if (rtts.Count == 0)
            throw new InvalidOperationException("Không đo được ping. Vui lòng kiểm tra kết nối mạng.");

        double jitter = 0;
        if (rtts.Count > 1)
            jitter = Enumerable.Range(1, rtts.Count - 1).Average(i => Math.Abs(rtts[i] - rtts[i - 1]));

        return new PingResult(rtts.Average(), rtts.Min(), rtts.Max(), jitter, lost * 100.0 / count);
    }

    private async Task<List<double>> MeasureHttpLatencyAsync(int count, IProgress<double>? progress, CancellationToken ct)
    {
        var list = new List<double>();
        try
        {
            // Lần đầu: bắt tay TLS, bỏ qua
            using (var warm = await _http.GetAsync(TraceUrl, HttpCompletionOption.ResponseHeadersRead, ct)) { }

            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                using var resp = await _http.GetAsync(TraceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                list.Add(sw.Elapsed.TotalMilliseconds);
                progress?.Report(sw.Elapsed.TotalMilliseconds);
                await Task.Delay(150, ct);
            }
        }
        catch (HttpRequestException) { }
        return list;
    }

    // ───────────────────────── ĐO ĐẠC BỔ SUNG ─────────────────────────

    /// <summary>Thời gian phân giải DNS trung bình (ms). Trả về null nếu không phân giải được.</summary>
    public async Task<double?> MeasureDnsAsync(CancellationToken ct)
    {
        string[] names = { "www.google.com", "www.cloudflare.com", "www.microsoft.com" };
        var times = new List<double>();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                await Dns.GetHostAddressesAsync(name, ct);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (SocketException) { }
        }
        return times.Count == 0 ? null : times.Average();
    }

    /// <summary>Ping trung bình tới Cloudflare, Google và Quad9 (4 lần mỗi máy chủ).</summary>
    public async Task<List<(string Name, double? Ms)>> MeasureHostsAsync(CancellationToken ct)
    {
        var hosts = new (string Name, string Addr)[]
        {
            ("Cloudflare", "1.1.1.1"),
            ("Google", "8.8.8.8"),
            ("Quad9", "9.9.9.9")
        };

        var result = new List<(string, double?)>();
        using var ping = new Ping();
        foreach (var (name, addr) in hosts)
        {
            var list = new List<double>();
            for (int i = 0; i < 4; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var r = await ping.SendPingAsync(addr, 2000);
                    if (r.Status == IPStatus.Success) list.Add(r.RoundtripTime);
                }
                catch (PingException) { }
                await Task.Delay(100, ct);
            }
            result.Add((name, list.Count > 0 ? list.Average() : null));
        }
        return result;
    }

    // ───────────────────────── DOWNLOAD / UPLOAD ─────────────────────────

    public Task<double> MeasureDownloadAsync(IProgress<double>? progress, CancellationToken ct) =>
        RunTransferAsync(async (add, token) =>
        {
            var buffer = new byte[64 * 1024];
            while (!token.IsCancellationRequested)
            {
                using var resp = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(token);
                int n;
                while ((n = await stream.ReadAsync(buffer, token)) > 0) add(n);
            }
        }, threads: 4, duration: TimeSpan.FromSeconds(10), progress, ct);

    public Task<double> MeasureUploadAsync(IProgress<double>? progress, CancellationToken ct) =>
        RunTransferAsync(async (add, token) =>
        {
            while (!token.IsCancellationRequested)
            {
                using var content = new CountingContent(2_000_000, add);
                using var resp = await _http.PostAsync(UploadUrl, content, token);
                resp.EnsureSuccessStatusCode();
            }
        }, threads: 3, duration: TimeSpan.FromSeconds(8), progress, ct);

    /// <summary>
    /// Chạy nhiều luồng song song, báo tốc độ tức thời (Mbps) mỗi 200ms,
    /// trả về tốc độ trung bình sau giai đoạn khởi động 1,5 giây.
    /// </summary>
    private static async Task<double> RunTransferAsync(
        Func<Action<int>, CancellationToken, Task> worker,
        int threads, TimeSpan duration,
        IProgress<double>? progress, CancellationToken ct)
    {
        long total = 0;
        void Add(int n) => Interlocked.Add(ref total, n);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(duration);

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            try { await worker(Add, runCts.Token); }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
            catch (IOException) { }
        })).ToArray();

        var sw = Stopwatch.StartNew();
        long lastBytes = 0, warmBytes = 0;
        double lastT = 0, warmT = 0, ema = 0;
        bool warmed = false;

        while (!runCts.IsCancellationRequested)
        {
            try { await Task.Delay(200, runCts.Token); }
            catch (OperationCanceledException) { break; }

            double t = sw.Elapsed.TotalSeconds;
            long b = Interlocked.Read(ref total);

            double inst = (b - lastBytes) * 8 / 1e6 / Math.Max(t - lastT, 0.001);
            ema = ema == 0 ? inst : ema * 0.6 + inst * 0.4;
            lastBytes = b;
            lastT = t;

            if (!warmed && t >= 1.5) { warmed = true; warmBytes = b; warmT = t; }
            progress?.Report(ema);
        }

        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        double end = sw.Elapsed.TotalSeconds;
        long endBytes = Interlocked.Read(ref total);
        if (endBytes == 0)
            throw new InvalidOperationException("Không nhận được dữ liệu từ máy chủ đo. Kiểm tra kết nối hoặc tường lửa.");

        double result = warmed && end > warmT
            ? (endBytes - warmBytes) * 8 / 1e6 / (end - warmT)
            : endBytes * 8 / 1e6 / end;

        progress?.Report(result);
        return result;
    }

    /// <summary>Nội dung upload, đếm số byte đã ghi ra socket.</summary>
    private sealed class CountingContent : HttpContent
    {
        private static readonly byte[] Chunk = CreateChunk();
        private readonly int _size;
        private readonly Action<int> _add;

        public CountingContent(int size, Action<int> add)
        {
            _size = size;
            _add = add;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        private static byte[] CreateChunk()
        {
            var b = new byte[16 * 1024];
            new Random(42).NextBytes(b);
            return b;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            int sent = 0;
            while (sent < _size)
            {
                int n = Math.Min(Chunk.Length, _size - sent);
                await stream.WriteAsync(Chunk.AsMemory(0, n));
                sent += n;
                _add(n);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _size;
            return true;
        }
    }

    // ───────────────────────── VỊ TRÍ / NHÀ MẠNG ─────────────────────────

    public async Task<NetworkInfo> GetNetworkInfoAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            var json = await _http.GetStringAsync(MetaUrl, timeout.Token);
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;

            string server = "Cloudflare";
            if (e.TryGetProperty("colo", out var colo))
            {
                string c = colo.ValueKind == JsonValueKind.Object
                    ? $"{Str(colo, "city")} ({Str(colo, "iata")})"
                    : colo.ValueKind == JsonValueKind.String ? colo.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(c)) server = $"Cloudflare – {c}";
            }

            return new NetworkInfo(
                Or(Str(e, "clientIp")),
                BuildLocation(Str(e, "city"), Str(e, "region"), Str(e, "country")),
                Or(Str(e, "asOrganization")),
                server);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }

        try
        {
            var json = await _http.GetStringAsync("https://ipinfo.io/json", timeout.Token);
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            return new NetworkInfo(
                Or(Str(e, "ip")),
                BuildLocation(Str(e, "city"), Str(e, "region"), Str(e, "country")),
                Or(Str(e, "org")),
                "Cloudflare");
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }

        return new NetworkInfo("Không xác định", "Không xác định", "Không xác định", "Cloudflare");
    }

    /// <summary>Thông tin card mạng đang dùng: Wi-Fi hay cáp, tên, tốc độ liên kết.</summary>
    public static string GetLocalAdapter()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                         && n.GetIPProperties().GatewayAddresses.Any(g =>
                                g.Address.AddressFamily == AddressFamily.InterNetwork
                                && !g.Address.Equals(IPAddress.Any)))
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();

            if (nic is null) return "Không có kết nối";

            string type = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Ethernet (có dây)",
                _ => nic.NetworkInterfaceType.ToString()
            };
            string speed = nic.Speed > 0 ? $" · liên kết {nic.Speed / 1_000_000} Mbps" : "";
            return $"{type} – {nic.Name}{speed}";
        }
        catch
        {
            return "Không xác định";
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "Không xác định" : s;

    private static string BuildLocation(string city, string region, string country)
    {
        if (country.Length == 2)
        {
            try { country = new RegionInfo(country).DisplayName; } catch { }
        }
        var parts = new[] { city, region, country }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct();
        var text = string.Join(", ", parts);
        return text.Length == 0 ? "Không xác định" : text;
    }

    public void Dispose() => _http.Dispose();
}

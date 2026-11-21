using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SpeedTestPro;

public partial class MainWindow : Window
{
    private readonly SpeedTestService _svc = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _dark = true;

    private NetworkInfo? _info;
    private PingResult? _ping;
    private double _download, _upload;
    private double? _dns;
    private List<(string Name, double? Ms)> _hosts = new();

    public MainWindow()
    {
        InitializeComponent();
        GaugeTrack.Data = BuildArc(1);
        SetGauge(0);
    }

    // ───────────── Sự kiện cửa sổ ─────────────

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try { await LoadNetworkInfoAsync(CancellationToken.None); }
        catch { /* bỏ qua, sẽ thử lại khi bắt đầu đo */ }
        StartButton.Focus();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        _svc.Dispose();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleTheme();
            e.Handled = true;
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e) => ToggleTheme();

    private void ToggleTheme()
    {
        _dark = !_dark;
        App.ApplyTheme(_dark);
        StatusText.Text = _dark ? "Đã chuyển sang giao diện tối." : "Đã chuyển sang giao diện sáng.";
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text =
            $"NetPing Bảo Bảo ( test ) – {DateTime.Now:dd/MM/yyyy HH:mm}\n" +
            $"IP: {_info?.Ip}\n" +
            $"Vị trí: {_info?.Location}\n" +
            $"Nhà mạng: {_info?.Isp}\n" +
            $"Ping: {_ping?.Avg:0} ms (min {_ping?.Min:0} / max {_ping?.Max:0}) | Jitter: {_ping?.Jitter:0.0} ms | Mất gói: {_ping?.LossPercent:0}%\n" +
            $"DNS: {(_dns.HasValue ? _dns.Value.ToString("0.0") + " ms" : "—")}\n" +
            $"Tải xuống: {_download:0.0} Mbps | Tải lên: {_upload:0.0} Mbps";
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = "Đã sao chép kết quả vào bộ nhớ tạm.";
        }
        catch
        {
            StatusText.Text = "Không thể sao chép. Vui lòng thử lại.";
        }
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(HistoryService.FilePath))
        {
            StatusText.Text = "Chưa có file lịch sử. Hãy đo một lần trước.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(HistoryService.FilePath) { UseShellExecute = true });
        }
        catch
        {
            // Không có chương trình mở CSV: mở thư mục chứa file
            Process.Start("explorer.exe", $"/select,\"{HistoryService.FilePath}\"");
        }
    }

    // ───────────── Chạy bài đo ─────────────

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            return;
        }
        await RunTestAsync();
    }

    private async Task RunTestAsync()
    {
        _running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        StartButton.Content = "Dừng";
        CopyButton.IsEnabled = false;
        ResetResults();

        try
        {
            // 1. Vị trí & nhà mạng
            SetPhase("Đang xác định vị trí mạng...");
            await LoadNetworkInfoAsync(ct);

            // 2. Ping
            SetPhase("Đang đo ping...");
            var pingProgress = new Progress<double>(ms => PingText.Text = ms.ToString("0"));
            _ping = await _svc.MeasurePingAsync(10, pingProgress, ct);
            PingText.Text = _ping.Avg.ToString("0");
            JitterText.Text = _ping.Jitter.ToString("0.0", CultureInfo.CurrentCulture);
            LossText.Text = $"Mất gói: {_ping.LossPercent:0}%";
            PingQuality.Text = "Chất lượng: " + RatePing(_ping.Avg);
            MinMaxText.Text = $"{_ping.Min:0} ms / {_ping.Max:0} ms";

            // 2b. DNS và ping tới nhiều máy chủ
            SetPhase("Đang đo DNS và các máy chủ...");
            _dns = await _svc.MeasureDnsAsync(ct);
            DnsText.Text = _dns.HasValue ? $"{_dns.Value:0.0} ms" : "Không đo được";
            _hosts = await _svc.MeasureHostsAsync(ct);
            foreach (var (name, ms) in _hosts)
            {
                string text = ms.HasValue ? $"{ms.Value:0} ms" : "Không phản hồi";
                if (name == "Cloudflare") HostCfText.Text = text;
                else if (name == "Google") HostGgText.Text = text;
                else if (name == "Quad9") HostQ9Text.Text = text;
            }

            // 3. Tải xuống
            SetPhase("Đang đo tốc độ tải xuống...");
            var dlProgress = new Progress<double>(SetGauge);
            _download = await _svc.MeasureDownloadAsync(dlProgress, ct);
            DownloadText.Text = Format(_download);
            SetGauge(_download);

            // 4. Tải lên
            SetPhase("Đang đo tốc độ tải lên...");
            var ulProgress = new Progress<double>(SetGauge);
            _upload = await _svc.MeasureUploadAsync(ulProgress, ct);
            UploadText.Text = Format(_upload);
            SetGauge(_upload);

            // 5. Lưu vào file lịch sử
            string saved;
            try
            {
                HistoryService.Append(_info, AdapterText.Text, _ping, _dns, _hosts, _download, _upload);
                saved = " Đã lưu vào file lịch sử.";
            }
            catch (Exception ex)
            {
                saved = " Không lưu được file lịch sử: " + ex.Message;
            }

            PhaseText.Text = "Hoàn tất";
            StatusText.Text =
                $"Hoàn tất. Ping {_ping.Avg:0} mili giây, tải xuống {_download:0.0} megabit mỗi giây, " +
                $"tải lên {_upload:0.0} megabit mỗi giây." + saved;
            CopyButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            PhaseText.Text = "Đã dừng";
            StatusText.Text = "Đã dừng bài đo.";
        }
        catch (Exception ex)
        {
            PhaseText.Text = "Lỗi";
            StatusText.Text = "Không thể hoàn tất: " + ex.Message;
        }
        finally
        {
            _running = false;
            StartButton.Content = "Đo lại";
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task LoadNetworkInfoAsync(CancellationToken ct)
    {
        AdapterText.Text = SpeedTestService.GetLocalAdapter();
        _info = await _svc.GetNetworkInfoAsync(ct);
        IpText.Text = _info.Ip;
        LocationText.Text = _info.Location;
        IspText.Text = _info.Isp;
        ServerText.Text = _info.Server;
    }

    // ───────────── Hiển thị ─────────────

    private void ResetResults()
    {
        PingText.Text = JitterText.Text = DownloadText.Text = UploadText.Text = "—";
        PingQuality.Text = "Đang đo...";
        LossText.Text = "Mất gói: —";
        MinMaxText.Text = DnsText.Text = HostCfText.Text = HostGgText.Text = HostQ9Text.Text = "—";
        _dns = null;
        _hosts = new();
        _download = _upload = 0;
        _ping = null;
        SetGauge(0);
    }

    private void SetPhase(string text)
    {
        PhaseText.Text = text;
        StatusText.Text = text;
    }

    private static string Format(double mbps) =>
        mbps.ToString(mbps >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture);

    private void SetGauge(double mbps)
    {
        SpeedText.Text = Format(mbps);
        double fraction = Math.Log10(1 + mbps) / Math.Log10(1001); // 0 → 1000 Mbps
        GaugeValue.Data = BuildArc(fraction);
    }

    /// <summary>Cung tròn 270° (từ góc dưới-trái, quay theo chiều kim đồng hồ).</summary>
    private static PathGeometry BuildArc(double fraction)
    {
        const double cx = 150, cy = 140, r = 118, startDeg = 135, sweepDeg = 270;
        fraction = Math.Clamp(fraction, 0.0005, 1);

        double a0 = startDeg * Math.PI / 180;
        double a1 = (startDeg + sweepDeg * fraction) * Math.PI / 180;

        var p0 = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
        var p1 = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));

        var figure = new PathFigure { StartPoint = p0, IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            p1, new Size(r, r), 0, sweepDeg * fraction > 180, SweepDirection.Clockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        return geo;
    }

    private static string RatePing(double ms) => ms switch
    {
        < 30 => "Rất tốt",
        < 60 => "Tốt",
        < 100 => "Trung bình",
        _ => "Kém"
    };
}

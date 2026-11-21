using System.IO;
using System.Text;

namespace SpeedTestPro;

/// <summary>
/// Ghi nối mỗi lần đo vào file CSV: Documents\SpeedTestPro\history.csv
/// (dùng dấu ';' và UTF-8 có BOM để Excel tiếng Việt mở đúng).
/// File nằm ngoài thư mục chương trình nên vẫn ghi được khi đóng gói thành exe.
/// </summary>
public static class HistoryService
{
    private const char Sep = ';';

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SpeedTestPro", "history.csv");

    private static readonly string[] Header =
    {
        "Thời gian", "IP", "Vị trí", "Nhà mạng", "Kết nối",
        "Ping TB (ms)", "Ping min (ms)", "Ping max (ms)", "Jitter (ms)", "Mất gói (%)",
        "DNS (ms)", "Ping Cloudflare (ms)", "Ping Google (ms)", "Ping Quad9 (ms)",
        "Tải xuống (Mbps)", "Tải lên (Mbps)"
    };

    public static void Append(
        NetworkInfo? info, string adapter, PingResult? ping, double? dns,
        IReadOnlyList<(string Name, double? Ms)> hosts, double download, double upload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var sb = new StringBuilder();
        bool isNew = !File.Exists(FilePath) || new FileInfo(FilePath).Length == 0;
        if (isNew) sb.AppendLine(string.Join(Sep, Header));

        double? Host(string name) => hosts.FirstOrDefault(h => h.Name == name).Ms;

        var cells = new[]
        {
            DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
            info?.Ip ?? "", info?.Location ?? "", info?.Isp ?? "", adapter,
            N(ping?.Avg), N(ping?.Min), N(ping?.Max), N(ping?.Jitter), N(ping?.LossPercent),
            N(dns), N(Host("Cloudflare")), N(Host("Google")), N(Host("Quad9")),
            N(download), N(upload)
        };
        sb.AppendLine(string.Join(Sep, cells.Select(Escape)));

        File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(true));
    }

    private static string N(double? v) => v.HasValue ? v.Value.ToString("0.0") : "";

    private static string Escape(string s) =>
        s.Contains(Sep) || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}

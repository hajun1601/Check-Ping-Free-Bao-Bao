using System.Windows;
using System.Windows.Media;

namespace SpeedTestPro;

public partial class App : Application
{
    /// <summary>Đổi giữa giao diện tối và sáng. Cả hai đều đạt tương phản WCAG AA.</summary>
    public static void ApplyTheme(bool dark)
    {
        var r = Current.Resources;

        void Set(string key, string hex) =>
            r[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        if (dark)
        {
            Set("BgBrush", "#0F172A");
            Set("CardBrush", "#1E293B");
            Set("LineBrush", "#475569");
            Set("TrackBrush", "#334155");
            Set("TextBrush", "#F8FAFC");
            Set("MutedBrush", "#CBD5E1");
            Set("AccentBrush", "#38BDF8");
            Set("OnAccentBrush", "#0F172A");
            Set("FocusBrush", "#FACC15");
        }
        else
        {
            Set("BgBrush", "#F1F5F9");
            Set("CardBrush", "#FFFFFF");
            Set("LineBrush", "#94A3B8");
            Set("TrackBrush", "#E2E8F0");
            Set("TextBrush", "#0F172A");
            Set("MutedBrush", "#475569");
            Set("AccentBrush", "#0369A1");
            Set("OnAccentBrush", "#FFFFFF");
            Set("FocusBrush", "#C2410C");
        }
    }
}

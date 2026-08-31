using System.Runtime.InteropServices;

namespace HaWinServer.Core;

/// <summary>
/// Draws the tray icon at runtime as a simple colored dot instead of shipping
/// .ico assets per state - one less thing to keep in sync with HassState, and
/// zero binary resources to maintain. The app's own .exe icon (Explorer,
/// Alt-Tab, taskbar) is a separate static Resources\app.ico, unrelated to this.
/// </summary>
public sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<HassState, Icon> _icons = new();

    public TrayIcons()
    {
        _icons[HassState.Stopped] = Create(Color.Gray);
        _icons[HassState.Starting] = Create(Color.Goldenrod);
        _icons[HassState.Stopping] = Create(Color.Goldenrod);
        _icons[HassState.Running] = Create(Color.SeaGreen);
        _icons[HassState.Error] = Create(Color.Firebrick);
    }

    public Icon For(HassState state) => _icons[state];

    private static Icon Create(Color dotColor)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // A house-shaped silhouette so the icon reads as "home" even at
            // 16px, with the state color as a small dot in the corner - similar
            // language to a lot of tray apps that show a base glyph + status dot.
            using var houseBrush = new SolidBrush(Color.FromArgb(235, 60, 60, 60));
            var houseBody = new RectangleF(6, 15, 20, 14);
            g.FillRectangle(houseBrush, houseBody);

            var roofPoints = new[]
            {
                new PointF(4, 16),
                new PointF(16, 5),
                new PointF(28, 16),
            };
            g.FillPolygon(houseBrush, roofPoints);

            using var dotBrush = new SolidBrush(dotColor);
            using var dotOutline = new Pen(Color.White, 1.5f);
            var dotRect = new RectangleF(18, 18, 11, 11);
            g.FillEllipse(dotBrush, dotRect);
            g.DrawEllipse(dotOutline, dotRect);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle wraps the handle but does not own it; cloning
            // into a new Icon lets us safely DestroyIcon the original right
            // away instead of tracking raw handles for the app's lifetime.
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
        _icons.Clear();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}

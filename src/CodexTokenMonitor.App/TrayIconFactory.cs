using System.Runtime.InteropServices;

namespace CodexTokenMonitor.App;

internal static class TrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(29, 51, 43));
        using var signal = new SolidBrush(Color.FromArgb(239, 108, 48));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        graphics.FillRectangle(signal, 8, 18, 4, 7);
        graphics.FillRectangle(signal, 14, 12, 4, 13);
        graphics.FillRectangle(signal, 20, 7, 4, 18);
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}

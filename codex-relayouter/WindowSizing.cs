// WindowSizing：集中管理 WinUI 3 主窗口的初始尺寸与屏幕居中逻辑。
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using Windows.Graphics;

namespace codex_bridge;

internal static class WindowSizing
{
    private const int BaselineWidth = 1800;
    private const int BaselineHeight = 1080;

    public static void ApplyStartupSizingAndCenter(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;

        var initialSize = CalculateInitialSize(workArea, BaselineWidth, BaselineHeight);

        appWindow.Resize(initialSize);
        CenterInWorkArea(appWindow, workArea, initialSize);
    }

    private static SizeInt32 CalculateInitialSize(RectInt32 workArea, int baselineWidth, int baselineHeight)
    {
        var maxWidth = (int)Math.Floor(workArea.Width * 0.95);
        var maxHeight = (int)Math.Floor(workArea.Height * 0.95);

        var width = Math.Min(baselineWidth, maxWidth);
        var height = Math.Min(baselineHeight, maxHeight);

        width = Math.Max(640, width);
        height = Math.Max(480, height);

        return new SizeInt32(width, height);
    }

    private static void CenterInWorkArea(AppWindow appWindow, RectInt32 workArea, SizeInt32 size)
    {
        var x = workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2);
        appWindow.Move(new PointInt32(x, y));
    }
}

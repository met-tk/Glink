using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using WinRT.Interop;

namespace Glink;

/// <summary>
/// 图片全屏查看窗口：
/// - 窗口居中于父窗口
/// - 按图片尺寸调整窗口大小（不超过屏幕 85%）
/// - 标题栏按钮浅色可见
/// - 滚轮以光标为中心缩放
/// - 左键拖动 1:1 跟手
/// - 双击在适配/100% 间切换，Esc 关闭
/// </summary>
public sealed partial class ImageViewerWindow : Window
{
    private readonly Grid _root;
    private readonly Image _image;
    private readonly ScaleTransform _scaleT;
    private readonly TranslateTransform _translateT;

    private double _scale = 1.0;
    private double _fitScale = 1.0;
    private Point _lastPos;
    private bool _isDragging;

    // 图片查看与视频查看同一"影院面"：底色恒黑、标题按钮与提示文字恒白系，
    // 观感不随应用主题翻转（图片可能很暗，主题深色文字会消失）。
    private void ApplyViewerTheme()
    {
        _root.RequestedTheme = ElementTheme.Dark;
        var title = AppWindow.TitleBar;
        title.BackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        title.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 0xF5, 0xF5, 0xF5);
        title.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        title.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(51, 0xFF, 0xFF, 0xFF);
        title.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
        title.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(34, 0xFF, 0xFF, 0xFF);
        title.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(153, 0xFF, 0xFF, 0xFF);
    }

    // 传入父窗口句柄用于居中
    public ImageViewerWindow(string imagePath, IntPtr parentHwnd)
    {
        Title = "图片查看";
        ExtendsContentIntoTitleBar = true;

        _root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        ApplyViewerTheme();

        // 布局驱动适配（标准图片 Viewer 的做法）：
        // Image 元素铺满 root，位图由布局引擎 contain-fit（Uniform）并自动居中。
        // 关键收益：渲染尺寸完全由"元素尺寸 × 图片宽高比"决定，与位图自然尺寸
        // （内嵌 DPI 元数据换算值）彻底解耦——Stretch.None 下位图内容会按自然
        // 尺寸渲染并裁剪到元素内，在非 96 DPI 监视器上（如 144）内容膨胀
        // ×(DPI/96) 且只显示左上角区域，表现为"图片被黑边挡住、黑边随窗口
        // 尺寸漂移"。缩放/平移改由 RenderTransform 叠加在布局适配之上。
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0)
        };

        _scaleT = new ScaleTransform();
        _translateT = new TranslateTransform();
        _image.RenderTransform = new TransformGroup { Children = { _scaleT, _translateT } };

        _root.Children.Add(_image);

        var hint = new TextBlock
        {
            Text = "拖动移动  ·  滚轮缩放  ·  双击适应窗口  ·  Esc 关闭",
            FontSize = 12,
            // 恒白系提示（影视惯例），与播放窗口的时间文字同款。
            Foreground = (Brush)Application.Current.Resources["PlayerTextDimBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16)
        };
        _root.Children.Add(hint);

        Content = _root;

        _root.PointerPressed += Root_PointerPressed;
        _root.PointerMoved += Root_PointerMoved;
        _root.PointerReleased += Root_PointerReleased;
        _root.PointerWheelChanged += Root_PointerWheelChanged;
        _root.DoubleTapped += Root_DoubleTapped;
        _root.KeyDown += Root_KeyDown;
        _root.SizeChanged += Root_SizeChanged;

        _parentHwnd = parentHwnd;
        // 先隐藏窗口避免默认尺寸闪烁，图片加载并调整尺寸后再激活显示
        AppWindow.Hide();
        LoadImage(imagePath);
    }

    private readonly IntPtr _parentHwnd;

    private async void LoadImage(string imagePath)
    {
        try
        {
            var bmp = new BitmapImage();
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(imagePath);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            await bmp.SetSourceAsync(stream);
            _image.Source = bmp;

            // 等布局完成后适配，再激活窗口显示（避免尺寸跳变闪烁）
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                SizeWindowToImage(bmp.PixelWidth, bmp.PixelHeight);
                FitToWindow();
                Activate();
            });
        }
        catch { /* Silent */ }
    }

    /// <summary>按图片尺寸调整窗口大小并居中于父窗口。</summary>
    private void SizeWindowToImage(int imgW, int imgH)
    {
        try
        {
            // DPI 缩放：AppWindow.Resize/Move 使用物理像素，图片尺寸是逻辑像素
            var hwnd = WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            var s = dpi / 96.0;

            // 通过 P/Invoke 获取工作区（物理像素）
            var workArea = GetWorkArea(_parentHwnd);
            var workW = workArea.Right - workArea.Left;
            var workH = workArea.Bottom - workArea.Top;
            var maxW = (int)(workW * 0.85);
            var maxH = (int)(workH * 0.85);

            // 期望窗口物理尺寸 = 图片逻辑尺寸 × DPI 缩放 + 边距
            var desiredW = (int)(imgW * s) + (int)(48 * s);
            var desiredH = (int)(imgH * s) + (int)(96 * s); // 标题栏 + 底部提示

            // 等比收缩到工作区 85% 以内：内容区保持图片宽高比。
            // 竖长图高度触顶时宽度必须同步收窄；旧实现宽高各自独立 clamp，
            // 会得到近似方形的窗口，适配后左右留出大片黑边。
            var shrink = Math.Min(1.0, Math.Min((double)maxW / desiredW, (double)maxH / desiredH));
            var winW = Math.Max((int)(desiredW * shrink), (int)(400 * s));
            var winH = Math.Max((int)(desiredH * shrink), (int)(300 * s));

            AppWindow.Resize(new Windows.Graphics.SizeInt32(winW, winH));

            // 居中于父窗口（物理像素）
            if (_parentHwnd != IntPtr.Zero)
            {
                GetWindowRect(_parentHwnd, out var pRect);
                var parentCx = pRect.Left + (pRect.Right - pRect.Left) / 2;
                var parentCy = pRect.Top + (pRect.Bottom - pRect.Top) / 2;
                var x = parentCx - winW / 2;
                var y = parentCy - winH / 2;
                if (x < workArea.Left) x = (int)workArea.Left;
                if (y < workArea.Top) y = (int)workArea.Top;
                if (x + winW > workArea.Right) x = (int)workArea.Right - winW;
                if (y + winH > workArea.Bottom) y = (int)workArea.Bottom - winH;
                AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
            }
        }
        catch { /* Silent */ }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_image.Source != null && !_isDragging)
            FitToWindow();
    }

    private void FitToWindow()
    {
        if (_image.Source is not BitmapSource src) return;
        var vw = _root.ActualWidth;
        var vh = _root.ActualHeight;
        if (vw <= 0 || vh <= 0) return;

        var iw = src.PixelWidth;
        var ih = src.PixelHeight;
        if (iw == 0 || ih == 0) return;

        // No 1:1 cap: the picture always adapts to the current window, scaling
        // up or down as needed, so double-click re-fits after any window resize.
        // 布局引擎（Stretch.Uniform）已在适配状态完成 contain-fit + 居中，
        // 适配态即 transform 恒等（k=1, t=0）。
        _fitScale = Math.Min(vw / iw, vh / ih);
        _scale = _fitScale;
        _scaleT.ScaleX = 1.0;
        _scaleT.ScaleY = 1.0;
        _translateT.X = 0;
        _translateT.Y = 0;
    }

    /// <summary>
    /// 应用叠加在布局适配之上的缩放：k 是相对适配态（Uniform contain-fit）的
    /// 额外倍率。布局引擎负责适配与居中，transform 只表达用户缩放/平移增量。
    /// </summary>
    private void ApplyTransform()
    {
        var k = _scale / _fitScale;
        _scaleT.ScaleX = k;
        _scaleT.ScaleY = k;
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _lastPos = e.GetCurrentPoint(_root).Position;
            _root.CapturePointer(e.Pointer);
        }
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetCurrentPoint(_root).Position;
        _translateT.X += pos.X - _lastPos.X;
        _translateT.Y += pos.Y - _lastPos.Y;
        _lastPos = pos;
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        _root.ReleasePointerCapture(e.Pointer);
    }

    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(_root).Position;
        var delta = e.GetCurrentPoint(_root).Properties.MouseWheelDelta;
        var factor = delta > 0 ? 1.2 : 1 / 1.2;
        var newScale = Math.Clamp(_scale * factor, _fitScale * 0.5, 8.0);
        factor = newScale / _scale;
        if (Math.Abs(factor - 1) < 0.0001) return;

        // 光标锚定（布局驱动模型）：最终位置 = 布局位置 × k + t，
        // 其中 k = _scale/_fitScale 是相对适配态（Uniform contain-fit + 居中）
        // 的额外倍率。保持光标下的内容不动：t' = p - ((p - t)/k) × k'。
        var k = _scale / _fitScale;
        var kNew = newScale / _fitScale;
        _translateT.X = pos.X - ((pos.X - _translateT.X) / k) * kNew;
        _translateT.Y = pos.Y - ((pos.Y - _translateT.Y) / k) * kNew;

        _scale = newScale;
        ApplyTransform();
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Double-click always scales the picture to fit the current window.
        FitToWindow();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
            Close();
    }

    // === P/Invoke for window centering ===

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private static RECT GetWorkArea(IntPtr hwnd)
    {
        try
        {
            var hMon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hMon, ref mi);
            return mi.rcWork;
        }
        catch
        {
            return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Xaml.Behaviors;

namespace DeskClone.Behaviors;

/// <summary>
/// 窗口拖拽调整大小时保持指定宽高比（默认 16:9）。
/// 通过拦截 WM_SIZING 消息并按拖拽的边修正目标矩形实现，
/// 不依赖任何第三方库。
/// </summary>
public sealed class WindowAspectRatioBehavior : Behavior<FrameworkElement>
{
    private const int WM_SIZING = 0x0214;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;

    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio),
        typeof(double),
        typeof(WindowAspectRatioBehavior),
        new PropertyMetadata(16d / 9d));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.Register(
        nameof(IsEnabled),
        typeof(bool),
        typeof(WindowAspectRatioBehavior),
        new PropertyMetadata(true));

    /// <summary>宽高比（宽 / 高）。所有内置分辨率都是 16:9。</summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>是否启用比例锁定。关闭后允许任意比例拉伸。</summary>
    public bool IsEnabled
    {
        get => (bool)GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }

    private HwndSource? _hookSource;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.Loaded += OnLoaded;
        AssociatedObject.Unloaded += OnUnloaded;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.Loaded -= OnLoaded;
        AssociatedObject.Unloaded -= OnUnloaded;
        DetachHook();
        base.OnDetaching();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DetachHook();
        if (Window.GetWindow(AssociatedObject) is { } window)
        {
            _hookSource = PresentationSource.FromVisual(window) as HwndSource;
            _hookSource?.AddHook(WndProc);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachHook();
    }

    private void DetachHook()
    {
        if (_hookSource is not null)
        {
            _hookSource.RemoveHook(WndProc);
            _hookSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_SIZING || !IsEnabled || AspectRatio <= 0)
        {
            return IntPtr.Zero;
        }

        var rect = Marshal.PtrToStructure<NativeRect>(lParam);
        var width = (double)(rect.Right - rect.Left);
        var height = (double)(rect.Bottom - rect.Top);
        if (width <= 0 || height <= 0)
        {
            return IntPtr.Zero;
        }

        switch (wParam.ToInt32())
        {
            // 拖左右边：宽度优先，按比例调整高度，顶边固定。
            case WmszLeft:
            case WmszRight:
                rect.Bottom = rect.Top + Math.Max(200, (int)Math.Round(width / AspectRatio));
                break;

            // 拖上下边：高度优先，水平居中调整宽度。
            case WmszTop:
            case WmszBottom:
            {
                var newWidth = Math.Max(320, (int)Math.Round(height * AspectRatio));
                var centerX = (rect.Left + rect.Right) / 2;
                rect.Left = centerX - newWidth / 2;
                rect.Right = centerX + newWidth / 2;
                break;
            }

            // 拖四角：对角固定，按比例修正高度（宽度的拖拽意图更直观，以宽度为准）。
            case WmszTopLeft:
            case WmszTopRight:
                rect.Top = rect.Bottom - Math.Max(200, (int)Math.Round(width / AspectRatio));
                break;

            case WmszBottomLeft:
            case WmszBottomRight:
                rect.Bottom = rect.Top + Math.Max(200, (int)Math.Round(width / AspectRatio));
                break;
        }

        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return new IntPtr(1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

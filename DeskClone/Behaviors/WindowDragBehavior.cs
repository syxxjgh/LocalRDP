using System.Windows;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace DeskClone.Behaviors;

/// <summary>让任意 FrameworkElement 成为窗口拖拽柄（按住左键拖动窗口）。</summary>
public sealed class WindowDragBehavior : Behavior<FrameworkElement>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.MouseLeftButtonDown -= OnMouseLeftButtonDown;
        base.OnDetaching();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            Window.GetWindow(AssociatedObject) is not { } window)
        {
            return;
        }

        e.Handled = true;
        window.DragMove();
    }
}

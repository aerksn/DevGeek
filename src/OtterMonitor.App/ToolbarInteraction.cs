using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OtterMonitor.App;

public partial class MainWindow
{
    private Point? toolbarPress;

    private void Toolbar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        toolbarPress = null;
        // Text editing keeps its own selection gesture.
        if (source is null || ReferenceEquals(source, FilterBox) || FilterBox.IsAncestorOf(source)) return;
        toolbarPress = e.GetPosition(this);
        // Buttons capture themselves on mouse-down. Capture empty space as well so
        // a drag can continue when the pointer leaves the header.
        while (source is not null && !ReferenceEquals(source, Toolbar))
        {
            if (source is ButtonBase) return;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        Toolbar.CaptureMouse();
    }

    private void Toolbar_MouseMove(object sender, MouseEventArgs e)
    {
        if (ContinueToolbarDrag(e.GetPosition(this), e.LeftButton, DragMove)) e.Handled = true;
    }

    private bool ContinueToolbarDrag(Point position, MouseButtonState leftButton, Action move)
    {
        if (leftButton != MouseButtonState.Pressed) { toolbarPress = null; return false; }
        if (toolbarPress is not { } origin) return false;
        if (Math.Abs(position.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return false;
        toolbarPress = null;
        // Losing capture resets ButtonBase.IsPressed before entering Windows' native
        // move loop, so releasing after a drag cannot click a button (including Close).
        Mouse.Capture(null);
        move();
        return true;
    }

    private void Toolbar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        toolbarPress = null;
        if (Toolbar.IsMouseCaptured) Toolbar.ReleaseMouseCapture();
    }

    private void Toolbar_LostMouseCapture(object sender, MouseEventArgs e) => toolbarPress = null;

    private void VerifyToolbarDrag(Action<bool, string> check)
    {
        var moveCount = 0;
        // Exercise the actual routed mouse-down from every action. The native modal
        // move loop is replaced here so verification never moves the user's pointer.
        foreach (var button in new[] { OtterMenuButton, BalanceButton, JevBalanceButton, AnthropicBalanceButton, DropdownButton, MinimizeButton, CloseButton })
        {
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            var origin = toolbarPress;
            check(origin is not null && !ContinueToolbarDrag(origin.Value, MouseButtonState.Pressed, () => moveCount++),
                button.Name + ": holding still preserves the normal click");
            button.CaptureMouse();
            // Synthetic events do not hold the physical mouse button. Capture can
            // synchronize a released-button move; restore the synthetic press.
            toolbarPress = origin;
            var dragged = ContinueToolbarDrag(origin.GetValueOrDefault() + new Vector(SystemParameters.MinimumHorizontalDragDistance + 2, 0),
                MouseButtonState.Pressed, () => moveCount++);
            check(dragged && Mouse.Captured is null && !button.IsPressed && toolbarPress is null,
                button.Name + ": dragging cancels the pressed button before moving");
            check(!ContinueToolbarDrag(new Point(1000, 1000), MouseButtonState.Pressed, () => moveCount++),
                button.Name + ": a gesture starts only one native move");
        }
        check(moveCount == 7, "Every toolbar button can initiate a drag");
        Toolbar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        check(Toolbar.IsMouseCaptured, "Empty header space captures the pointer for dragging");
        Toolbar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        check(toolbarPress is null && !Toolbar.IsMouseCaptured, "Releasing without dragging clears the pending gesture");
        OpenFilter(); UpdateLayout();
        FilterBox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        check(toolbarPress is null, "Text selection in the filter does not start a window drag");
        CloseFilter();
    }
}

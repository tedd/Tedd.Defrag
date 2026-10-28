using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Tedd.Defrag.Desktop;

/// <summary>Captures map drags and consumes Ctrl+wheel before the surrounding scroll view.</summary>
internal sealed class MapPointerInput : IDisposable
{
    private readonly UIElement _view;
    private readonly Action<float, float> _start, _drag;
    private readonly Action<float, float>? _hover;
    private readonly Func<Task> _end;
    private readonly Action _cancel;
    private readonly Func<float, float, bool, Task>? _zoom;
    private readonly PointerEventHandler _pressed, _moved, _released, _cancelled, _wheel;
    private readonly ManipulationModes _originalManipulationMode;
    private uint? _pointer;

    public MapPointerInput(UIElement view, Action<float, float> start, Action<float, float> drag,
        Func<Task> end, Action cancel, Action<float, float>? hover = null, Func<float, float, bool, Task>? zoom = null)
    {
        _view = view; _start = start; _drag = drag; _end = end; _cancel = cancel; _hover = hover; _zoom = zoom;
        _pressed = Pressed; _moved = Moved; _released = Released; _cancelled = Cancelled; _wheel = Wheel;
        _originalManipulationMode = view.ManipulationMode;
        view.ManipulationMode = ManipulationModes.None;
        view.AddHandler(UIElement.PointerPressedEvent, _pressed, true);
        view.AddHandler(UIElement.PointerMovedEvent, _moved, true);
        view.AddHandler(UIElement.PointerReleasedEvent, _released, true);
        view.AddHandler(UIElement.PointerCanceledEvent, _cancelled, true);
        view.AddHandler(UIElement.PointerCaptureLostEvent, _cancelled, true);
        view.AddHandler(UIElement.PointerWheelChangedEvent, _wheel, true);
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_view);
        if (_pointer.HasValue || point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
        if (!_view.CapturePointer(e.Pointer)) return;
        _pointer = e.Pointer.PointerId;
        e.Handled = true;
        _start((float)point.Position.X, (float)point.Position.Y);
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(_view).Position;
        _hover?.Invoke((float)position.X, (float)position.Y);
        if (_pointer != e.Pointer.PointerId) return;
        e.Handled = true;
        _drag((float)position.X, (float)position.Y);
    }

    private async void Released(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        var position = e.GetCurrentPoint(_view).Position;
        _drag((float)position.X, (float)position.Y);
        _pointer = null;
        e.Handled = true;
        _view.ReleasePointerCapture(e.Pointer);
        await _end();
    }

    private void Cancelled(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != e.Pointer.PointerId) return;
        _pointer = null;
        _view.ReleasePointerCapture(e.Pointer);
        _cancel();
    }

    private async void Wheel(object sender, PointerRoutedEventArgs e)
    {
        if (_zoom == null || _pointer.HasValue || (e.KeyModifiers & VirtualKeyModifiers.Control) == 0) return;
        var point = e.GetCurrentPoint(_view);
        if (point.Properties.MouseWheelDelta == 0 || point.Properties.IsHorizontalMouseWheel) return;
        e.Handled = true;
        await _zoom((float)point.Position.X, (float)point.Position.Y, point.Properties.MouseWheelDelta > 0);
    }

    public void Dispose()
    {
        if (_pointer.HasValue) { _pointer = null; _view.ReleasePointerCaptures(); _cancel(); }
        _view.RemoveHandler(UIElement.PointerPressedEvent, _pressed);
        _view.RemoveHandler(UIElement.PointerMovedEvent, _moved);
        _view.RemoveHandler(UIElement.PointerReleasedEvent, _released);
        _view.RemoveHandler(UIElement.PointerCanceledEvent, _cancelled);
        _view.RemoveHandler(UIElement.PointerCaptureLostEvent, _cancelled);
        _view.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheel);
        _view.ManipulationMode = _originalManipulationMode;
    }
}

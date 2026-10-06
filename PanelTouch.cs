using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace NewsWatch;

/// <summary>
/// Touch for an overlay panel: tap, press-and-hold, and a sideways swipe toward
/// the docked edge that throws it off (a short or slow one springs back).
/// Touch is handled here rather than promoted to mouse, so the panel's mouse
/// handlers should ignore promoted input (<see cref="FromTouch"/>).
/// </summary>
internal sealed class PanelTouch
{
    private const double MoveSlop = 10;           // DIPs before a touch counts as a drag, not a tap
    private const double DismissFraction = 0.35;  // of the panel's width
    private const double FlingSpeed = 0.6;        // DIPs per ms toward the edge
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(600);

    private readonly FrameworkElement _tile;
    private readonly TranslateTransform _shift;
    private readonly Func<bool> _onLeft;
    private readonly Action _tap;
    private readonly Action? _hold;
    private readonly Action _swipedOff;
    private readonly Action? _started;
    private readonly Action? _ended;
    private readonly DispatcherTimer _holdTimer = new() { Interval = HoldTime };

    private TouchDevice? _touch;
    private Point _start;
    private bool _dragging, _scrolling, _held;
    private double _lastX, _velocity;
    private int _lastTime;

    /// <param name="onLeft">Which edge the panel is docked to, read per gesture.</param>
    /// <param name="hold">Press-and-hold; null means holding does nothing special.</param>
    /// <param name="swipedOff">The panel was thrown off; dismiss it (its slide-out continues from where the finger left it).</param>
    /// <param name="started">A finger went down: hold any auto-dismiss timer.</param>
    /// <param name="ended">The finger lifted and the panel is still there.</param>
    public static void Attach(FrameworkElement tile, Func<bool> onLeft, Action tap, Action? hold,
        Action swipedOff, Action? started = null, Action? ended = null)
        => _ = new PanelTouch(tile, onLeft, tap, hold, swipedOff, started, ended);

    /// <summary>A mouse event that is really a promoted touch, already handled as a gesture.</summary>
    public static bool FromTouch(MouseEventArgs e)
        => e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch;

    private PanelTouch(FrameworkElement tile, Func<bool> onLeft, Action tap, Action? hold,
        Action swipedOff, Action? started, Action? ended)
    {
        _tile = tile;
        _shift = (TranslateTransform)tile.RenderTransform;
        _onLeft = onLeft;
        _tap = tap;
        _hold = hold;
        _swipedOff = swipedOff;
        _started = started;
        _ended = ended;

        // Our own hold replaces Windows' press-and-hold ring and its right-click.
        Stylus.SetIsPressAndHoldEnabled(tile, false);
        Stylus.SetIsFlicksEnabled(tile, false);

        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            if (_touch == null || _dragging || _scrolling) return;
            _held = true;
            _hold?.Invoke();
        };
        tile.TouchDown += OnTouchDown;
        tile.TouchMove += OnTouchMove;
        tile.TouchUp += OnTouchUp;
        tile.LostTouchCapture += (_, e) =>
        {
            if (e.TouchDevice == _touch) Finish(e.TouchDevice, cancelled: true);
        };
    }

    private void OnTouchDown(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (_touch != null) return; // one finger drives a panel

        _touch = e.TouchDevice;
        _tile.CaptureTouch(_touch);
        _start = e.GetTouchPoint(null).Position;
        _dragging = _scrolling = _held = false;
        _velocity = 0;
        _lastX = _start.X;
        _lastTime = Environment.TickCount;

        // Take the panel from wherever its slide-in / spring-back animation has it.
        var x = _shift.X;
        _shift.BeginAnimation(TranslateTransform.XProperty, null);
        _shift.X = x;
        var opacity = _tile.Opacity;
        _tile.BeginAnimation(UIElement.OpacityProperty, null);
        _tile.Opacity = opacity;

        if (_hold != null) _holdTimer.Start();
        _started?.Invoke();
    }

    private void OnTouchMove(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (e.TouchDevice != _touch || _held || _scrolling) return;

        var p = e.GetTouchPoint(null).Position;
        var dx = p.X - _start.X;
        var dy = p.Y - _start.Y;

        if (!_dragging)
        {
            if (Math.Abs(dx) < MoveSlop && Math.Abs(dy) < MoveSlop) return;
            _holdTimer.Stop();
            // Mostly vertical: not a swipe (and no longer a tap).
            if (Math.Abs(dy) > Math.Abs(dx)) { _scrolling = true; return; }
            _dragging = true;
        }

        var now = Environment.TickCount;
        var elapsed = now - _lastTime;
        if (elapsed > 0)
        {
            _velocity = (p.X - _lastX) / elapsed;
            _lastX = p.X;
            _lastTime = Environment.TickCount;
        }

        // Follows the finger toward the edge; resists being pulled the other way.
        var outward = _onLeft() ? -dx : dx;
        _shift.X = outward >= 0 ? dx : dx * 0.25;
        _tile.Opacity = 1 - 0.6 * Math.Clamp(outward / Width, 0, 1);
    }

    private void OnTouchUp(object? sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (e.TouchDevice == _touch) Finish(e.TouchDevice, cancelled: false);
    }

    private void Finish(TouchDevice device, bool cancelled)
    {
        _touch = null;
        _holdTimer.Stop();
        _tile.ReleaseTouchCapture(device);

        if (_dragging)
        {
            var sign = _onLeft() ? -1 : 1;
            var outward = sign * _shift.X;
            var speed = sign * _velocity;
            // A stale velocity (finger paused before lifting) shouldn't fling.
            if (Environment.TickCount - _lastTime > 100) speed = 0;
            if (!cancelled && (outward > Width * DismissFraction || (outward > 24 && speed > FlingSpeed)))
            {
                _swipedOff();
                return;
            }
            SpringBack();
        }
        else if (!cancelled && !_held && !_scrolling)
        {
            _tap();
            return; // tap dismisses or opens-and-dismisses: nothing to resume
        }
        _ended?.Invoke();
    }

    private void SpringBack()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _shift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _tile.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
    }

    private double Width => Math.Max(_tile.ActualWidth, 1);
}

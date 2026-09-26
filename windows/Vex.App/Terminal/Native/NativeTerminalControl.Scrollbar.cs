using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    // Scrollbar overlay state. The bar hugs the right edge and stays hidden
    // until the user scrolls, drags, or hovers it — then fades out after a
    // short idle. Hover/drag also widens the thumb (animated).
    private const double ScrollbarThinWidth = 6;
    private const double ScrollbarWideWidth = 12;
    private const double ScrollbarHitWidth = 16;
    private const double ScrollbarHideDelayMs = 900;
    private double _scrollbarWidth = ScrollbarThinWidth;
    private double _scrollbarTargetWidth = ScrollbarThinWidth;
    private double _scrollbarOpacity;
    private double _scrollbarTargetOpacity;
    private bool _scrollbarHovered;
    private bool _scrollbarDragging;
    private double _scrollbarDragOffset;
    private Brush _scrollbarThumbBrush = Brushes.Gray;
    private Brush _scrollbarThumbHoverBrush = Brushes.LightGray;
    private readonly DispatcherTimer _scrollbarAnimTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly DispatcherTimer _scrollbarHideTimer = new() { Interval = TimeSpan.FromMilliseconds(ScrollbarHideDelayMs) };

    private void InitializeScrollbar()
    {
        _scrollbarAnimTimer.Tick += (_, _) => AnimateScrollbarVisual();
        _scrollbarHideTimer.Tick += (_, _) =>
        {
            _scrollbarHideTimer.Stop();
            if (!_scrollbarHovered && !_scrollbarDragging)
                SetScrollbarOpacity(0);
        };
    }

    private void RebuildScrollbarBrushes()
    {
        var fg = ((SolidColorBrush)_palette.Foreground).Color;
        _scrollbarThumbBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0x50, fg.R, fg.G, fg.B));
        _scrollbarThumbHoverBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0xA0, fg.R, fg.G, fg.B));
    }

    /// <summary>True while there is scrollback above the viewport to scroll into.</summary>
    private bool IsScrollbarVisible()
    {
        var scrollbar = _terminal.Scrollbar;
        return scrollbar.Total > scrollbar.Len && scrollbar.Len > 0;
    }

    private bool IsOverScrollbar(Point point)
        => ScrollbarOwnsPointer && IsScrollbarVisible() && point.X >= ActualWidth - ScrollbarHitWidth;

    /// <summary>Whether the overlay scrollbar may act on the pointer. It must
    /// stand down for an app that captured the mouse, otherwise a TUI loses
    /// clicks and motion in its rightmost columns; Shift overrides the app,
    /// matching xterm and Windows Terminal.</summary>
    private bool ScrollbarOwnsPointer
        => !_mouseTracking || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    /// <summary>
    /// Thumb geometry. The scrollable extent is the scrollback above the
    /// viewport plus the viewport itself; the viewport offset maps linearly
    /// onto the thumb's travel along the track.
    /// </summary>
    private bool TryGetScrollbarGeometry(out double thumbY, out double thumbH, out double travel)
    {
        thumbY = thumbH = travel = 0;
        if (!IsScrollbarVisible())
            return false;

        var scrollbar = _terminal.Scrollbar;
        var trackHeight = ActualHeight;
        thumbH = Math.Min(trackHeight, Math.Max(24, trackHeight * scrollbar.Len / Math.Max(1, scrollbar.Total)));
        travel = Math.Max(0, trackHeight - thumbH);
        var scrollable = scrollbar.Total - scrollbar.Len;
        thumbY = travel * scrollbar.Offset / Math.Max(1, scrollable);
        return true;
    }

    private void DrawScrollbar()
    {
        using var dc = _scrollbarVisual.RenderOpen();
        if (_scrollbarOpacity < 0.01 || !TryGetScrollbarGeometry(out var thumbY, out var thumbH, out _))
            return;

        var brush = _scrollbarHovered || _scrollbarDragging ? _scrollbarThumbHoverBrush : _scrollbarThumbBrush;
        var x = ActualWidth - _scrollbarWidth;
        var radius = _scrollbarWidth / 2;
        dc.PushOpacity(_scrollbarOpacity);
        dc.DrawRoundedRectangle(brush, null, new Rect(x, thumbY, _scrollbarWidth, thumbH), radius, radius);
        dc.Pop();
    }

    /// <summary>Shows the overlay scrollbar after user-driven scroll or hover,
    /// then schedules a fade-out unless the pointer is still on it.</summary>
    private void RevealScrollbar()
    {
        if (!IsScrollbarVisible())
        {
            SetScrollbarOpacity(0);
            _scrollbarHideTimer.Stop();
            return;
        }

        SetScrollbarOpacity(1);
        if (_scrollbarHovered || _scrollbarDragging)
            _scrollbarHideTimer.Stop();
        else
            ScheduleScrollbarHide();
    }

    private void ScheduleScrollbarHide()
    {
        _scrollbarHideTimer.Stop();
        _scrollbarHideTimer.Start();
    }

    private void SetScrollbarOpacity(double opacity)
    {
        if (Math.Abs(_scrollbarTargetOpacity - opacity) < 0.01)
            return;
        _scrollbarTargetOpacity = opacity;
        if (Math.Abs(_scrollbarTargetOpacity - _scrollbarOpacity) < 0.01)
            DrawScrollbar();
        else if (!_scrollbarAnimTimer.IsEnabled)
            _scrollbarAnimTimer.Start();
    }

    private void SetScrollbarHovered(bool hovered)
    {
        var targetWidth = hovered || _scrollbarDragging ? ScrollbarWideWidth : ScrollbarThinWidth;
        if (_scrollbarHovered == hovered && _scrollbarTargetWidth == targetWidth)
            return;
        _scrollbarHovered = hovered;
        Cursor = hovered ? Cursors.Arrow : Cursors.IBeam;
        _scrollbarTargetWidth = targetWidth;

        if (hovered && IsScrollbarVisible())
        {
            SetScrollbarOpacity(1);
            _scrollbarHideTimer.Stop();
        }
        else if (!_scrollbarDragging)
        {
            ScheduleScrollbarHide();
        }

        if (Math.Abs(_scrollbarTargetWidth - _scrollbarWidth) < 0.2
            && Math.Abs(_scrollbarTargetOpacity - _scrollbarOpacity) < 0.01)
            DrawScrollbar();
        else if (!_scrollbarAnimTimer.IsEnabled)
            _scrollbarAnimTimer.Start();
    }

    private void AnimateScrollbarVisual()
    {
        var widthDelta = _scrollbarTargetWidth - _scrollbarWidth;
        var opacityDelta = _scrollbarTargetOpacity - _scrollbarOpacity;
        var widthDone = Math.Abs(widthDelta) < 0.2;
        var opacityDone = Math.Abs(opacityDelta) < 0.01;

        if (widthDone)
            _scrollbarWidth = _scrollbarTargetWidth;
        else
            _scrollbarWidth += widthDelta * 0.3;

        if (opacityDone)
            _scrollbarOpacity = _scrollbarTargetOpacity;
        else
            _scrollbarOpacity += opacityDelta * 0.35;

        if (widthDone && opacityDone)
            _scrollbarAnimTimer.Stop();

        DrawScrollbar();
    }

    private void DragScrollbarThumb(Point pos)
    {
        if (!TryGetScrollbarGeometry(out _, out var thumbH, out var travel) || travel <= 0)
            return;
        var scrollbar = _terminal.Scrollbar;
        var scrollable = scrollbar.Total - scrollbar.Len;
        var desired = Math.Clamp(pos.Y - _scrollbarDragOffset, 0, travel);
        var offset = (ulong)Math.Round(desired * scrollable / travel);
        if (offset != scrollbar.Offset)
        {
            _terminal.ScrollToRow(offset);
            RevealScrollbar();
            FlushRedraw();
        }
    }
}

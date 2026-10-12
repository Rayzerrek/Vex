using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private void RebuildFontMetrics()
    {
        var key = new FontMetricsKey(_normalTypeface, _fontSize, _pixelsPerDip);
        // Tab switches unload/reload the surface without changing its font.
        // Keep shaped glyphs and geometry warm, but still check the new layout.
        if (_fontMetricsKey == key)
        {
            RecalculateGridSize();
            return;
        }
        _fontMetricsKey = key;
        _fallbackCellPlacements.Clear();
        _fallbackCellDrawings.Clear();
        _glyphAdvanceCache.Clear();
        StopCopyAnimation();
        ClearLinkHover();
        // Cell geometry is part of every row's pixels; any rebuild invalidates
        // all row caches so the next pass repaints the surface.
        _renderVersion++;

        // The glyph face was already resolved (and the font file parsed) by
        // ApplyTypefaces; reusing it here avoids a second font-file parse on
        // every rebuild.
        var typeface = _normalTypeface;
        _baselineY = _fontSize * _fontFamily.Baseline;

        if (_normalGlyph is { } glyph)
        {
            var em = _fontSize;
            var map = glyph.CharacterToGlyphMap;
            var advance = map.TryGetValue('M', out var mGlyph) ? glyph.AdvanceWidths[mGlyph] : glyph.AdvanceWidths[0];
            _cellWidth = Math.Max(1, advance * em);
            _cellHeight = Math.Max(1, Math.Ceiling(em * _fontFamily.LineSpacing));
        }
        else
        {
            var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, _fontSize, Brushes.White, _pixelsPerDip);
            _baselineY = probe.Baseline;
            _cellWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace);
            _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));
        }

        RecalculateGridSize();
    }

    private void RecalculateGridSize()
    {
        // WPF can run queued layout callbacks before the deferred Unloaded
        // event; IsLoaded alone does not protect a freed native emulator.
        if (_disposed || !IsLoaded || ActualWidth < 150 || ActualHeight < 80)
            return;

        var cols = Math.Max(2, (int)(ActualWidth / _cellWidth));
        var rows = Math.Max(1, (int)(ActualHeight / _cellHeight));
        var nativeCellWidth = Math.Max(1, (int)Math.Round(_cellWidth * _pixelsPerDip));
        var nativeCellHeight = Math.Max(1, (int)Math.Round(_cellHeight * _pixelsPerDip));
        var gridChanged = cols != _cols || rows != _rows;
        if (!gridChanged && nativeCellWidth == _nativeCellWidth && nativeCellHeight == _nativeCellHeight
            && (_session is not null || _sessionStarting))
            return;

        // Preserve the user's scrollback pin: only snap to bottom when they
        // were already there. Unconditional ScrollToBottom after every reflow
        // is what made resize feel like the buffer was reloading and jumping.
        var wasAtBottom = !gridChanged || IsPinnedToBottom(_terminal.Scrollbar);

        _cols = cols;
        _rows = rows;
        _nativeCellWidth = nativeCellWidth;
        _nativeCellHeight = nativeCellHeight;
        Diag($"grid {cols}x{rows} session={_session is not null}");
        EnsureRowVisuals();
        if (gridChanged)
            InvalidateRowPaintCaches();
        _terminal.Resize(cols, rows, nativeCellWidth, nativeCellHeight);
        if (gridChanged && wasAtBottom)
            _terminal.ScrollToBottom();
        _needsFullRedraw = true;

        if (_session is null)
        {
            // Start the shell as soon as this pane has a grid — not on
            // keyboard focus. Focus arrives only after the first frame plus
            // a ContextIdle dispatch, which used to delay the prompt by
            // hundreds of milliseconds; starting here warms the shell up
            // behind the first painted frame like Windows Terminal. Only
            // the selected tab's panes are realized, so this cannot spawn
            // shells for background tabs.
            // UpdateFrame must run before the repaint so FrameRows reflects
            // the new geometry before StartSession paints the first frame.
            FlushRedraw();
            StartSessionIfReady();
        }
        else if (gridChanged)
        {
            // Resize ConPTY before painting so the child and VT agree on the
            // new grid for this frame. Failures are retried on settle.
            ApplySessionResize((short)cols, (short)rows);
            FlushRedraw();
            ScheduleResizeSettle();
        }
        else
        {
            FlushRedraw();
        }
    }

    private static bool IsPinnedToBottom((ulong Total, ulong Offset, ulong Len) sb)
    {
        if (sb.Len == 0 || sb.Total <= sb.Len)
            return true;
        return sb.Offset + sb.Len >= sb.Total;
    }


    private void EnsureRowVisuals()
    {
        while (_rowVisuals.Count > _rows)
        {
            var last = _rowVisuals[^1];
            _rowVisuals.RemoveAt(_rowVisuals.Count - 1);
            _children.Remove(last);
        }
        while (_rowVisuals.Count < _rows)
        {
            var visual = new DrawingVisual();
            _rowVisuals.Add(visual);
            // Keep feedback above every row even when a resize adds visuals.
            _children.Insert(_children.IndexOf(_linkHoverVisual), visual);
        }
        if (_rowHashes.Length != _rows)
        {
            // Version 0 never matches the current _renderVersion, so the
            // cells are all repainted on the next pass after a resize.
            _rowHashes = new ulong[_rows];
            _rowVersions = new int[_rows];
            _rowLinks = new LinkSpan[_rows][];
            _rowLinkHashes = new ulong[_rows];
            _rowLinkTexts = new string?[_rows];
        }
    }

    /// <summary>Drops per-row paint caches after a width change so wrapped
    /// glyphs cannot reuse pre-resize hashes.</summary>
    private void InvalidateRowPaintCaches()
    {
        if (_rowHashes.Length == 0)
            return;
        Array.Clear(_rowHashes);
        Array.Clear(_rowVersions);
        Array.Clear(_rowLinkHashes);
        Array.Clear(_rowLinkTexts);
        for (var i = 0; i < _rowLinks.Length; i++)
            _rowLinks[i] = Array.Empty<LinkSpan>();
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size availableSize)
        => double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height)
            ? new Size(0, 0)
            : availableSize;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_disposed)
            return;
        StopCopyAnimation();
        ClearLinkHover();
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Coalesce to one pass per rendered frame: while the window edge is
        // being dragged, size events can fire several times per frame and each
        // one would resize the emulator (buffer reflow) and the ConPTY session.
        if (_resizeScheduled)
            return;
        _resizeScheduled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _resizeScheduled = false;
            RecalculateGridSize();
        });
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_disposed)
            return;
        StopCopyAnimation();
        ClearLinkHover();
        _fallbackCellPlacements.Clear();
        _fallbackCellDrawings.Clear();
        _glyphAdvanceCache.Clear();
        _pixelsPerDip = newDpi.PixelsPerDip;
        // Glyph rasterization scales with pixels-per-dip even when the DIP
        // cell size does not, so the cached row pixels and native mouse
        // geometry are stale.
        _renderVersion++;
        _needsFullRedraw = true;
        RecalculateGridSize();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_palette.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }
}

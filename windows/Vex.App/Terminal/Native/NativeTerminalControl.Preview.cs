using System.Windows;
using System.Windows.Media;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private bool _renderingPreview;
    private long _previewRevision;
    private long _capturedPreviewRevision = -1;
    private DrawingImage? _previewImage;

    /// <summary>Captures current terminal drawings without arranging the view, resizing its PTY, or changing its viewport.</summary>
    internal ImageSource? CaptureTerminalPreview()
    {
        if (_disposed || _cols == 0 || _rows == 0)
            return null;
        try
        {
            // Hidden terminals normally defer painting. Only the open overview
            // requests their latest frame; it never reparents the real control.
            _renderingPreview = true;
            FlushRedraw();
        }
        finally { _renderingPreview = false; }
        if (_previewImage is not null && _capturedPreviewRevision == _previewRevision)
            return _previewImage;

        var size = new Rect(0, 0, _cols * _cellWidth, _rows * _cellHeight);
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(size) };
        using (var context = drawing.Open())
        {
            context.DrawRectangle(_palette.Background, null, size);
            foreach (var row in _rowVisuals)
                context.DrawDrawing(row.Drawing);
        }
        // Drawing snapshots retain the old frame even as row visuals repaint.
        drawing.Freeze();
        _previewImage = new DrawingImage(drawing);
        _previewImage.Freeze();
        _capturedPreviewRevision = _previewRevision;
        return _previewImage;
    }

#if DEBUG || VEX_SELFTEST
    internal bool SelfTestTerminalPreview()
    {
        var visibility = Visibility;
        var cols = _cols;
        var rows = _rows;
        SelfTestFeed("\x1b[?25l\x1b[2J\x1b[Hpreview-before");
        var first = CaptureTerminalPreview();
        var stable = ReferenceEquals(first, CaptureTerminalPreview());
        var scroll = _terminal.Scrollbar;
        var previousSample = _performanceSample;
        var sample = new PerformanceSample();
        _performanceSample = sample;
        try
        {
            Visibility = Visibility.Hidden;
            SelfTestFeed("\x1b[?1000h");
            var unchanged = ReferenceEquals(first, CaptureTerminalPreview()) && sample.RowsDrawn == 0;
            SelfTestFeed("\x1b[Hpreview-after");
            var changed = CaptureTerminalPreview();
            var incremental = sample.RowsDrawn == 1;
            var latest = SelfTestRowText(0).StartsWith("preview-after", StringComparison.Ordinal);
            SelfTestFeed("\x1b[?2026h\x1b[Hatomic-preview");
            var deferred = ReferenceEquals(changed, CaptureTerminalPreview());
            SelfTestFeed("\x1b[?2026l");
            var atomic = CaptureTerminalPreview();
            return stable && unchanged && incremental && first is DrawingImage { IsFrozen: true } && !ReferenceEquals(first, changed)
                && latest && deferred && !ReferenceEquals(changed, atomic)
                && _cols == cols && _rows == rows && _terminal.Scrollbar == scroll;
        }
        finally
        {
            _performanceSample = previousSample;
            SelfTestFeed("\x1b[?1000l");
            Visibility = visibility;
        }
    }
#endif
}

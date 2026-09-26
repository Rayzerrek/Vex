using System.Text;
using System.Windows;
using System.Windows.Input;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private readonly Dictionary<Key, TerminalKeyModifiers> _terminalKeysDown = new();
    private PendingTextKey? _pendingTextKey;

    private readonly record struct PendingTextKey(Key WpfKey, TerminalKey Key, TerminalKeyAction Action, TerminalKeyModifiers Modifiers);

    private readonly byte[] _mouseReport = new byte[128];
    private int _reportedMouseButtons;
    private int _wheelDeltaRemainder;

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        StartSessionIfReady();
        UpdateBlinkTimer();
        DrawCaret();
        FocusGained?.Invoke();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_session is not null)
        {
            foreach (var (key, modifiers) in _terminalKeysDown)
            {
                if (TerminalKeyMap.TryMap(key, out var terminalKey))
                    SendTerminalKey(key, terminalKey, TerminalKeyAction.Release, modifiers, ReadOnlySpan<byte>.Empty);
            }
        }
        _terminalKeysDown.Clear();
        _pendingTextKey = null;
        UpdateBlinkTimer();
        DrawCaret();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // Modifier-only presses are the first half of chords like Ctrl+C used
        // to copy the selection; clearing here would tear the selection down
        // before the chord completes.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        // Word/line-wise keyboard selection owns the Ctrl+Shift+arrow chords
        // (Windows Terminal/Ghostty style): arrows step a whole word, Home/
        // End jump to the line edges. Selection outranks pane management, so
        // splitting moved to Ctrl+Shift+R / Ctrl+Shift+D below.
        if (!_terminal.IsAlternateScreen &&
            mods == (ModifierKeys.Control | ModifierKeys.Shift) &&
            key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            ExtendKeyboardSelection(key, byWord: true);
            e.Handled = true;
            return;
        }

        // Full-screen terminal applications own Ctrl+Shift chords too. They
        // use these combinations for navigation and command palettes just as
        // often as ordinary Ctrl chords.
        if (!_terminal.IsAlternateScreen && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            var command = key switch
            {
                Key.R => TerminalCommand.SplitRight,
                Key.D => TerminalCommand.SplitDown,
                Key.T => TerminalCommand.NewTab,
                Key.W => TerminalCommand.ClosePane,
                _ => (TerminalCommand?)null,
            };
            if (command is { } requested)
            {
                CommandRequested?.Invoke(requested);
                e.Handled = true;
                return;
            }
            if (key == Key.C)
            {
                CopySelection();
                e.Handled = true;
                return;
            }
            if (key == Key.V)
            {
                PasteClipboard();
                e.Handled = true;
                return;
            }
            if (key == Key.X)
            {
                CutSelection();
                e.Handled = true;
                return;
            }
        }

        // Ctrl+C copies when a selection exists, otherwise sends ETX.
        if (key == Key.C && mods == ModifierKeys.Control && (_selectionActive || _terminal.HasSelection))
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        // Keyboard selection (Shift+Arrow) drives the same selection gesture
        // the mouse uses, with a virtual caret independent of the shell
        // cursor. Only in the primary screen: full-screen apps own these keys.
        if (mods == ModifierKeys.Shift && !_terminal.IsAlternateScreen &&
            key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            ExtendKeyboardSelection(key, byWord: false);
            e.Handled = true;
            return;
        }

        ClearSelection();

        // Viewport scroll keybindings (the alternate screen has no scrollback).
        if (!_terminal.IsAlternateScreen)
        {
            if (mods == ModifierKeys.Control && key == Key.Home)
            {
                _terminal.ScrollToTop();
                RevealScrollbar();
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Control && key == Key.End)
            {
                _terminal.ScrollToBottom();
                RevealScrollbar();
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Shift && key == Key.PageUp)
            {
                _terminal.ScrollBy(-_rows);
                RevealScrollbar();
                FlushRedraw();
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Shift && key == Key.PageDown)
            {
                _terminal.ScrollBy(_rows);
                RevealScrollbar();
                FlushRedraw();
                e.Handled = true;
                return;
            }
        }

        if (!TerminalKeyMap.TryMap(key, out var terminalKey))
            return;

        var terminalModifiers = CurrentTerminalModifiers(mods);
        var action = e.IsRepeat ? TerminalKeyAction.Repeat : TerminalKeyAction.Press;
        var isTextKey = TerminalKeyMap.IsTextKey(key);
        var reportAllKeys = (_terminal.KittyKeyboardFlags & 0b01000) != 0;

        // WPF delivers layout/IME text after KeyDown. In Kitty's report-all
        // mode defer text-producing keys to TextInput so the native encoder
        // can attach the actual composed UTF-8 text instead of guessing from
        // a physical key on a potentially non-US layout.
        if (isTextKey && reportAllKeys &&
            (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            _pendingTextKey = new PendingTextKey(key, terminalKey, action, terminalModifiers);
            return;
        }

        // In legacy mode ordinary text still belongs to TextInput. Ctrl/Alt
        // chords are key events, however, because the encoder must apply the
        // terminal's disambiguation and meta-prefix modes.
        if (isTextKey && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            return;

        // Preserve Vex's shell-friendly Ctrl+Backspace behavior in legacy
        // mode. Kitty has an unambiguous Backspace+Ctrl representation and
        // must go through the native encoder instead.
        if (key == Key.Back && mods == ModifierKeys.Control && _terminal.KittyKeyboardFlags == 0)
        {
            ReadOnlySpan<byte> ctrlBackspace = stackalloc byte[] { 0x17 };
            _session.Write(ctrlBackspace);
            e.Handled = true;
            return;
        }

        if (SendTerminalKey(key, terminalKey, action, terminalModifiers, ReadOnlySpan<byte>.Empty))
            e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_session is null)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!_terminalKeysDown.Remove(key, out var modifiers) ||
            !TerminalKeyMap.TryMap(key, out var terminalKey))
            return;

        if (SendTerminalKey(key, terminalKey, TerminalKeyAction.Release, modifiers, ReadOnlySpan<byte>.Empty))
            e.Handled = true;
    }

    private TerminalKeyModifiers CurrentTerminalModifiers(ModifierKeys modifiers)
    {
        var result = TerminalKeyMap.MapModifiers(modifiers);
        if (Keyboard.IsKeyToggled(Key.CapsLock)) result |= TerminalKeyModifiers.CapsLock;
        if (Keyboard.IsKeyToggled(Key.NumLock)) result |= TerminalKeyModifiers.NumLock;
        if (Keyboard.IsKeyDown(Key.RightShift)) result |= TerminalKeyModifiers.RightShift;
        if (Keyboard.IsKeyDown(Key.RightCtrl)) result |= TerminalKeyModifiers.RightControl;
        if (Keyboard.IsKeyDown(Key.RightAlt)) result |= TerminalKeyModifiers.RightAlt;
        if (Keyboard.IsKeyDown(Key.RWin)) result |= TerminalKeyModifiers.RightSuper;
        return result;
    }

    private bool SendTerminalKey(Key wpfKey, TerminalKey key, TerminalKeyAction action,
        TerminalKeyModifiers modifiers, ReadOnlySpan<byte> utf8)
    {
        var session = _session;
        if (session is null)
            return false;

        Span<byte> output = stackalloc byte[512];
        var written = _terminal.EncodeKey(
            key, action, modifiers, utf8, TerminalKeyMap.UnshiftedCodepoint(wpfKey), output);
        if (written == 0)
            return false;

        session.Write(output[..written]);
        if (action is TerminalKeyAction.Press or TerminalKeyAction.Repeat)
            _terminalKeysDown[wpfKey] = modifiers;
        return true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (DiagPath is not null)
            Diag($"text '{e.Text.Replace("\r", "<CR>")}' session={_session is not null}");
        if (_session is null || string.IsNullOrEmpty(e.Text))
            return;

        if (_pendingTextKey is { } pending)
        {
            _pendingTextKey = null;
            if (e.Text.Length <= 32)
            {
                Span<byte> text = stackalloc byte[Encoding.UTF8.GetMaxByteCount(e.Text.Length)];
                var textLength = Encoding.UTF8.GetBytes(e.Text, text);
                SendTerminalKey(pending.WpfKey, pending.Key, pending.Action, pending.Modifiers, text[..textLength]);
            }
            else
            {
                SendTerminalKey(pending.WpfKey, pending.Key, pending.Action, pending.Modifiers, Encoding.UTF8.GetBytes(e.Text));
            }
            e.Handled = true;
            return;
        }

        // Short single-line text (the overwhelmingly common case) encodes
        // into a stack buffer; the pooled fallback covers multi-line pastes
        // via IME. Avoids a byte[] allocation per keystroke.
        if (e.Text.Length <= 32 && e.Text.IndexOf('\n') < 0)
        {
            Span<byte> buffer = stackalloc byte[Encoding.UTF8.GetMaxByteCount(e.Text.Length)];
            var written = Encoding.UTF8.GetBytes(e.Text, buffer);
            _session.Write(buffer[..written]);
        }
        else
        {
            _session.Write(Encoding.UTF8.GetBytes(e.Text));
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_session is null)
            return;

        var pos = e.GetPosition(this);

        // The scrollbar overlays the right edge. It yields to an app that
        // captured the mouse (Shift still reaches it) so a TUI never loses
        // clicks in its rightmost columns.
        if (IsOverScrollbar(pos) && TryGetScrollbarGeometry(out var thumbY, out var thumbH, out _))
        {
            if (pos.Y >= thumbY && pos.Y <= thumbY + thumbH)
            {
                _scrollbarDragging = true;
                _scrollbarDragOffset = pos.Y - thumbY;
                _scrollbarWidth = _scrollbarTargetWidth = ScrollbarWideWidth;
                CaptureMouse();
                RevealScrollbar();
                DrawScrollbar();
            }
            else
            {
                _terminal.ScrollBy(pos.Y < thumbY ? -_rows : _rows);
                RevealScrollbar();
                FlushRedraw();
            }
            e.Handled = true;
            return;
        }

        var (col, row) = CellFromPoint(pos);

        // Ctrl+click on a detected URL opens it in the browser instead of
        // selecting or forwarding the click to the app's mouse mode.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && LinkUriAt(col, row) is { } linkUri)
        {
            OpenLink(linkUri);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _mouseTracking)
        {
            // Shift overrides app mouse capture, as in xterm; it starts a
            // fresh selection at the pointer instead of extending.
            _selectionActive = true;
            _selectionDragged = false;
            _selectionGestureActive = true;
            _mouseSelectionOverride = true;
            var nativePos = NativePoint(pos);
            _selectionClickCount = _terminal.SelectionPress(col, row, nativePos.X, nativePos.Y);
            CaptureMouse();
            FlushRedraw();
            e.Handled = true;
            return;
        }

        // An app that enabled mouse tracking owns the click outright; in
        // non-tracking mode the control handles it as the start of a
        // selection.
        if (_mouseTracking)
        {
            SetReportedButton(MouseInputButton.Left, true);
            SendMouse(MouseInputAction.Press, MouseInputButton.Left, pos);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        _selectionActive = true;
        _selectionDragged = false;
        _selectionGestureActive = true;
        var selectionPos = NativePoint(pos);
        _selectionClickCount = _terminal.SelectionPress(col, row, selectionPos.X, selectionPos.Y);
        CaptureMouse();
        FlushRedraw();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var pos = e.GetPosition(this);

        if (_scrollbarDragging)
        {
            DragScrollbarThumb(pos);
            e.Handled = true;
            return;
        }

        var overScrollbar = IsOverScrollbar(pos);
        SetScrollbarHovered(overScrollbar);

        // Hand cursor over detected links, except while dragging a selection
        // or the scrollbar thumb (where the pointer means something else).
        if (!_scrollbarDragging && !overScrollbar && e.LeftButton != MouseButtonState.Pressed)
        {
            var (hoverCol, hoverRow) = CellFromPoint(pos);
            Cursor = IsOverLink(hoverCol, hoverRow) && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                ? Cursors.Hand
                : _mouseTracking && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    ? Cursors.Arrow
                    : Cursors.IBeam;
        }

        // The overlay scrollbar owns unbuttoned hover at the right edge. A
        // drag that started in the terminal remains captured by the TUI.
        if (overScrollbar && _reportedMouseButtons == 0 && !_mouseSelectionOverride)
        {
            e.Handled = true;
            return;
        }

        if (_mouseTracking && !_mouseSelectionOverride)
        {
            SendMouse(MouseInputAction.Motion, PressedButton(e), pos);
            e.Handled = true;
            return;
        }
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && _selectionActive)
        {
            var (col, row) = CellFromPoint(pos);
            _selectionDragged = true;
            var nativePos = NativePoint(pos);
            _terminal.SelectionDrag(col, row, nativePos.X, nativePos.Y);
            FlushRedraw();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_scrollbarDragging)
        {
            _scrollbarDragging = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            SetScrollbarHovered(IsOverScrollbar(e.GetPosition(this)));
            DrawScrollbar();
            e.Handled = true;
            return;
        }

        if (_mouseSelectionOverride)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            _selectionGestureActive = false;
            _terminal.SelectionRelease(col, row);

            bool madeSelection = _selectionDragged || _selectionClickCount > 1;
            if (!madeSelection)
                FinishClickSelection();
            else
                CopySelection();

            _selectionDragged = false;
            _mouseSelectionOverride = false;
        }
        else if (_mouseTracking || IsReportedButton(MouseInputButton.Left))
        {
            var pos = e.GetPosition(this);
            SetReportedButton(MouseInputButton.Left, false);
            SendMouse(MouseInputAction.Release, MouseInputButton.Left, pos);
            e.Handled = true;
        }
        else if (_selectionActive)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            _selectionGestureActive = false;
            _terminal.SelectionRelease(col, row);

            bool madeSelection = _selectionDragged || _selectionClickCount > 1;
            if (!madeSelection)
                FinishClickSelection();
            else
                CopySelection();

            _selectionDragged = false;
        }
        ReleaseMouseIfNoButtons();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        var position = Mouse.GetPosition(this);
        if (_selectionGestureActive)
        {
            var (col, row) = CellFromPoint(position);
            var wasDragged = _selectionDragged;
            var clickCount = _selectionClickCount;
            _selectionGestureActive = false;
            _terminal.SelectionRelease(col, row);
            _mouseSelectionOverride = false;
            _selectionDragged = false;

            bool madeSelection = wasDragged || clickCount > 1;
            if (!madeSelection)
                FinishClickSelection();
            else
                CopySelection();
        }

        _scrollbarDragging = false;
        SetScrollbarHovered(false);
        foreach (var button in new[] { MouseInputButton.Left, MouseInputButton.Middle, MouseInputButton.Right })
        {
            if (!IsReportedButton(button))
                continue;
            SetReportedButton(button, false);
            SendMouse(MouseInputAction.Release, button, position);
        }
        DrawScrollbar();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        if (_mouseTracking)
        {
            var pos = e.GetPosition(this);
            SetReportedButton(MouseInputButton.Right, true);
            SendMouse(MouseInputAction.Press, MouseInputButton.Right, pos);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        PasteClipboard();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_mouseTracking && !IsReportedButton(MouseInputButton.Right))
            return;
        SetReportedButton(MouseInputButton.Right, false);
        SendMouse(MouseInputAction.Release, MouseInputButton.Right, e.GetPosition(this));
        ReleaseMouseIfNoButtons();
        e.Handled = true;
    }

    private void OnTerminalMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        Focus();
        if (!_mouseTracking)
            return;
        SetReportedButton(MouseInputButton.Middle, true);
        SendMouse(MouseInputAction.Press, MouseInputButton.Middle, e.GetPosition(this));
        CaptureMouse();
        e.Handled = true;
    }

    private void OnTerminalMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        if (!_mouseTracking && !IsReportedButton(MouseInputButton.Middle))
            return;
        SetReportedButton(MouseInputButton.Middle, false);
        SendMouse(MouseInputAction.Release, MouseInputButton.Middle, e.GetPosition(this));
        ReleaseMouseIfNoButtons();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetScrollbarHovered(false);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var steps = ConsumeWheelSteps(e.Delta);

        // Shift always addresses Vex's own scrollback, matching xterm and
        // Windows Terminal. Without the override an app that captured the
        // mouse owns the wheel outright, leaving no way to scroll back into
        // output the app has already scrolled past.
        var shiftScrollsLocally = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_mouseTracking && !shiftScrollsLocally)
        {
            var button = steps > 0 ? MouseInputButton.WheelUp : MouseInputButton.WheelDown;
            for (var i = 0; i < Math.Abs(steps); i++)
            {
                SendMouse(MouseInputAction.Press, button, e.GetPosition(this));
                SendMouse(MouseInputAction.Release, button, e.GetPosition(this));
            }
            e.Handled = true;
            return;
        }

        if (_terminal.IsAlternateScreen)
            return; // viewport scrollback does not exist on the alt screen

        // The system setting uses -1 for "scroll a page at a time".
        var configured = SystemParameters.WheelScrollLines;
        var lines = (configured < 0 ? Math.Max(1, _rows - 1) : Math.Max(1, configured)) * steps;
        if (lines != 0)
        {
            _terminal.ScrollBy(-lines);
            RevealScrollbar();
            FlushRedraw();
        }
        e.Handled = true;
    }

    private int ConsumeWheelSteps(int delta)
    {
        _wheelDeltaRemainder += delta;
        var steps = _wheelDeltaRemainder / 120;
        _wheelDeltaRemainder %= 120;
        return steps;
    }

    private void SendMouse(MouseInputAction action, MouseInputButton? button, Point position)
    {
        var keyboard = Keyboard.Modifiers;
        var modifiers = MouseInputModifiers.None;
        if (keyboard.HasFlag(ModifierKeys.Shift)) modifiers |= MouseInputModifiers.Shift;
        if (keyboard.HasFlag(ModifierKeys.Control)) modifiers |= MouseInputModifiers.Control;
        if (keyboard.HasFlag(ModifierKeys.Alt)) modifiers |= MouseInputModifiers.Alt;

        // The native encoder takes integer geometry while WPF renders in
        // fractional DIPs. Scale into the same coordinate space supplied to
        // ghostty_terminal_resize so cell-edge clicks cannot drift a column.
        var nativePosition = NativePoint(position);
        var len = _terminal.EncodeMouse(action, button, modifiers,
            nativePosition.X, nativePosition.Y, _reportedMouseButtons != 0, _mouseReport);
        if (len > 0)
            _session?.Write(_mouseReport.AsSpan(0, len));
    }

    private void SetReportedButton(MouseInputButton button, bool pressed)
    {
        var bit = 1 << (int)button;
        if (pressed)
            _reportedMouseButtons |= bit;
        else
            _reportedMouseButtons &= ~bit;
    }

    private bool IsReportedButton(MouseInputButton button)
        => (_reportedMouseButtons & (1 << (int)button)) != 0;

    private MouseInputButton? PressedButton(MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) return MouseInputButton.Left;
        if (e.MiddleButton == MouseButtonState.Pressed) return MouseInputButton.Middle;
        if (e.RightButton == MouseButtonState.Pressed) return MouseInputButton.Right;
        return null;
    }

    private void ReleaseMouseIfNoButtons()
    {
        if (_reportedMouseButtons == 0 && IsMouseCaptured)
            ReleaseMouseCapture();
    }

    private Point NativePoint(Point point)
        => new(point.X * _nativeCellWidth / _cellWidth,
            point.Y * _nativeCellHeight / _cellHeight);

    private (int Col, int Row) CellFromPoint(Point point)
    {
        var col = Math.Clamp((int)(point.X / _cellWidth), 0, Math.Max(0, _cols - 1));
        var row = Math.Clamp((int)(point.Y / _cellHeight), 0, Math.Max(0, _rows - 1));
        return (col, row);
    }
}

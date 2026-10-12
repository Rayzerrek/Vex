using static Vex.Libghostty.Native;

namespace Vex.Libghostty;

public sealed partial class GhosttyTerminal
{
    /// <summary>Encodes a pointer event using the exact tracking mode and
    /// wire format requested by the terminal application. The encoder is
    /// driven from the live mode bits — the same state the host's routing
    /// decisions read — so routing and encoding can never disagree; the
    /// cached last-transition flag that setopt_from_terminal relied on
    /// collapses to "none" when an app resets one tracking mode while
    /// another is still set, silently emitting zero bytes for every event.
    /// setopt is dirty-checked since modes change rarely. A successful event
    /// can still produce zero bytes when its current mode filters that
    /// event.</summary>
    public unsafe int EncodeMouse(MouseInputAction action, MouseInputButton? button,
        MouseInputModifiers modifiers, double x, double y, bool anyButtonPressed,
        byte[] output)
    {
        lock (_vtLock)
        {
            var tracking = TrackingModeLocked;
            if (_lastEncodedTracking != tracking)
            {
                _lastEncodedTracking = tracking;
                Native.ghostty_mouse_encoder_setopt(_mouseEncoder, MouseEncoderOption.Event, (IntPtr)(&tracking));
            }
            var format = FormatLocked;
            if (_lastEncodedFormat != format)
            {
                _lastEncodedFormat = format;
                Native.ghostty_mouse_encoder_setopt(_mouseEncoder, MouseEncoderOption.Format, (IntPtr)(&format));
            }

            var pressedValue = anyButtonPressed ? 1 : 0;
            if (_mouseAnyButtonPressed != pressedValue)
            {
                _mouseAnyButtonPressed = pressedValue;
                byte pressed = anyButtonPressed ? (byte)1 : (byte)0;
                Native.ghostty_mouse_encoder_setopt(_mouseEncoder, MouseEncoderOption.AnyButtonPressed, (IntPtr)(&pressed));
            }

            Native.ghostty_mouse_event_set_action(_mouseEvent, action);
            if (button is { } value)
                Native.ghostty_mouse_event_set_button(_mouseEvent, value);
            else
                Native.ghostty_mouse_event_clear_button(_mouseEvent);
            Native.ghostty_mouse_event_set_mods(_mouseEvent, modifiers);
            Native.ghostty_mouse_event_set_position(_mouseEvent, new Native.GhosttyMousePosition
            {
                x = (float)x,
                y = (float)y,
            });

            var result = Native.ghostty_mouse_encoder_encode(
                _mouseEncoder, _mouseEvent, output, (nuint)output.Length, out var written);
            Check(result, "mouse_encoder_encode");
            return checked((int)written);
        }
    }

    private unsafe void ConfigureMouseEncoderSize()
    {
        var size = new Native.GhosttyMouseEncoderSize
        {
            size = (nuint)sizeof(Native.GhosttyMouseEncoderSize),
            screenWidth = (uint)Math.Max(1, _cols * _cellWidthPx),
            screenHeight = (uint)Math.Max(1, _rows * _cellHeightPx),
            cellWidth = (uint)Math.Max(1, _cellWidthPx),
            cellHeight = (uint)Math.Max(1, _cellHeightPx),
        };
        Native.ghostty_mouse_encoder_setopt(_mouseEncoder, MouseEncoderOption.Size, (IntPtr)(&size));
    }
}

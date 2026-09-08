using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vex.App.Model;
using Vex.App.Terminal.Native;

namespace Vex.App;

public sealed partial class ThemeSwitcher : OverlayControl
{
    private string _openingTheme = "";
    private bool _editorOpen;
    private readonly List<TextBox> _colorInputs = new();

    // The editable color slots shown in the editor, in display order.
    private static readonly (string Label, string Property)[] EditableColors =
    {
        ("Background", nameof(TerminalTheme.Background)),
        ("Foreground", nameof(TerminalTheme.Foreground)),
        ("Cursor", nameof(TerminalTheme.Cursor)),
        ("Selection", nameof(TerminalTheme.SelectionBackground)),
        ("Black", nameof(TerminalTheme.Black)),
        ("Red", nameof(TerminalTheme.Red)),
        ("Green", nameof(TerminalTheme.Green)),
        ("Yellow", nameof(TerminalTheme.Yellow)),
        ("Blue", nameof(TerminalTheme.Blue)),
        ("Magenta", nameof(TerminalTheme.Magenta)),
        ("Cyan", nameof(TerminalTheme.Cyan)),
        ("White", nameof(TerminalTheme.White)),
    };

    public ThemeSwitcher()
    {
        InitializeComponent();
        HideOnWindowDeactivate();
    }

    protected override void HideCore() => Hide();

    public void Show()
    {
        // Re-bind on every open: the visible set follows the active
        // appearance (dark chrome shows dark themes only, light shows light).
        var themes = BuiltInThemes.ForAppearance(AppSettings.Instance.IsDarkAppearance);
        ThemeList.ItemsSource = themes;

        // Snapshot so Escape can revert a previewed theme.
        _openingTheme = AppSettings.Instance.ThemeName;

        Visibility = Visibility.Visible;
        OpenPopup(this);

        ThemeList.SelectedItem = themes.FirstOrDefault(t => t.Name == _openingTheme)
            ?? themes.FirstOrDefault();
        // SelectionChanged fires above and applies the resolved theme live,
        // which also corrects a stale cross-appearance setting.

        AnimateOverlayOpen(Backdrop, Panel, PanelScale, PanelTranslate);

        Dispatcher.BeginInvoke(() => ThemeList.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide() => HideWithAnimation(Backdrop, Panel);

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();

    private void Panel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ThemeList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Item containers are non-focusable, so click-selection is done by hand.
        if (ItemsControl.ContainerFromElement(ThemeList, e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            ThemeList.SelectedItem = item.DataContext;
            Hide();
            e.Handled = true;
        }
    }

    private void ThemeList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                AppSettings.Instance.ThemeName = _openingTheme;
                Hide();
                e.Handled = true;
                break;
            case Key.Enter:
                Hide();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (ThemeList.Items.Count == 0)
            return;
        var next = ((ThemeList.SelectedIndex + delta) % ThemeList.Items.Count + ThemeList.Items.Count) % ThemeList.Items.Count;
        ThemeList.SelectedIndex = next;
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection is the live preview: moving across cards re-tints the
        // whole app immediately, so Enter/click simply keep it.
        if (ThemeList.SelectedItem is TerminalTheme theme && AppSettings.Instance.ThemeName != theme.Name)
            AppSettings.Instance.ThemeName = theme.Name;

        // If the editor is open, rebuild it for the newly selected theme.
        if (_editorOpen && ThemeList.SelectedItem is TerminalTheme selected)
            RebuildColorGrid(selected);
    }

    // ---- Theme Editor -------------------------------------------------------

    private void EditToggle_Click(object sender, RoutedEventArgs e)
    {
        _editorOpen = !_editorOpen;
        EditToggle.Content = _editorOpen ? "Done" : "Edit";
        EditorPanel.Visibility = _editorOpen ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = _editorOpen
            ? "click a swatch to edit · live preview"
            : "← → move · Enter keep · Esc revert";

        if (_editorOpen && ThemeList.SelectedItem is TerminalTheme selected)
            RebuildColorGrid(selected);
        else if (!_editorOpen)
        {
            ColorGrid.Children.Clear();
            _colorInputs.Clear();
        }
    }

    /// <summary>Builds a grid of color swatches with hex inputs for the given
    /// theme. Edits write into <see cref="BuiltInThemes.Custom"/>, a mutable
    /// singleton, so the live preview picks up changes immediately through the
    /// normal theme-apply pipeline.</summary>
    private void RebuildColorGrid(TerminalTheme source)
    {
        ColorGrid.Children.Clear();
        _colorInputs.Clear();

        // Copy the selected theme into Custom so built-ins stay pristine.
        CopyInto(source, BuiltInThemes.Custom);

        foreach (var (label, property) in EditableColors)
        {
            var hex = (string?)typeof(TerminalTheme).GetProperty(property)?.GetValue(BuiltInThemes.Custom) ?? "#000000";

            var swatchBorder = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(4),
                BorderBrush = (Brush)FindResource("VexBorder"), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(FastColor.ParseHex(hex)),
            };

            var hexBox = new TextBox
            {
                Text = hex, FontSize = 11, Width = 82, Margin = new Thickness(4,0,0,0),
                Background = (Brush)FindResource("VexSurface"),
                Foreground = (Brush)FindResource("VexText"),
                BorderBrush = (Brush)FindResource("VexBorder"),
                BorderThickness = new Thickness(1), Padding = new Thickness(4,2,4,2),
                Tag = property, // store the property name for the change handler
            };
            hexBox.TextChanged += ColorInput_TextChanged;
            _colorInputs.Add(hexBox);

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 10, 8),
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(swatchBorder);
            panel.Children.Add(hexBox);
            ColorGrid.Children.Add(panel);
        }
    }

    private void ColorInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not string property)
            return;

        var hex = box.Text.Trim();
        if (!hex.StartsWith('#') || hex.Length < 7)
            return;

        try
        {
            var color = FastColor.ParseHex(hex);
            typeof(TerminalTheme).GetProperty(property)?.SetValue(BuiltInThemes.Custom, hex);

            // Update the swatch next to the input.
            if (box.Parent is StackPanel panel && panel.Children[0] is Border swatch)
                swatch.Background = new SolidColorBrush(color);

            // Apply the custom theme live: chrome + terminal palette cache.
            NativeTerminalControl.InvalidatePaletteCache("Custom");
            if (AppSettings.Instance.ThemeName != "Custom")
                AppSettings.Instance.ThemeName = "Custom";
            else
            {
                // ThemeName is already "Custom" — the setter won't fire
                // PropertyChanged, so apply manually.
                ChromePalette.Apply(BuiltInThemes.Custom);
            }
        }
        catch { /* ignore partial/invalid hex while typing */ }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeList.SelectedItem is not TerminalTheme selected)
            return;

        // Restore the original built-in theme and rebuild the editor grid.
        var original = BuiltInThemes.All.FirstOrDefault(t => t.Name == selected.Name);
        if (original is not null)
        {
            AppSettings.Instance.ThemeName = original.Name;
            RebuildColorGrid(original);
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        // Export Custom if the user has been editing; otherwise export the
        // selected built-in.
        var theme = AppSettings.Instance.ThemeName == "Custom"
            ? BuiltInThemes.Custom
            : ThemeList.SelectedItem as TerminalTheme;
        if (theme is null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(theme, VexJsonContext.Default.TerminalTheme);
            Clipboard.SetText(json);
        }
        catch { /* clipboard may be locked; ignore */ }
    }

    /// <summary>Shallow-copies all themeable color fields from
    /// <paramref name="source"/> into <paramref name="target"/>. Used to seed
    /// <see cref="BuiltInThemes.Custom"/> from a built-in without mutating the
    /// original.</summary>
    private static void CopyInto(TerminalTheme source, TerminalTheme target)
    {
        target.Name = "Custom";
        target.Background = source.Background;
        target.Foreground = source.Foreground;
        target.Cursor = source.Cursor;
        target.SelectionBackground = source.SelectionBackground;
        target.Black = source.Black;
        target.Red = source.Red;
        target.Green = source.Green;
        target.Yellow = source.Yellow;
        target.Blue = source.Blue;
        target.Magenta = source.Magenta;
        target.Cyan = source.Cyan;
        target.White = source.White;
        target.BrightBlack = source.BrightBlack;
        target.BrightRed = source.BrightRed;
        target.BrightGreen = source.BrightGreen;
        target.BrightYellow = source.BrightYellow;
        target.BrightBlue = source.BrightBlue;
        target.BrightMagenta = source.BrightMagenta;
        target.BrightCyan = source.BrightCyan;
        target.BrightWhite = source.BrightWhite;
    }
}

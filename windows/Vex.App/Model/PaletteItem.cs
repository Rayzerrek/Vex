using System;

namespace Vex.App.Model;

public sealed record PaletteItem(string Title, string? Subtitle, Action Action, string? Category = null);

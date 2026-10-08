using System.Text.Json.Serialization;

namespace Vex.App.Model;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(AppSnapshot))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(TerminalTheme))]
[JsonSerializable(typeof(SavedTerminalLayout))]
internal partial class VexJsonContext : JsonSerializerContext
{
}

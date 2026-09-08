using System.Text.Json.Serialization;

namespace Vex.App.Model;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(AppSnapshot))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(TerminalTheme))]
internal partial class VexJsonContext : JsonSerializerContext
{
}

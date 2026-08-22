using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Vex.App.Model;

/// <summary>
/// Loads and registers syntax-highlighting <see cref="IHighlightingDefinition"/> instances
/// lazily from the embedded XSHD resources on demand, choosing the One Dark set for the
/// dark appearance and One Light for light so code stays readable in both.
/// </summary>
internal static class EditorHighlighting
{
    private const string ResourcePrefix = "Vex.App.Highlighting.";

    /// <summary>Resource prefix for the active appearance; flipped when the
    /// app switches between dark and light chrome.</summary>
    internal static string ThemePrefix { get; private set; } = "OneDark";

    internal static void SetAppearance(bool dark)
        => ThemePrefix = dark ? "OneDark" : "OneLight";

    /// <summary>Drops cached definitions so the next lookup loads the set for
    /// the current appearance (definitions are immutable once loaded).</summary>
    internal static void ResetCache() => Definitions.Clear();

    // ConcurrentDictionary forbids null values, so the cache holds a sentinel
    // for "resource missing" instead of null.
    private static readonly ConcurrentDictionary<string, IHighlightingDefinition> Definitions = new();

    /// <summary>
    /// Returns the syntax-highlighting <see cref="IHighlightingDefinition"/> most appropriate for
    /// <paramref name="fileExtension"/> (lower-case, with leading dot) in the active appearance,
    /// or <c>null</c> if no mapping exists.
    /// </summary>
    internal static IHighlightingDefinition? ForExtension(string fileExtension) =>
        fileExtension switch
        {
            // JavaScript / TypeScript family
            ".js" or ".jsx" or ".mjs" or ".cjs"
                or ".ts" or ".tsx" or ".mts" or ".cts"
                => Get("JavaScript"),

            // JSON
            ".json" or ".jsonc"
                => Get("JSON"),

            // C#
            ".cs" or ".csx"
                => Get("CSharp"),

            // C / C++
            ".c" or ".h" or ".cpp" or ".cc" or ".cxx"
                or ".hpp" or ".hxx" or ".hh" or ".inl"
                => Get("Cpp"),

            // Go — mapped to C++ rules (braces, similar keywords)
            ".go" => Get("Cpp"),

            // Rust
            ".rs" => Get("Cpp"),

            // Swift
            ".swift" => Get("Cpp"),

            // Kotlin — Java-like; fall back to CSharp rules
            ".kt" or ".kts" => Get("CSharp"),

            // Python
            ".py" or ".pyw" or ".pyi"
                => Get("Python"),

            // Shell scripts
            ".sh" or ".bash" or ".zsh" or ".fish" or ".nu"
                or ".dockerfile" or ".containerfile"
                => Get("Shell"),

            // XML / HTML / config dialects
            ".xml" or ".xaml" or ".xsl" or ".xslt" or ".html" or ".htm" or ".svg"
                or ".yml" or ".yaml" or ".toml" or ".md" or ".markdown"
                => Get("XML"),

            _ => null,
        };

    private static IHighlightingDefinition? Get(string name)
    {
        // GetOrAdd runs the factory once per key even under concurrency; the
        // sentinel (a zero-cost singleton) marks a missing resource.
        var definition = Definitions.GetOrAdd($"{ThemePrefix}:{name}", static fullKey =>
        {
            // The appearance prefix rides inside the cache key, so flipping
            // dark/light lazily loads the other set while keeping both.
            var sep = fullKey.IndexOf(':');
            var resourceName = $"{ResourcePrefix}{fullKey[..sep]}-{fullKey[(sep + 1)..]}.xshd";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null)
                return MissingDefinition.Instance;

            using var reader = XmlReader.Create(stream);
            var loaded = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(fullKey, null, loaded);
            return loaded;
        });
        return ReferenceEquals(definition, MissingDefinition.Instance) ? null : definition;
    }

    private sealed class MissingDefinition : IHighlightingDefinition
    {
        public static readonly MissingDefinition Instance = new();
        public string Name => "";
        public HighlightingRuleSet? MainRuleSet => null;
        public IEnumerable<HighlightingColor> NamedHighlightingColors => [];
        public IDictionary<string, string> Properties => new Dictionary<string, string>();
        public HighlightingColor? GetNamedColor(string name) => null;
        public HighlightingRuleSet? GetNamedRuleSet(string name) => null;
    }
}

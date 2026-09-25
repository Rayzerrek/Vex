using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Vex.App.Model;

/// <summary>
/// Loads and registers syntax-highlighting <see cref="IHighlightingDefinition"/> instances
/// lazily from the embedded XSHD resources on demand, choosing the One Dark set for a
/// dark surface and One Light for a light one so code stays readable on both.
/// </summary>
internal static class EditorHighlighting
{
    private const string ResourcePrefix = "Vex.App.Highlighting.";

    // ConcurrentDictionary forbids null values, so the cache holds a sentinel
    // for "resource missing" instead of null.
    private static readonly ConcurrentDictionary<string, IHighlightingDefinition> Definitions = new();

    /// <summary>
    /// Returns the syntax-highlighting <see cref="IHighlightingDefinition"/> most appropriate
    /// for <paramref name="fileExtension"/> (lower-case, with leading dot) on a surface that is
    /// <paramref name="dark"/>, or <c>null</c> if no mapping exists. The variant is an argument
    /// rather than global state: a definition cached behind a stale variant painted dark-surface
    /// colors onto a light editor (washed-out "ghost" code) after a theme flip.
    /// </summary>
    internal static IHighlightingDefinition? ForExtension(string fileExtension, bool dark)
    {
        var variant = dark ? "OneDark" : "OneLight";
        return fileExtension switch
        {
            // JavaScript / TypeScript family
            ".js" or ".jsx" or ".mjs" or ".cjs"
                or ".ts" or ".tsx" or ".mts" or ".cts"
                => Get(variant, "JavaScript"),

            // JSON
            ".json" or ".jsonc"
                => Get(variant, "JSON"),

            // C#
            ".cs" or ".csx"
                => Get(variant, "CSharp"),

            // C / C++
            ".c" or ".h" or ".cpp" or ".cc" or ".cxx"
                or ".hpp" or ".hxx" or ".hh" or ".inl"
                => Get(variant, "Cpp"),

            // Go — mapped to C++ rules (braces, similar keywords)
            ".go" => Get(variant, "Cpp"),

            // Rust
            ".rs" => Get(variant, "Cpp"),

            // Swift
            ".swift" => Get(variant, "Cpp"),

            // Kotlin — Java-like; fall back to CSharp rules
            ".kt" or ".kts" => Get(variant, "CSharp"),

            // Python
            ".py" or ".pyw" or ".pyi"
                => Get(variant, "Python"),

            // Shell scripts
            ".sh" or ".bash" or ".zsh" or ".fish" or ".nu"
                or ".dockerfile" or ".containerfile"
                => Get(variant, "Shell"),

            // XML / HTML / config dialects
            ".xml" or ".xaml" or ".xsl" or ".xslt" or ".html" or ".htm" or ".svg"
                or ".yml" or ".yaml" or ".toml" or ".md" or ".markdown"
                => Get(variant, "XML"),

            _ => null,
        };
    }

    private static IHighlightingDefinition? Get(string variant, string name)
    {
        // GetOrAdd runs the factory once per key even under concurrency; the
        // sentinel (a zero-cost singleton) marks a missing resource. Both
        // variants stay cached under their own key, so flipping dark/light
        // loads the other set and flipping back reuses the first one.
        var definition = Definitions.GetOrAdd($"{variant}:{name}", static fullKey =>
        {
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

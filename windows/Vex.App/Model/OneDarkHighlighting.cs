using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Vex.App.Model;

/// <summary>
/// Loads and registers One Dark <see cref="IHighlightingDefinition"/> instances lazily from
/// the embedded XSHD resources on demand.
/// </summary>
internal static class OneDarkHighlighting
{
    private const string ResourcePrefix = "Vex.App.Highlighting.";
    // ConcurrentDictionary forbids null values, so the cache holds a sentinel
    // for "resource missing" instead of null.
    private static readonly ConcurrentDictionary<string, IHighlightingDefinition> Definitions = new();

    /// <summary>
    /// Returns the One Dark <see cref="IHighlightingDefinition"/> most appropriate for
    /// <paramref name="fileExtension"/> (lower-case, with leading dot), or <c>null</c>
    /// if no mapping exists.
    /// </summary>
    internal static IHighlightingDefinition? ForExtension(string fileExtension) =>
        fileExtension switch
        {
            // JavaScript / TypeScript family
            ".js" or ".jsx" or ".mjs" or ".cjs"
                or ".ts" or ".tsx" or ".mts" or ".cts"
                => Get("OneDark-JavaScript"),

            // JSON
            ".json" or ".jsonc"
                => Get("OneDark-JSON"),

            // C#
            ".cs" or ".csx"
                => Get("OneDark-CSharp"),

            // C / C++
            ".c" or ".h" or ".cpp" or ".cc" or ".cxx"
                or ".hpp" or ".hxx" or ".hh" or ".inl"
                => Get("OneDark-Cpp"),

            // Go — mapped to C++ rules (braces, similar keywords)
            ".go" => Get("OneDark-Cpp"),

            // Rust
            ".rs" => Get("OneDark-Cpp"),

            // Swift
            ".swift" => Get("OneDark-Cpp"),

            // Kotlin — Java-like; fall back to CSharp rules
            ".kt" or ".kts" => Get("OneDark-CSharp"),

            // Python
            ".py" or ".pyw" or ".pyi"
                => Get("OneDark-Python"),

            // Shell scripts
            ".sh" or ".bash" or ".zsh" or ".fish" or ".nu"
                or ".dockerfile" or ".containerfile"
                => Get("OneDark-Shell"),

            // XML / HTML / config dialects
            ".xml" or ".xaml" or ".xsl" or ".xslt" or ".html" or ".htm" or ".svg"
                or ".yml" or ".yaml" or ".toml" or ".md" or ".markdown"
                => Get("OneDark-XML"),

            _ => null,
        };

    private static IHighlightingDefinition? Get(string name)
    {
        // GetOrAdd runs the factory once per key even under concurrency; the
        // sentinel (a zero-cost singleton) marks a missing resource.
        var definition = Definitions.GetOrAdd(name, static key =>
        {
            var resourceName = $"{ResourcePrefix}{key}.xshd";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null)
                return MissingDefinition.Instance;

            using var reader = XmlReader.Create(stream);
            var loaded = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(key, null, loaded);
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

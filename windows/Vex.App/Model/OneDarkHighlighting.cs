using System.IO;
using System.Reflection;
using System.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Vex.App.Model;

/// <summary>
/// Loads and registers One Dark <see cref="IHighlightingDefinition"/> instances from
/// the embedded XSHD resources at application startup.
/// </summary>
internal static class OneDarkHighlighting
{
    private const string ResourcePrefix = "Vex.App.Highlighting.";

    private static int _registered;

    // Names that match the x:Key in each XSHD <SyntaxDefinition name="…">
    private static readonly string[] DefinitionNames =
    [
        "OneDark-JavaScript",
        "OneDark-CSharp",
        "OneDark-Python",
        "OneDark-Cpp",
        "OneDark-JSON",
        "OneDark-XML",
        "OneDark-Shell",
    ];

    /// <summary>
    /// Call once before any <see cref="EditorPane"/> is created to register all
    /// One Dark definitions with <see cref="HighlightingManager"/>. Idempotent;
    /// also invoked lazily from <see cref="Get"/> so startup never pays for
    /// parsing the XSHD files until an editor is actually shown.
    /// </summary>
    internal static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            return;

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in DefinitionNames)
        {
            var resourceName = $"{ResourcePrefix}{name}.xshd";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                continue;

            using var reader = new XmlTextReader(stream);
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(name, null, definition);
        }
    }

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
        Register();
        return HighlightingManager.Instance.GetDefinition(name);
    }
}

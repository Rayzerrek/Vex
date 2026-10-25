import { Link } from "@tanstack/react-router";
import { DownloadSimpleIcon } from "@phosphor-icons/react/dist/csr/DownloadSimple";
import { ArrowUpRightIcon } from "@phosphor-icons/react/dist/csr/ArrowUpRight";
import { docsPath, guidePath, repository } from "../routes";
const features = [
  [
    "workspace",
    "Space for every process",
    "Keep your shell, editor, and dev server side by side. Split panes, switch tabs, or focus on one terminal.",
    "Ctrl+Shift+R / D"
  ],
  [
    "shortcuts",
    "Stay at the keyboard",
    "Find a file, insert its path, and keep working. Thoughtful shortcuts put your workspace within reach.",
    "Ctrl+Shift+F"
  ],
  [
    "program-status",
    "Know when you’re needed",
    "Program-reported status shows which terminal is working, finished, or waiting for your input.",
    "Ctrl+Shift+Space"
  ]
];

export function Home() {
  return (
    <main id="main-content" tabIndex={-1} className="home-layout page-width">
      <section
        className="hero page-enter py-16 sm:py-24"
        aria-labelledby="hero-title"
      >
        <p className="mb-6 text-nav text-soft">
          A native terminal workspace for Windows
        </p>
        <h1
          id="hero-title"
          className="max-w-3xl text-[clamp(3rem,7vw,5rem)] leading-[1.05] tracking-[-0.055em]"
        >
          Room for
          <br />
          the real work.
        </h1>
        <p className="mt-7 max-w-lg text-lg leading-relaxed text-soft">
          Your shells. Your tools. A little less in between. Tabs, split panes,
          and thoughtful keyboard controls in a genuinely native Windows app.
        </p>
        <div className="mt-8 flex flex-wrap gap-3">
          <a className="button button-primary" href="#download">
            <DownloadSimpleIcon aria-hidden="true" />
            Get Vex for Windows
          </a>
          <Link className="button button-secondary" to={docsPath}>
            Read the docs
          </Link>
        </div>
        <p className="mt-5 text-xs text-soft">
          Windows 10 & 11 · x64 · Free and open source
        </p>
      </section>

      <section
        id="features"
        aria-label="Why Vex"
        className="border-y border-rule py-3"
      >
        {features.map(([slug, title, text, keys]) => (
          <div
            key={slug}
            className="grid gap-3 py-7 sm:grid-cols-[1fr_2fr] sm:gap-10"
          >
            <Link
              className="text-lg tracking-tight hover:underline"
              to={guidePath(slug)}
            >
              {title}
            </Link>
            <div>
              <p className="max-w-lg text-body text-soft">{text}</p>
              <kbd className="mt-3 inline-block font-mono text-xs text-soft">
                {keys}
              </kbd>
            </div>
          </div>
        ))}
      </section>

      <section
        className="grid gap-6 py-16 sm:grid-cols-[1fr_2fr] sm:gap-10"
        aria-labelledby="native-title"
      >
        <h2 id="native-title" className="text-title">
          Native by design.
        </h2>
        <div className="max-w-lg space-y-4 text-body text-soft">
          <p>
            Written in Zig. Win32 for the window, ConPTY for your processes,
            Ghostty VT for terminal emulation, and D3D12 for rendering. No
            Electron or .NET runtime.
          </p>
          <p>
            Bring PowerShell, Nushell, cmd, Git Bash, or WSL. Use the defaults,
            or make Vex yours with a small Lua configuration file.
          </p>
          <Link className="text-link" to={guidePath("configuration")}>
            Configure Vex <ArrowUpRightIcon aria-hidden="true" />
          </Link>
        </div>
      </section>

      <section
        id="download"
        className="grid gap-6 border-t border-rule py-16 sm:grid-cols-[1fr_2fr] sm:gap-10"
        aria-labelledby="download-title"
      >
        <h2 id="download-title" className="text-title">
          Get Vex.
        </h2>
        <div className="max-w-lg">
          <p className="mb-5 text-body text-soft">
            Windows 10 (1809+) and Windows 11. No account or runtime to install.
          </p>
          <a
            className="button button-primary"
            href={`${repository}/releases/latest`}
          >
            <DownloadSimpleIcon aria-hidden="true" />
            Download from GitHub
          </a>
          <p className="mt-4 text-nav leading-relaxed text-soft">
            Choose the x64 .msi installer or portable .zip archive. SHA-256
            checksums are available alongside each release.
          </p>
          <Link
            className="text-link mt-4"
            to={guidePath("getting-started")}
            hash="build-from-source"
          >
            Or build from source <ArrowUpRightIcon aria-hidden="true" />
          </Link>
        </div>
      </section>
    </main>
  );
}

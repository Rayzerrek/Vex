import { Icon } from "../components/icons";
import { useScrollReveal } from "../components/use-scroll-reveal";

const repository = "https://github.com/Rayzerrek/Vex";
const features = [
  {
    title: "A place for every process.",
    text: "Keep a shell, a dev server, and your editor side by side. Split in either direction, resize your panes, or focus on just one. Your layout stays out of your way.",
    href: "/docs/workspace#split-panes",
    link: "Tabs and split panes",
    keys: "Ctrl+Shift+R / D",
    action: "Split right or down"
  },
  {
    title: "Find the path. Keep the context.",
    text: "Search files and folders from the terminal cursor. Navigate into a directory, choose a match, and insert its path without running a command or leaving your application.",
    href: "/docs/shortcuts#path-picker",
    link: "Path completion",
    keys: "Ctrl+Shift+F",
    action: "Insert a file or folder path"
  },
  {
    title: "Know when you’re needed.",
    text: "Program-reported status brings work, completion, and requests for input to your pane headers and tabs. Close protection gives active programs a second look before you quit.",
    href: "/docs/program-status",
    link: "Status and attention",
    keys: "Ctrl+Shift+Space",
    action: "See all your tabs"
  }
];

export function Home() {
  const scrollRef = useScrollReveal();
  return (
    <main ref={scrollRef} id="main-content" className="home-layout">
      <section className="hero page-width" aria-labelledby="hero-title">
        <p className="platform-note">
          <span className="platform-symbol" aria-hidden="true">
            ⊞
          </span>{" "}
          A native terminal workspace for Windows
        </p>
        <h1 id="hero-title">
          <span className="hero-line">
            <span>Room for</span>
          </span>
          <br />
          <span className="hero-line">
            <span>the real work.</span>
          </span>
        </h1>
        <div className="hero-bottom">
          <p>
            Your shells. Your tools. A little less in between. Vex brings tabs,
            split panes, and thoughtful keyboard controls to a genuinely native
            Windows app.
          </p>
          <div className="hero-actions">
            <a className="button button-primary" href="#download">
              <Icon name="download" /> Get Vex for Windows
            </a>
            <a className="button button-secondary" href="/docs/getting-started">
              Start with the docs
            </a>
          </div>
        </div>
        <div className="hero-footnote">
          <span>Windows 10 & 11, x64</span>
          <span>Free software. Open source.</span>
        </div>
      </section>

      <section
        className="features-section page-width"
        id="features"
        aria-labelledby="features-title"
      >
        <div className="section-intro" data-scroll-reveal>
          <h2 id="features-title">
            Less managing.
            <br />
            More doing.
          </h2>
          <p>
            A workspace around your terminal, not another layer between you and
            it.
          </p>
        </div>
        <div className="feature-list">
          {features.map((feature) => (
            <div className="feature-row" key={feature.title}>
              <h3>{feature.title}</h3>
              <div className="feature-body">
                <p>{feature.text}</p>
                <a className="text-link" href={feature.href}>
                  {feature.link}
                  <Icon name="external" />
                </a>
              </div>
              <div className="feature-shortcut">
                <kbd>{feature.keys}</kbd>
                <span>{feature.action}</span>
              </div>
            </div>
          ))}
        </div>
      </section>

      <section className="native-section" aria-labelledby="native-title">
        <div className="page-width native-inner" data-scroll-reveal>
          <div>
            <h2 id="native-title">
              Native isn’t
              <br />
              an afterthought.
            </h2>
            <p>Written in Zig. At home on Windows.</p>
          </div>
          <div className="native-facts">
            <div>
              <h3>Windows all the way down.</h3>
              <p>
                Win32 for the window. ConPTY for your processes. Ghostty VT for
                terminal emulation. An instanced D3D12 renderer for the screen.
                No Electron or .NET runtime.
              </p>
            </div>
            <div>
              <h3>Your shell comes with you.</h3>
              <p>
                PowerShell, Nushell, cmd, Git Bash, or WSL. Add custom Windows
                and Linux shells without changing your profile files.
              </p>
            </div>
            <div>
              <h3>Simple defaults. Lua when you want it.</h3>
              <p>
                Change your theme and preferences in Settings, or make them your
                own in init.lua. Reload the configuration without restarting
                your terminals.
              </p>
            </div>
          </div>
        </div>
      </section>

      <section
        className="docs-invitation page-width"
        aria-labelledby="guides-title"
        data-scroll-reveal
      >
        <div>
          <h2 id="guides-title">
            A good place
            <br />
            to get started.
          </h2>
          <p>
            No setup ritual. Open Vex and work, then learn the details at your
            own pace.
          </p>
          <a className="text-link" href="/docs/getting-started">
            Explore the documentation
            <Icon name="external" />
          </a>
        </div>
        <nav className="guide-links" aria-label="Explore the guides">
          {[
            [
              "getting-started",
              "Install Vex",
              "Your first prompt, without the prerequisites."
            ],
            [
              "workspace",
              "Make room",
              "Tabs, splits, focus, and a view of everything."
            ],
            [
              "configuration",
              "Make it yours",
              "A small Lua file. Clear, documented settings."
            ],
            [
              "shells",
              "Bring your shell",
              "Windows profiles, WSL, and custom programs."
            ]
          ].map(([slug, title, detail]) => (
            <a key={slug} href={`/docs/${slug}`}>
              <span>
                <strong>{title}</strong>
                <small>{detail}</small>
              </span>
              <Icon name="external" />
            </a>
          ))}
        </nav>
      </section>

      <section
        className="download-section page-width"
        id="download"
        aria-labelledby="download-title"
      >
        <div>
          <h2 id="download-title">
            Your next
            <br />
            working space.
          </h2>
          <p>
            For Windows 10 (1809+) and Windows 11.
            <br />
            No account. No runtime to install.
          </p>
          <a className="text-link" href={`${repository}/blob/main/LICENSE`}>
            Free under GPL-3.0
          </a>
        </div>
        <div className="download-options">
          <a className="download-option" href={`${repository}/releases/latest`}>
            <Icon name="download" />
            <span>
              <strong>Windows installer</strong>
              <small>Choose the native x64 .msi release.</small>
            </span>
            <Icon name="external" />
          </a>
          <a className="download-option" href={`${repository}/releases/latest`}>
            <Icon name="download" />
            <span>
              <strong>Portable archive</strong>
              <small>Unpack the native x64 .zip. Run Vex.exe.</small>
            </span>
            <Icon name="external" />
          </a>
          <p className="download-note">
            Both links open GitHub Releases, where you can choose a package and
            verify its SHA-256 checksum.
          </p>
          <a
            className="text-link"
            href="/docs/getting-started#build-from-source"
          >
            Or build it yourself
          </a>
        </div>
      </section>
    </main>
  );
}

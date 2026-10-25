import type { ReactNode } from "react";
import { CodeBlock } from "./code-block";

type DocPage = {
  slug: string;
  title: string;
  group: "Get started" | "Customize" | "Reference";
  description: string;
  sections: { id: string; title: string }[];
};
type PageContent = Omit<DocPage, "sections"> & {
  lead: ReactNode;
  sections: { id: string; title: string; body: ReactNode }[];
};

function Key({ children }: { children: string }) {
  return <kbd className="key-combo">{children}</kbd>;
}
function Note({ children }: { children: ReactNode }) {
  return <aside className="doc-callout">{children}</aside>;
}
function DocTable({
  caption,
  headers,
  rows
}: {
  caption: string;
  headers: string[];
  rows: ReactNode[][];
}) {
  return (
    <div
      className="doc-table-wrap"
      tabIndex={0}
      role="region"
      aria-label={caption}
    >
      <table className="doc-table">
        <caption>{caption}</caption>
        <thead>
          <tr>
            {headers.map((header) => (
              <th key={header} scope="col">
                {header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) => (
            <tr key={i}>
              {row.map((cell, j) =>
                j === 0 ? (
                  <th key={j} scope="row">
                    {cell}
                  </th>
                ) : (
                  <td key={j}>{cell}</td>
                )
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
function Shortcuts({
  caption,
  rows
}: {
  caption: string;
  rows: [string, string][];
}) {
  return (
    <DocTable
      caption={caption}
      headers={["Action", "Keys"]}
      rows={rows.map(([action, keys]) => [
        action,
        <Key key={action}>{keys}</Key>
      ])}
    />
  );
}

const configExample = `-- %LOCALAPPDATA%\\Vex\\init.lua
vex.config.theme = "Vex Obsidian"
vex.config.appearance = "theme" -- or "dark" / "light"
vex.config.font_family = "Cascadia Mono"
vex.config.font_size = 14
vex.config.cursor_blink = true
vex.config.copy_on_select = true
vex.config.confirm_close = true
vex.config.default_shell = "startup"`;

const pages: PageContent[] = [
  {
    slug: "getting-started",
    title: "Installation",
    group: "Get started",
    description:
      "A native Windows terminal. Download, unpack, and get to work.",
    lead: "Vex brings tabs, split panes, and your choice of shell into a native Zig and Win32 application. You don’t need an account, Zig, or a .NET runtime to use it.",
    sections: [
      {
        id: "requirements",
        title: "Requirements",
        body: (
          <>
            <p>
              Windows 10 version 1809 or later, or Windows 11, on an x64
              machine. Shell availability depends on what is installed on your
              computer; WSL profiles also require a working WSL installation.
            </p>
            <p>
              Vex uses ConPTY, Ghostty VT, and a D3D12 renderer. There is no
              WPF, CLR, or Electron runtime.
            </p>
          </>
        )
      },
      {
        id: "download",
        title: "Download and install",
        body: (
          <>
            <p>
              Open{" "}
              <a href="https://github.com/Rayzerrek/Vex/releases/latest">
                GitHub Releases
              </a>{" "}
              and choose a native Windows x64 package. SHA-256 checksums are
              published alongside the downloads.
            </p>
            <DocTable
              caption="Native release packages"
              headers={["Package", "Installation"]}
              rows={[
                [<code key="msi">.msi</code>, "Run the Windows installer."],
                [
                  <code key="zip">Vex-Native-&lt;version&gt;-win-x64.zip</code>,
                  <>
                    Extract the archive to a folder you own and run{" "}
                    <code>Vex.exe</code>.
                  </>
                ]
              ]}
            />
            <p>
              Vex 2.0 replaces the WPF application. Older packages named{" "}
              <code>Vex-&lt;version&gt;</code> use <code>Vex.App.exe</code>.
              Back up old settings before upgrading: JSON preferences and
              sessions are not imported. See{" "}
              <a href="/vex/docs/workspace#persistence">what is saved</a>.
            </p>
            <Note>
              <p>
                <strong>Keep the extracted files together.</strong> The package
                includes <code>vex-native.exe</code>,{" "}
                <code>ghostty-vt.dll</code>, <code>conpty.dll</code>,{" "}
                <code>OpenConsole.exe</code>, and supporting assets. Copying
                just the launcher is not enough.
              </p>
            </Note>
          </>
        )
      },
      {
        id: "first-workspace",
        title: "Your first terminal",
        body: (
          <>
            <ol>
              <li>Launch Vex. Use your shell as you normally would.</li>
              <li>
                Open the command palette with <Key>Ctrl+Shift+P</Key>. Choose{" "}
                <strong>Settings: Open Preferences...</strong> to adjust your
                default shell and font.
              </li>
              <li>
                Open a tab with <Key>Ctrl+Shift+T</Key>. Split right with{" "}
                <Key>Ctrl+Shift+R</Key> or down with <Key>Ctrl+Shift+D</Key>.
              </li>
            </ol>
            <p>
              Try <Key>Ctrl+Shift+M</Key> for Theme Studio, or continue to the{" "}
              <a href="/vex/docs/workspace">workspace guide</a>. Configuration
              is optional; Vex runs with defaults when no Lua file exists.
            </p>
          </>
        )
      },
      {
        id: "build-from-source",
        title: "Build from source",
        body: (
          <>
            <p>
              Install <strong>Zig 0.15.2</strong>, clone the{" "}
              <a href="https://github.com/Rayzerrek/Vex">repository</a>, and run
              on Windows:
            </p>
            <CodeBlock language="PowerShell">
              {"cd app\nzig build\nzig build test\nzig build run"}
            </CodeBlock>
            <p>Linux and WSL can cross-compile using the pinned toolchain:</p>
            <CodeBlock language="Shell">
              {
                "nix develop\ncd app\nzig build\nzig build test-build\npython3 -m unittest discover -s tests -p 'test_*.py' -v\npython3 tools/package.py"
              }
            </CodeBlock>
            <p>
              Cross-compilation is not a Windows test pass. Run the compiled
              tests with their adjacent DLLs and{" "}
              <code>vex-conpty-fixture.exe</code> on Windows. The Python
              packager produces a portable ZIP; MSI packaging uses the Windows{" "}
              <code>app/tools/build-installer.ps1</code> script and its WiX/.NET
              build tools.
            </p>
            <p>
              See{" "}
              <a href="https://github.com/Rayzerrek/Vex/blob/main/app/README.md">
                the application README
              </a>{" "}
              for packaging and desktop verification details.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "workspace",
    title: "Workspace",
    group: "Get started",
    description:
      "Tabs for context. Split panes for the things you want side by side.",
    lead: "A Vex window owns its tabs directly. Each tab contains a terminal or a tree of split panes, with one active pane receiving keyboard input.",
    sections: [
      {
        id: "tabs",
        title: "Working with tabs",
        body: (
          <>
            <p>
              Create a tab with <Key>Ctrl+Shift+T</Key>. Switch with{" "}
              <Key>Ctrl+Tab</Key> and <Key>Ctrl+Shift+Tab</Key>. A window
              supports up to eight tabs.
            </p>
            <p>
              Drag tabs to reorder them. Double-click a tab to rename it, or
              choose <strong>Tab: Rename Active Tab</strong> in the command
              palette. Use <strong>Terminal: New Tab with Shell...</strong> to
              choose a different shell for a new tab.
            </p>
            <p>
              Middle-click a tab to close it. <Key>Ctrl+Shift+W</Key> closes the
              active split pane, or its tab if there are no splits. The final
              tab cannot be closed through the normal close-tab action.
            </p>
          </>
        )
      },
      {
        id: "split-panes",
        title: "Split panes and focus",
        body: (
          <>
            <p>
              Split right with <Key>Ctrl+Shift+R</Key> or down with{" "}
              <Key>Ctrl+Shift+D</Key>. A new split starts in the active pane’s
              current directory. Drag dividers to change the space each pane
              receives.
            </p>
            <p>
              The command palette also offers{" "}
              <strong>Pane: Split Right with Shell...</strong> and{" "}
              <strong>Pane: Split Down with Shell...</strong>. Shell preferences
              affect new processes, not the terminals already running.
            </p>
            <p>
              Choose <strong>Pane: Toggle Focus / Zoom Mode</strong> to show
              only the active pane. Toggle it again to return to the split
              layout. Splitting or closing a pane leaves focus mode.
            </p>
          </>
        )
      },
      {
        id: "tab-overview",
        title: "Tab overview",
        body: (
          <>
            <p>
              Press <Key>Ctrl+Shift+Space</Key> to see all tabs together,
              including their split panes. These are terminal frames, not window
              captures; opening the overview does not resize your terminal
              applications.
            </p>
            <Shortcuts
              caption="Overview controls"
              rows={[
                ["Choose a tab", "Arrow keys / Tab / Shift+Tab"],
                ["First or last tab", "Home / End"],
                ["Switch to selected tab", "Enter"],
                ["Close selected tab", "Delete"],
                ["Close its active pane", "Ctrl+Shift+W"],
                ["Return without switching", "Esc"]
              ]}
            />
            <p>
              You can also click a card to switch, or its close control to close
              a tab or pane. Closing keeps the overview open and uses the same
              active-program protection as the rest of Vex.
            </p>
          </>
        )
      },
      {
        id: "closing",
        title: "Closing active programs",
        body: (
          <>
            <p>
              Closing a pane, tab, or the entire window checks for active
              programs. The <strong>Quit Vex?</strong> review lists programs
              across all tabs and splits, including background tabs. Idle shells
              are omitted; unknown or unverified activity stays protected.
            </p>
            <p>
              <strong>Keep Vex open</strong> is the default. Press Escape to
              cancel. Only confirming the close terminates the protected
              processes.
            </p>
            <Note>
              <p>
                Turning off{" "}
                <strong>
                  Settings → General → Confirm before closing active programs
                </strong>{" "}
                disables these warnings immediately. The equivalent Lua
                preference is <code>vex.config.confirm_close = false</code>.
              </p>
            </Note>
          </>
        )
      },
      {
        id: "persistence",
        title: "What is saved",
        body: (
          <>
            <p>
              Vex saves preferences and custom shell profiles in{" "}
              <a href="/vex/docs/customize/config#storage">
                Lua configuration files
              </a>
              . The current native application does not provide project
              grouping, saved layout templates, or session restoration.
            </p>
            <p>
              Closing Vex ends its terminal processes. It does not save running
              programs or reopen the old tab and split layout on the next
              launch. Legacy <code>settings.json</code> and{" "}
              <code>session.json</code> files are not the native configuration
              API.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "configuration",
    title: "Configuration",
    group: "Customize",
    description:
      "Good defaults, a small Lua file, and changes you can reload in place.",
    lead: "You can use Vex without writing a configuration file. For a setup you want to keep in code, assign preferences through vex.config in init.lua.",
    sections: [
      {
        id: "storage",
        title: "Configuration file",
        body: (
          <>
            <p>
              The default path is <code>%LOCALAPPDATA%\Vex\init.lua</code>. Set{" "}
              <code>VEX_CONFIG</code> to an absolute file path before launching
              Vex to use another location.
            </p>
            <p>
              A missing file is normal: Vex uses defaults without a warning.
              Nothing is created until you save preferences. Legacy JSON
              settings are no longer read.
            </p>
            <CodeBlock language="Lua">{configExample}</CodeBlock>
            <Note>
              <p>
                <strong>Saving through Settings replaces init.lua.</strong> The
                UI first saves the exact previous source to{" "}
                <code>init.backup.lua</code>, then writes preferences
                atomically. The new file does not preserve comments, functions
                or module imports. This is one rotating backup, not a history;
                keep a separate copy of hand-written Lua. Identical saves leave
                the backup untouched. Adding a shell uses a separate file.
              </p>
            </Note>
          </>
        )
      },
      {
        id: "settings-reference",
        title: "Settings reference",
        body: (
          <>
            <p>
              All keys below are properties of <code>vex.config</code>. Unknown
              keys and invalid values are errors, rather than silently ignored
              preferences.
            </p>
            <DocTable
              caption="vex.config properties"
              headers={["Property", "Default", "Accepted value"]}
              rows={[
                [
                  <code key="theme">theme</code>,
                  "Vex Obsidian",
                  "An exact built-in theme name from Theme Studio."
                ],
                [
                  <code key="appearance">appearance</code>,
                  "theme",
                  "theme (infer from the selected theme), dark, or light."
                ],
                [
                  <code key="family">font_family</code>,
                  "Cascadia Mono",
                  "A nonempty font family name."
                ],
                [
                  <code key="size">font_size</code>,
                  "14",
                  "An integer from 9 to 32."
                ],
                [
                  <code key="blink">cursor_blink</code>,
                  "true",
                  "Boolean; enable cursor blinking."
                ],
                [
                  <code key="copy">copy_on_select</code>,
                  "true",
                  "Boolean; copy and clear mouse selections on release."
                ],
                [
                  <code key="close">confirm_close</code>,
                  "true",
                  "Boolean; confirm before closing active programs."
                ],
                [
                  <code key="shell">default_shell</code>,
                  "startup",
                  <>
                    A{" "}
                    <a href="/vex/docs/customize/shells#default-shell">
                      built-in shell ID
                    </a>{" "}
                    or saved reference such as <code>custom:256</code>.
                  </>
                ]
              ]}
            />
            <p>
              Explicit dark/light appearance takes precedence. If the selected
              theme belongs to the opposite collection, Vex uses Vex Obsidian or
              Vex Alabaster respectively. UI saves include the current mode.
              Shell changes apply to new tabs and splits, not existing
              processes.
            </p>
          </>
        )
      },
      {
        id: "reload",
        title: "Reload without restarting",
        body: (
          <>
            <p>
              Open <Key>Ctrl+Shift+P</Key> and choose{" "}
              <strong>Configuration: Reload init.lua</strong>. The action
              reloads both preferences and the custom shell catalog.
            </p>
            <p>
              A failed reload keeps the previous valid settings and reports the
              Lua source and line in a nonblocking notification. A startup error
              uses defaults instead. Vex never rewrites a file to repair an
              error. Failed saves or backups roll live preferences back without
              closing sessions; the previous source remains available for
              recovery.
            </p>
            <p>
              There is no background Lua execution or file polling.
              Configuration runs once per load; the Lua VM is destroyed before
              the preferences are applied.
            </p>
          </>
        )
      },
      {
        id: "lua-modules",
        title: "Lua modules and limits",
        body: (
          <>
            <p>
              Ordinary expressions, functions, loops, and Lua modules work. For
              example, <code>require("appearance")</code> loads{" "}
              <code>appearance.lua</code> beside your configuration file.
            </p>
            <p>
              Lua 5.4.8 is linked into the app. Base, table, string, math,
              UTF-8, and Lua <code>require</code> are available. Process, IO,
              debug, coroutine, native-module, dynamic-loader, and
              protected-call APIs are intentionally unavailable.
            </p>
            <DocTable
              caption="Configuration evaluation limits"
              headers={["Resource", "Limit"]}
              rows={[
                ["Source file", "64 KiB"],
                ["VM memory", "8 MiB"],
                [
                  "Execution per evaluation, including modules",
                  "Approximately one million instructions"
                ]
              ]}
            />
            <Note>
              <p>
                Configuration is trusted local code, not a security sandbox for
                untrusted scripts. Modules are not rewritten by Settings.
              </p>
            </Note>
          </>
        )
      },
      {
        id: "reset",
        title: "Reset preferences safely",
        body: (
          <>
            <p>
              Close Vex and rename <code>init.lua</code> to keep a backup. If
              you use <code>VEX_CONFIG</code>, rename that file instead. The
              next launch uses defaults. To recover the previous source instead,
              copy <code>init.backup.lua</code> over <code>init.lua</code> while
              Vex is closed. With <code>VEX_CONFIG</code>, the backup is{" "}
              <code>&lt;name&gt;.backup.lua</code> beside your chosen file.
            </p>
            <p>
              Custom profiles live in the adjacent <code>init.shells.lua</code>{" "}
              file, or <code>&lt;name&gt;.shells.lua</code> for a custom
              configuration path. Leave it in place to retain your profiles;
              back it up separately if you also want to reset them.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "appearance",
    title: "Appearance",
    group: "Customize",
    description:
      "Theme the whole window, choose your font, and tune the cursor.",
    lead: "Vex derives its window chrome from the terminal theme. Tabs, pane headers, overlays, and terminal colors belong to the same palette.",
    sections: [
      {
        id: "theme-studio",
        title: "Theme Studio",
        body: (
          <>
            <p>
              Open Theme Studio with <Key>Ctrl+Shift+M</Key>, or choose{" "}
              <strong>Color Theme: Open Theme Studio...</strong> in the command
              palette. Use Tab inside Studio to toggle between dark and light
              mode.
            </p>
            <DocTable
              caption="Built-in themes"
              headers={["Mode", "Themes"]}
              rows={[
                [
                  "Dark",
                  "Vex Obsidian, Vex Tidal, Vex Grove, Vex Dusk, Vex Ember, Vex Glacier"
                ],
                [
                  "Light",
                  "Vex Alabaster, Vex Linen, Vex Mist, Vex Petal, Vex Apricot, Vex Sage"
                ]
              ]}
            />
            <p>To select a theme in Lua, use its exact name:</p>
            <CodeBlock language="Lua">
              {'vex.config.theme = "Vex Tidal"'}
            </CodeBlock>
          </>
        )
      },
      {
        id: "terminal-colors",
        title: "Terminal application colors",
        body: (
          <>
            <p>
              Changing the host theme refreshes idle terminal colors and clears
              application palette overrides. Vex answers color-scheme queries
              and notifies applications subscribed to mode 2031. Applications
              must support this protocol and allow automatic theme selection;
              Vex does not edit their configuration files.
            </p>
            <p>
              For applications that keep an opposite light/dark palette, Vex can
              adapt displayed RGB colors when a dominant neutral background is
              detected. It preserves syntax hues and relative surface levels.
              Matching palettes, isolated accents, saturated backgrounds and
              frames without a majority surface are left alone.
            </p>
            <p>
              A further contrast pass improves low-contrast text toward a 4.5:1
              ratio, excluding identical colors and block/Powerline graphics.
              These are rendering adjustments: original terminal colors remain
              intact, so switching back is reversible, including tab previews.
            </p>
          </>
        )
      },
      {
        id: "font-and-cursor",
        title: "Font and cursor",
        body: (
          <>
            <p>
              The default terminal font is <strong>Cascadia Mono</strong> at
              size 14. Choose a font in Settings, or set{" "}
              <code>font_family</code> and an integer <code>font_size</code>{" "}
              from 9 to 32 in Lua.
            </p>
            <Shortcuts
              caption="Font controls"
              rows={[
                ["Increase font size", "Ctrl++"],
                ["Decrease font size", "Ctrl+-"],
                ["Reset font size", "Ctrl+0"]
              ]}
            />
            <p>
              Cursor blinking is on by default. Use{" "}
              <strong>Cursor: Toggle Blinking</strong> in the command palette,
              or set <code>vex.config.cursor_blink = false</code>.
            </p>
          </>
        )
      },
      {
        id: "motion",
        title: "Motion and system preferences",
        body: (
          <>
            <p>
              Tab interactions and the tab overview respect Windows’ animation
              preference. Transitions settle instead of keeping an idle render
              loop running, and opening the overview leaves PTY sizes unchanged.
            </p>
            <p>
              The website’s light and dark switch controls only this website. It
              does not change the theme in your installed Vex application.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "shells",
    title: "Shell profiles",
    group: "Customize",
    description:
      "Use a built-in shell or bring your own, on Windows and through WSL.",
    lead: "A shell profile describes what to launch. You can choose a different shell for a new tab or split without changing the sessions you already have open.",
    sections: [
      {
        id: "default-shell",
        title: "Choose a default shell",
        body: (
          <>
            <p>
              Use Settings or <strong>Shell: Change Default Shell...</strong> in
              the command palette. In Lua, set{" "}
              <code>vex.config.default_shell</code> to one of these IDs:
            </p>
            <DocTable
              caption="Built-in shell IDs"
              headers={["ID", "Shell"]}
              rows={[
                ["startup", "Respect the CLI launch choice (the default)."],
                ["cmd", "Command Prompt"],
                ["powershell", "Windows PowerShell"],
                ["pwsh", "PowerShell 7"],
                ["nu", "Nushell"],
                ["gitbash", "Git Bash"],
                ["wsl", "Windows Subsystem for Linux"]
              ]}
            />
            <p>
              Available shells depend on the local installation. A saved custom
              profile can be selected with a reference such as{" "}
              <code>custom:256</code>.
            </p>
          </>
        )
      },
      {
        id: "custom-shells",
        title: "Add a custom shell",
        body: (
          <>
            <ol>
              <li>
                Open <strong>Settings → General → Add custom shell…</strong>.
              </li>
              <li>
                Enter a name. Browse for an executable, or enter a command on
                PATH such as <code>fish.exe</code>.
              </li>
              <li>
                Put launch arguments in the separate field, then save.
                Executable paths with spaces do not need quotes.
              </li>
            </ol>
            <p>
              The shell picker launches existing profiles; it does not contain
              an add action. Vex passes arguments directly to your program
              without guessing flags or changing its user profile.
            </p>
            <p>
              Validation errors stay in the editor without discarding your
              fields. A missing program reports an error without terminating
              existing terminals. An unavailable custom startup default falls
              back to the system shell.
            </p>
          </>
        )
      },
      {
        id: "wsl",
        title: "Custom Linux shells in WSL",
        body: (
          <>
            <p>
              To launch a Linux shell in the default WSL distribution, use a
              program such as <code>wsl:/usr/bin/fish</code> and separate
              arguments such as <code>-l</code>.
            </p>
            <p>
              This selects the WSL backend and its directory handling, rather
              than trying to run a Linux path as a Windows executable. WSL
              checks that the executable exists when the session starts.
            </p>
            <p>
              Close protection probes the pane’s Linux foreground process group.
              Unknown, failed, or stale results stay protected; Vex does not
              assume that every <code>wsl.exe</code> process is an idle shell.
            </p>
          </>
        )
      },
      {
        id: "profile-storage",
        title: "Profile storage",
        body: (
          <>
            <p>
              Settings manages profiles separately in{" "}
              <code>init.shells.lua</code> beside <code>init.lua</code>. With{" "}
              <code>VEX_CONFIG</code>, the companion is named{" "}
              <code>&lt;name&gt;.shells.lua</code>. Adding a profile never
              rewrites your hand-written preferences.
            </p>
            <p>The managed format uses bounded Lua:</p>
            <CodeBlock language="Lua">
              {
                'vex.add_shell(256, "My shell", "C:\\\\Tools\\\\shell.exe", "--interactive")'
              }
            </CodeBlock>
            <p>
              IDs and default selections survive restarts. Settings rereads the
              file before appending to preserve external profile changes, but
              regenerates its contents: comments and expressions in the managed
              file are not preserved.
            </p>
            <p>
              The dialog supports up to 56 custom profiles. The profile file has
              the same 64 KiB source and Lua evaluation limits as preferences.
              Reloading a broken profile file retains the last valid catalog.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "shortcuts",
    title: "Keyboard shortcuts",
    group: "Reference",
    description:
      "Workspace actions, terminal selection, and path completion at your fingertips.",
    lead: (
      <>
        Plain Ctrl bindings generally belong to your shell or TUI. Vex reserves
        Ctrl+Shift for workspace actions, with a few explicit exceptions for tab
        navigation, preferences, and font zoom.
      </>
    ),
    sections: [
      {
        id: "workspace-keys",
        title: "Workspace keys",
        body: (
          <>
            <Shortcuts
              caption="Workspace and overlays"
              rows={[
                ["Command palette", "Ctrl+Shift+P"],
                ["Theme Studio", "Ctrl+Shift+M"],
                ["Tab overview", "Ctrl+Shift+Space"],
                ["Insert a file or folder path", "Ctrl+Shift+F"],
                ["New tab", "Ctrl+Shift+T"],
                ["Close active pane or tab", "Ctrl+Shift+W"],
                ["Next tab", "Ctrl+Tab"],
                ["Previous tab", "Ctrl+Shift+Tab"],
                ["Split right", "Ctrl+Shift+R"],
                ["Split down", "Ctrl+Shift+D"],
                ["Preferences", "Ctrl+,"],
                ["Font size", "Ctrl++ / Ctrl+- / Ctrl+0"],
                ["Close overlay", "Esc"]
              ]}
            />
            <p>
              <Key>Ctrl+S</Key> goes to your terminal application, including its
              save binding or terminal flow control. Workspace actions remain
              available inside full-screen TUIs.
            </p>
          </>
        )
      },
      {
        id: "path-picker",
        title: "Path picker",
        body: (
          <>
            <p>
              Press <Key>Ctrl+Shift+F</Key> at the terminal cursor. Type to
              fuzzy-search files and folders below the current directory.
              Indexing runs recursively on a background worker; queries match
              the retained index. Inserting a path does not execute the command.
            </p>
            <Shortcuts
              caption="Path navigation"
              rows={[
                ["Select a match", "Up / Down / PageUp / PageDown"],
                ["Insert the selected path", "Enter"],
                ["Insert without shell quoting", "Ctrl+Enter"],
                ["Enter a directory or insert a file", "Tab"],
                ["Enter the selected directory", "Right"],
                ["Parent directory, keep query", "Shift+Tab / Alt+Left"],
                ["Parent when query is empty", "Backspace"],
                ["Clear query", "Ctrl+Backspace"],
                ["Paste query or directory", "Ctrl+Shift+V"],
                ["Close", "Esc / Ctrl+Shift+F"]
              ]}
            />
            <p>
              Type a directory prefix followed by <code>/</code> or{" "}
              <code>\</code> to navigate. Absolute paths, <code>..</code>, and{" "}
              <code>~/</code> work. Entering a folder starts a fresh query;
              moving up retains it. Double-click enters a directory or inserts a
              file.
            </p>
            <p>
              Paths use the shell’s quoting and drive syntax, with bracketed
              paste when supported. Full-screen applications receive raw paths.
              The starting directory comes from the last valid OSC 7 directory
              report, otherwise the pane’s launch directory. Vex does not infer
              directories from prompt text.
            </p>
            <Note>
              <p>
                <strong>Bounded recursive search.</strong> Scans stop at 16,384
                entries or 8 MiB of indexed names. Build output and dependency
                folders are not excluded, so narrow the directory if results are
                truncated. Indexing uses Windows filesystem access, including
                paths exposed by the pane’s WSL distribution; it does not browse
                an SSH host’s filesystem.
              </p>
            </Note>
          </>
        )
      },
      {
        id: "terminal-keys",
        title: "Terminal keys",
        body: (
          <>
            <Shortcuts
              caption="Selection and scrollback"
              rows={[
                ["Copy selection", "Ctrl+C / Ctrl+Shift+C"],
                ["Paste", "Ctrl+Shift+V"],
                ["Cut eligible selected input", "Ctrl+Shift+X"],
                ["Delete eligible selected input", "Backspace"],
                ["Extend selection", "Shift+Arrow / Shift+Home / Shift+End"],
                ["Extend by word", "Ctrl+Shift+Arrow"],
                ["Scroll to top or bottom", "Ctrl+Home / Ctrl+End"],
                ["Page up or down", "Shift+PageUp / Shift+PageDown"],
                ["Delete previous word", "Ctrl+Backspace"]
              ]}
            />
            <p>
              <Key>Ctrl+C</Key> copies when text is selected; otherwise it sends
              the interrupt your shell expects. <Key>Ctrl+Backspace</Key> sends{" "}
              <code>0x17</code>, used by readline and PSReadLine for
              backward-kill-word.
            </p>
            <p>
              Selection and scrollback navigation apply on the primary screen.
              Full-screen applications own their arrows and paging keys.
              Ctrl+Shift copy, paste, and cut remain available.
            </p>
            <p>
              Backspace and cut can delete a keyboard selection ending at the
              live shell cursor within one logical line, including soft-wrapped
              rows. Selections in scrollback, across hard newlines, or beyond
              the cursor do not turn terminal output into editable text.
            </p>
          </>
        )
      },
      {
        id: "copy-feedback",
        title: "Clipboard and copy feedback",
        body: (
          <>
            <p>
              Copies highlight the visible source with a brief sweep. Cuts,
              whitespace-only copies and disabled system animations do not
              animate. Application-managed copies can also animate when Vex can
              match unchanged source text in the focused pane; detection is
              best-effort, not proof that an application copied rather than cut.
            </p>
            <p>
              OSC 52 and OSC 1337 clipboard writes are supported without a
              separate confirmation. Output from remote commands can therefore
              replace clipboard text. Clipboard read/query access is not
              enabled.
            </p>
          </>
        )
      },
      {
        id: "mouse",
        title: "Mouse controls",
        body: (
          <>
            <DocTable
              caption="Mouse actions"
              headers={["Input", "Action"]}
              rows={[
                ["Drag", "Select text."],
                ["Double-click / triple-click", "Select a word / line."],
                ["Ctrl+Click", "Open the detected URL."],
                [
                  "Middle-click a tab",
                  "Close that tab, subject to close protection."
                ],
                ["Drag a divider", "Resize split panes."]
              ]}
            />
            <p>
              Mouse selections copy and clear on release by default. Disable{" "}
              <strong>Copy on select</strong> in Settings, or set{" "}
              <code>vex.config.copy_on_select = false</code>, to keep the
              clipboard unchanged until you explicitly copy. Keyboard selections
              always require explicit copying.
            </p>
            <p>
              When a TUI requests mouse reporting, clicks, drags, and wheel
              input go to it in its negotiated format. Hold Shift to use Vex’s
              local mouse handling instead.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "command-line",
    title: "Command line",
    group: "Reference",
    description: "Choose a shell or launch a custom program when starting Vex.",
    lead: "Launch options select the startup shell. They do not provide a remote-control server or a scripting API for an already running window.",
    sections: [
      {
        id: "shell",
        title: "Choose a shell",
        body: (
          <>
            <p>From the directory containing the native executable:</p>
            <CodeBlock language="PowerShell">
              {
                ".\\vex-native.exe --shell pwsh\n.\\vex-native.exe --shell cmd\n.\\vex-native.exe --shell nu\n.\\vex-native.exe --shell gitbash\n.\\vex-native.exe --shell wsl"
              }
            </CodeBlock>
            <p>
              The default preference{" "}
              <code>vex.config.default_shell = "startup"</code> respects this
              launch choice. Set a specific shell in{" "}
              <a href="/vex/docs/customize/shells#default-shell">
                configuration
              </a>{" "}
              when you want a persistent default for new tabs and splits.
            </p>
          </>
        )
      },
      {
        id: "program",
        title: "Launch a custom program",
        body: (
          <>
            <p>
              Use an absolute executable path and keep the Windows argument
              string separate:
            </p>
            <CodeBlock language="PowerShell">
              {
                '.\\vex-native.exe --program "C:\\Windows\\System32\\cmd.exe" --arguments "/d"'
              }
            </CodeBlock>
            <p>
              For reusable launches, add a{" "}
              <a href="/vex/docs/customize/shells#custom-shells">
                custom shell profile
              </a>{" "}
              in Settings instead.
            </p>
          </>
        )
      },
      {
        id: "wsl-launches",
        title: "Choose a WSL distribution",
        body: (
          <>
            <p>
              Use <code>--distribution</code> with a WSL launch to select an
              installed distribution. An explicit Linux executable goes after
              <code> wsl:</code> in the shell argument:
            </p>
            <CodeBlock language="PowerShell">
              {
                '.\\vex-native.exe --shell wsl --distribution "Ubuntu"\n.\\vex-native.exe --shell wsl:/usr/bin/fish --distribution "Debian" --arguments "-l"'
              }
            </CodeBlock>
            <p>
              The Linux program path must be absolute. With WSL, launch
              arguments require an explicit program; plain{" "}
              <code>--shell wsl</code> uses the distribution’s default shell.{" "}
              <code>--distribution</code> is not accepted for Windows shell
              launches.
            </p>
          </>
        )
      },
      {
        id: "development",
        title: "Pass options through Zig",
        body: (
          <>
            <p>
              When developing on Windows, pass launch arguments after{" "}
              <code>--</code>:
            </p>
            <CodeBlock language="PowerShell">
              {"cd app\nzig build run -- --shell pwsh"}
            </CodeBlock>
            <p>
              See{" "}
              <a href="/vex/docs/installation#build-from-source">
                build from source
              </a>{" "}
              for the pinned compiler and verification commands.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "program-status",
    title: "Program status",
    group: "Reference",
    description:
      "Understand work, completion, and requests for input without checking every pane.",
    lead: "Vex interprets shell markers and explicit program reports. Pane headers, tabs, and the overview surface activity so you can see where your attention is needed.",
    sections: [
      {
        id: "status-indicators",
        title: "Reading status",
        body: (
          <>
            <DocTable
              caption="Reported states"
              headers={["State", "Meaning"]}
              rows={[
                ["Working", "A shell or application reports active work."],
                [
                  "Needs input",
                  "An application reports that it is blocked on input."
                ],
                ["Finished", "A completion result is available."],
                ["Failed", "A failure result is available."]
              ]}
            />
            <p>
              A program may supply a title, message, and progress. Status can be
              aggregated across split panes into their tab. These reports
              describe application state; they are separate from merely entering
              a full-screen terminal mode.
            </p>
          </>
        )
      },
      {
        id: "shell-integration",
        title: "Shell integration",
        body: (
          <>
            <p>
              CMD, PowerShell, and Nushell integration is embedded in the
              application. No ShellIntegration folder is required beside the
              executable, and your user profile files are not modified.
            </p>
            <p>
              Other shells, including Git Bash, WSL, and custom programs, can
              run without the built-in integration. Command, prompt, and
              directory tracking depend on those shells emitting the supported
              OSC sequences.
            </p>
            <Note>
              <p>
                <strong>Detailed status requires cooperation.</strong> Vex does
                not infer an approval request just because an assistant or
                another TUI is open. The application must report that it needs
                input.
              </p>
            </Note>
          </>
        )
      },
      {
        id: "program-reports",
        title: "Program reports",
        body: (
          <>
            <p>
              Vex supports OSC 7501 program status reports and capability
              queries, as well as OSC 133 shell markers. OSC 9;4 progress is
              also interpreted by the terminal.
            </p>
            <p>
              For the wire format and integration guidance, see the{" "}
              <a href="https://www.superlogical.com/rex/docs/build/program-status">
                Program Status Protocol specification
              </a>
              . This is protocol interoperability, not support for Rex’s server,
              plugin, or Lua event APIs.
            </p>
            <p>
              <a href="/vex/docs/workspace#closing">Close protection</a> also
              uses process information. A missing status report is not proof
              that a process is safe to terminate; uncertain activity remains
              protected.
            </p>
          </>
        )
      }
    ]
  }
];

// Navigation and article anchors share one source of truth.
// eslint-disable-next-line react-refresh/only-export-components -- Shared static document metadata.
export const docsPages: DocPage[] = pages.map(
  ({ slug, title, group, description, sections }) => ({
    slug,
    title,
    group,
    description,
    sections: sections.map(({ id, title: sectionTitle }) => ({
      id,
      title: sectionTitle
    }))
  })
);

export function DocContent({ slug }: { slug: string }) {
  const page = pages.find((candidate) => candidate.slug === slug);
  if (!page)
    return (
      <p>
        Guide not found. Start with{" "}
        <a href="/vex/docs/installation">installation</a>.
      </p>
    );
  return (
    <>
      <p className="doc-lead">{page.lead}</p>
      {page.sections.map(({ id, title, body }) => (
        <section
          className="doc-section"
          id={id}
          key={id}
          aria-labelledby={`${id}-heading`}
        >
          <h2 id={`${id}-heading`}>
            <a className="heading-anchor" href={`#${id}`}>
              {title}
              <span aria-hidden="true">#</span>
            </a>
          </h2>
          {body}
        </section>
      ))}
    </>
  );
}

import type { ReactNode } from "react";

type DocPage = {
  slug: string;
  title: string;
  group: "Get started" | "Workspace" | "Reference";
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
    <div className="doc-table-wrap">
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
          {rows.map((row, rowIndex) => (
            <tr key={rowIndex}>
              {row.map((cell, cellIndex) =>
                cellIndex === 0 ? (
                  <th key={cellIndex} scope="row">
                    {cell}
                  </th>
                ) : (
                  <td key={cellIndex}>{cell}</td>
                )
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ShortcutTable({
  caption,
  rows
}: {
  caption: string;
  rows: [string, string[]][];
}) {
  return (
    <DocTable
      caption={caption}
      headers={["Action", "Keys"]}
      rows={rows.map(([action, keys]) => [
        action,
        <span className="key-list" key={action}>
          {keys.map((key, index) => (
            <span key={key}>
              {index > 0 && " or "}
              <Key>{key}</Key>
            </span>
          ))}
        </span>
      ])}
    />
  );
}

function CodeBlock({
  language,
  children
}: {
  language: string;
  children: string;
}) {
  return (
    <pre className="doc-code">
      <code data-language={language}>{children}</code>
    </pre>
  );
}

const settingsExample = JSON.stringify(
  {
    Appearance: "Dark",
    ThemeName: "One Dark",
    FontFamily: "Cascadia Mono",
    FontSize: 14,
    CursorBlink: true,
    ShellId: "custom-debian",
    CustomShells: [
      {
        Id: "custom-debian",
        Name: "Debian (WSL)",
        Program: "C:\\Windows\\System32\\wsl.exe",
        Arguments: "-d Debian"
      }
    ]
  },
  null,
  2
);

const pages: PageContent[] = [
  {
    slug: "getting-started",
    title: "Installation",
    group: "Get started",
    description:
      "Install Vex on Windows, choose a shell, and open your first workspace.",
    lead: "Vex is a native Windows terminal workspace. It brings ConPTY sessions, projects, tabs, and split panes into one WPF application.",
    sections: [
      {
        id: "requirements",
        title: "Requirements",
        body: (
          <>
            <p>
              Vex runs on Windows 10 version 1809 or later, and Windows 11.
              Release downloads target Windows x64.
            </p>
            <p>
              The portable release is self-contained: you do not need to install
              .NET to run it. The .NET 10 SDK is needed if you build Vex from
              source.
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
              Choose an archive from the{" "}
              <a href="https://github.com/Rayzerrek/Vex/releases">
                GitHub releases page
              </a>
              .
            </p>
            <DocTable
              caption="Windows release downloads"
              headers={["Package", "How to use it"]}
              rows={[
                [
                  <code key="msi">Vex-&lt;version&gt;-win-x64.msi</code>,
                  "Run the installer. It adds Vex to the Start menu and creates a desktop shortcut."
                ],
                [
                  <code key="zip">Vex-&lt;version&gt;-win-x64.zip</code>,
                  <>
                    Unpack the archive and run <code>Vex.App.exe</code>.
                  </>
                ]
              ]}
            />
            <aside className="doc-callout">
              <p>
                <strong>Keep the portable files together.</strong> The
                executable loads <code>ghostty-vt.dll</code> and native WPF
                libraries from the same directory. Copying only the executable
                is not enough.
              </p>
            </aside>
          </>
        )
      },
      {
        id: "first-workspace",
        title: "Open your first workspace",
        body: (
          <>
            <ol className="doc-steps">
              <li>
                Launch Vex. Open the command palette with{" "}
                <Key>Ctrl+Shift+P</Key>, choose <strong>New Project</strong>,
                and select your working directory.
              </li>
              <li>
                Use <strong>Settings</strong> in the palette to choose a shell.
                Vex detects installed PowerShell 7, Nushell, Windows PowerShell,
                Command Prompt, Git Bash, and WSL profiles.
              </li>
              <li>
                Open another tab with <Key>Ctrl+Shift+T</Key>, or split the
                current tab right with <Key>Ctrl+Shift+R</Key> and down with{" "}
                <Key>Ctrl+Shift+D</Key>.
              </li>
            </ol>
            <p>
              Plain Ctrl shortcuts stay with your shell and terminal
              applications. Start with the{" "}
              <a href="/docs/shortcuts">shortcut reference</a>, then see{" "}
              <a href="/docs/workspace">how projects and panes fit together</a>.
            </p>
            <p>
              On first run, the app appearance follows the Windows app theme.
              Use <Key>Ctrl+Shift+M</Key> to preview terminal themes for that
              appearance.
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
              With Zig 0.15.2 installed on Windows, run these commands from the{" "}
              <a href="https://github.com/Rayzerrek/Vex">repository</a> root:
            </p>
            <CodeBlock language="powershell">
              {"cd app\nzig build\nzig build test\nzig build run"}
            </CodeBlock>
            <p>
              From the repository root, the Windows packaging script produces a
              ZIP and MSI in <code>app/publish/</code> (Python and .NET/WiX
              required for packaging):
            </p>
            <CodeBlock language="powershell">
              {"pwsh app/tools/build-installer.ps1"}
            </CodeBlock>
            <p>
              Artifact names use the version in <code>app/VERSION</code>.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "workspace",
    title: "Workspace",
    group: "Workspace",
    description:
      "Organize projects, tabs, and panes; save layouts and understand session restoration.",
    lead: "A project holds your working directory and tabs. Each tab contains a terminal pane or a split tree of panes, with its own focused pane.",
    sections: [
      {
        id: "projects-and-tabs",
        title: "Projects and tabs",
        body: (
          <>
            <p>
              Keep several projects open and switch between them without closing
              their terminals. Vex detects Git branches for project directories.
            </p>
            <p>
              Use <strong>New Project</strong> or{" "}
              <strong>Switch Project...</strong> in the command palette. Within
              a project, create a tab with <Key>Ctrl+Shift+T</Key> and move
              between tabs with <Key>Ctrl+Tab</Key> or <Key>Ctrl+Shift+Tab</Key>
              .
            </p>
            <p>
              Tabs support drag reordering and custom titles. Press{" "}
              <Key>Ctrl+Shift+Space</Key> for tab peek, a visual overview.
              Middle-click a tab to close it.
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
              <Key>Ctrl+Shift+D</Key>. Drag a pane title bar toward an edge to
              place it in a split; a preview shows the destination. Divider
              ratios are part of the saved layout.
            </p>
            <p>
              Choose <strong>Toggle Focus Mode</strong> in the command palette
              to show only the active pane without changing the underlying split
              tree. Toggle it again to return to the full layout.
            </p>
            <p>
              <Key>Ctrl+Shift+W</Key> closes a pane or tab. Small status glyphs
              on panes, tabs, and project rows help you spot work that needs
              attention; see <a href="/docs/program-status">program status</a>{" "}
              for their meaning.
            </p>
          </>
        )
      },
      {
        id: "saved-layouts",
        title: "Saved layouts",
        body: (
          <>
            <ol className="doc-steps">
              <li>
                Arrange your panes, then right-click the tab and choose{" "}
                <strong>Save layout…</strong>.
              </li>
              <li>
                Give each pane a directory and, optionally, a startup command.
                Relative directories resolve against the project where you open
                the layout.
              </li>
              <li>
                Open it from <strong>Saved layouts</strong> in the tab menu, or
                search for its name in the command palette. It opens in a new
                tab.
              </li>
            </ol>
            <p>
              Use <strong>Edit layout…</strong> in the same menu to change or
              delete a template. Vex validates every directory before adding the
              new tab and lets you repair missing paths in the layout editor.
              You can save up to 32 templates with up to 32 panes each.
            </p>
            <aside className="doc-callout">
              <p>
                <strong>A template is a launch action.</strong> Saving or
                editing never executes commands. Opening a template explicitly
                starts its commands once in new shells; ordinary session
                restoration does not replay them.
              </p>
            </aside>
            <p>
              Templates live in <code>SavedLayouts</code> in{" "}
              <a href="/docs/configuration#settings-reference">settings.json</a>
              , separate from the current session.
            </p>
          </>
        )
      },
      {
        id: "session-restore",
        title: "Session restoration",
        body: (
          <>
            <p>
              Vex records open projects, tabs, split trees, divider ratios, and
              focus in <code>session.json</code>. The file is saved when the
              layout changes and on exit.
            </p>
            <p>
              On restart, a new shell starts in each pane’s recorded working
              directory. Running commands and program state are not resumed.
              Projects whose recorded directories no longer exist are dropped.
            </p>
            <p>
              Older sessions containing editor panes restore those panes as
              terminals in the project directory, preserving the split layout
              and focus. Referenced files are not opened or modified.
            </p>
            <p>
              To clear the restored workspace, close Vex and remove only{" "}
              <code>session.json</code>. Your settings and saved templates stay
              in <code>settings.json</code>. See{" "}
              <a href="/docs/configuration#storage">configuration storage</a>{" "}
              for the paths.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "shortcuts",
    title: "Keyboard shortcuts",
    group: "Workspace",
    description:
      "Keyboard and mouse controls for the workspace, terminal, and path picker.",
    lead: (
      <>
        Plain <code>Ctrl+key</code> belongs to the terminal. Vex uses{" "}
        <code>Ctrl+Shift+key</code> for workspace actions, including while
        full-screen terminal applications are running.
      </>
    ),
    sections: [
      {
        id: "workspace-keys",
        title: "Workspace keys",
        body: (
          <>
            <ShortcutTable
              caption="Workspace and overlays"
              rows={[
                ["Command palette", ["Ctrl+Shift+P"]],
                ["Theme picker", ["Ctrl+Shift+M"]],
                ["Tab peek", ["Ctrl+Shift+Space"]],
                ["Insert file or directory path", ["Ctrl+Shift+F"]],
                ["New tab", ["Ctrl+Shift+T"]],
                ["Close pane or tab", ["Ctrl+Shift+W"]],
                ["Next tab", ["Ctrl+Tab"]],
                ["Previous tab", ["Ctrl+Shift+Tab"]],
                ["Split right", ["Ctrl+Shift+R"]],
                ["Split down", ["Ctrl+Shift+D"]],
                ["Close overlay or settings", ["Esc"]]
              ]}
            />
            <p>
              <Key>Ctrl+Tab</Key> is the tab-navigation exception.{" "}
              <Key>Ctrl+S</Key> goes to the terminal or TUI, including its save
              binding or terminal flow control.
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
              <Key>Ctrl+Shift+F</Key> opens a compact picker at the terminal
              cursor, including inside Claude and full-screen applications. Type
              to fuzzy-search files and folders directly in the current
              directory.
            </p>
            <ShortcutTable
              caption="Path picker navigation and insertion"
              rows={[
                ["Select match", ["Up", "Down", "PageUp", "PageDown"]],
                ["Insert selected path without executing", ["Enter"]],
                ["Insert a raw path without shell quoting", ["Ctrl+Enter"]],
                ["Enter selected directory or insert selected file", ["Tab"]],
                ["Enter selected directory", ["Right"]],
                [
                  "Move to parent, keeping the query",
                  ["Shift+Tab", "Alt+Left", "Backspace (empty query)"]
                ],
                ["Clear query", ["Ctrl+Backspace"]],
                ["Paste query or directory path", ["Ctrl+Shift+V"]],
                ["Close picker", ["Esc", "Ctrl+Shift+F"]]
              ]}
            />
            <p>
              The mouse wheel changes the selected match. Double-click enters a
              directory or inserts a file. Entering a folder with <Key>Tab</Key>{" "}
              or <Key>Right</Key> starts a fresh query; moving up keeps the
              query.
            </p>
            <p>
              Type a directory prefix followed by <code>/</code> or{" "}
              <code>\</code> to navigate. Absolute paths, <code>..</code>, and{" "}
              <code>~/</code> are supported. Insertions use absolute paths,
              shell-specific quoting and drive syntax, and bracketed paste when
              supported. Alternate-screen applications receive raw paths.
            </p>
            <aside className="doc-callout">
              <p>
                <strong>The search is local, not recursive.</strong> Subfolders
                are scanned only when you enter them. WSL and Git Bash can
                insert paths to Windows files, but the index searches the
                Windows filesystem, not a remote or Linux filesystem.
              </p>
            </aside>
            <p>
              The starting directory comes from the last local directory
              reported through OSC 7 or a recognized default cmd/PowerShell
              prompt. Otherwise, it uses the pane’s starting directory. Custom
              prompts and remote shells may need manual navigation.
            </p>
            <p>
              The footer shows the current scope, indexing, or a truncated scan.
              Scans are limited to 100,000 immediate entries; each pane caches
              up to four directory snapshots for 30 seconds. Search runs in
              memory, with large lists ranked in the background and no process
              launch per keystroke.
            </p>
          </>
        )
      },
      {
        id: "terminal-keys",
        title: "Terminal keys",
        body: (
          <>
            <ShortcutTable
              caption="Selection, clipboard, and scrollback"
              rows={[
                ["Copy selection", ["Ctrl+C", "Ctrl+Shift+C"]],
                ["Paste", ["Ctrl+Shift+V"]],
                ["Cut selection", ["Ctrl+Shift+X"]],
                ["Delete eligible selected input", ["Backspace"]],
                [
                  "Extend selection",
                  ["Shift+Arrow", "Shift+Home", "Shift+End"]
                ],
                ["Extend selection by word", ["Shift+Ctrl+Arrow"]],
                ["Scroll to top", ["Ctrl+Home"]],
                ["Scroll to bottom", ["Ctrl+End"]],
                ["Page up", ["Shift+PageUp"]],
                ["Page down", ["Shift+PageDown"]],
                ["Delete previous word", ["Ctrl+Backspace"]],
                ["Open detected URL", ["Ctrl+Click"]]
              ]}
            />
            <p>
              Selection and scrollback keys apply on the primary screen.
              Full-screen applications own their arrows, paging, and Shift
              combinations. Copy, paste, and cut remain available through the
              Ctrl+Shift chords.
            </p>
            <p>
              <Key>Ctrl+C</Key> copies only when a selection exists; otherwise
              it sends ETX, the interrupt shells expect.{" "}
              <Key>Ctrl+Backspace</Key> sends <code>0x17</code>, which readline
              and PSReadLine bind to backward-kill-word.
            </p>
            <p>
              <Key>Backspace</Key> can delete a keyboard selection ending at the
              live shell cursor within one logical line, including soft-wrapped
              rows. It sends one backspace per selected grapheme and preserves
              spaces. Scrollback selections, hard newlines, and selections
              beyond the cursor retain ordinary Backspace behavior. Cut follows
              the same deletion rules; terminal output is not editable.
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
              headers={["Action", "Effect"]}
              rows={[
                [
                  "Click",
                  "Place the caret, or report a click to a TUI requesting mouse input."
                ],
                ["Drag", "Select text using Ghostty’s 60% cell threshold."],
                ["Double-click", "Select a word."],
                ["Triple-click", "Select a line."],
                [
                  "Drag pane title bar",
                  "Split toward an edge with a per-pane preview."
                ],
                ["Middle-click tab", "Close the tab."],
                [
                  <Key key="url">Ctrl+Click</Key>,
                  "Open the URL under the pointer."
                ]
              ]}
            />
            <p>
              Mouse selections are copied and cleared on release by default.
              Turn off{" "}
              <strong>Settings → Terminal → Selection → Copy on select</strong>{" "}
              to keep them highlighted until you copy explicitly. This setting
              takes effect immediately and survives restarts. Keyboard
              selections always require explicit copying.
            </p>
            <p>
              When a TUI such as vim, tmux, or htop requests mouse reporting,
              clicks, drags, and wheel events go to that application in its
              negotiated format. Vex’s click-to-position is suppressed until the
              application releases the mouse.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "appearance",
    title: "Appearance",
    group: "Reference",
    description:
      "Choose light or dark appearance, terminal themes, fonts, and cursor behavior.",
    lead: "App appearance controls the window chrome. Terminal themes control the colors inside your panes, with separate sets for light and dark appearance.",
    sections: [
      {
        id: "light-and-dark",
        title: "Light and dark appearance",
        body: (
          <>
            <p>
              On first run, Vex follows the Windows app theme. An empty or
              unrecognized <code>Appearance</code> value also falls back to the
              Windows <code>AppsUseLightTheme</code> preference at startup.
            </p>
            <p>
              Use <strong>Switch to Light Appearance</strong> or{" "}
              <strong>Switch to Dark Appearance</strong> in the command palette
              to change it explicitly. The saved values are <code>"Light"</code>{" "}
              and <code>"Dark"</code>.
            </p>
            <p>
              A theme must belong to the current appearance. A mismatched saved
              theme name falls back to that appearance’s first theme rather than
              changing the app appearance.
            </p>
          </>
        )
      },
      {
        id: "terminal-themes",
        title: "Terminal themes",
        body: (
          <>
            <p>
              Press <Key>Ctrl+Shift+M</Key> to open the theme picker with live
              preview. It offers themes matching the current appearance.
            </p>
            <DocTable
              caption="Built-in terminal themes"
              headers={["Appearance", "Themes"]}
              rows={[
                [
                  "Dark",
                  "Vex Dark, One Dark, Solarized Dark, Catppuccin Mocha, Rosé Pine"
                ],
                [
                  "Light",
                  "Vex Light, One Light, Solarized Light, GitHub Light, Catppuccin Latte, Rosé Pine Dawn"
                ]
              ]}
            />
            <p>
              Each theme defines background, foreground, cursor, selection, and
              the 16 ANSI colors as <code>#RRGGBB</code> values. Vex also has a
              built-in terminal theme editor; <code>ThemeName: "Custom"</code>{" "}
              selects its custom theme.
            </p>
            <p>
              Automatic contrast is enabled by default to repair low-contrast
              terminal text at render time. Disable it when you need exact
              application colors.
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
              The default terminal font is <strong>Cascadia Mono</strong> at 14
              points. Choose any installed font family in Settings. Font sizes
              loaded from settings are clamped to 8–72 points.
            </p>
            <p>
              The terminal cursor blinks by default. Set{" "}
              <code>CursorBlink</code> to <code>false</code> to disable
              blinking; the theme supplies its color.
            </p>
            <p>
              These preferences are stored as <code>FontFamily</code>,{" "}
              <code>FontSize</code>, <code>CursorBlink</code>, and{" "}
              <code>AutomaticContrast</code>. See the{" "}
              <a href="/docs/configuration#settings-reference">
                settings reference
              </a>{" "}
              for their defaults and types.
            </p>
          </>
        )
      }
    ]
  },
  {
    slug: "configuration",
    title: "Configuration",
    group: "Reference",
    description:
      "Settings and session files, shell profile IDs, a JSON example, and reset instructions.",
    lead: (
      <>
        Vex keeps preferences and workspace state in{" "}
        <code>%LocalAppData%\Vex\</code>. Settings and the restored session are
        separate files.
      </>
    ),
    sections: [
      {
        id: "storage",
        title: "Storage and safe editing",
        body: (
          <>
            <DocTable
              caption="Files in %LocalAppData%\\Vex\\"
              headers={["File", "Contents"]}
              rows={[
                [
                  <code key="settings">settings.json</code>,
                  "Appearance, theme, font, cursor, shell profiles, selection preferences, and saved layouts."
                ],
                [
                  <code key="session">session.json</code>,
                  "Open projects, tabs, split trees, divider ratios, and focus."
                ]
              ]}
            />
            <aside className="doc-callout">
              <p>
                <strong>Close Vex before editing.</strong> The app writes these
                files itself. Its debounced save can overwrite changes made
                while it is running. Keep a backup if you want to preserve your
                existing preferences or layouts.
              </p>
            </aside>
            <p>
              Missing files are recreated with defaults on the next start.
              Removing a file resets its contents; it does not uninstall Vex.
            </p>
          </>
        )
      },
      {
        id: "settings-reference",
        title: "Settings reference",
        body: (
          <>
            <DocTable
              caption="settings.json keys"
              headers={["Key", "Type / default", "Meaning"]}
              rows={[
                [
                  <code key="appearance">Appearance</code>,
                  "String; follows Windows initially",
                  <>
                    App chrome: <code>"Dark"</code> or <code>"Light"</code>.
                  </>
                ],
                [
                  <code key="theme">ThemeName</code>,
                  <>
                    String; <code>"Vex Dark"</code>
                  </>,
                  <>
                    A theme in the current appearance, or <code>"Custom"</code>.
                    Light appearance resolves to its own theme set.
                  </>
                ],
                [
                  <code key="font">FontFamily</code>,
                  <>
                    String; <code>"Cascadia Mono"</code>
                  </>,
                  "An installed font family."
                ],
                [
                  <code key="size">FontSize</code>,
                  "Integer; 14",
                  "Terminal font size in points; loaded values are clamped to 8–72."
                ],
                [
                  <code key="blink">CursorBlink</code>,
                  "Boolean; true",
                  "Blink the terminal cursor."
                ],
                [
                  <code key="contrast">AutomaticContrast</code>,
                  "Boolean; true",
                  "Repair low-contrast terminal text at render time."
                ],
                [
                  <code key="copy">CopyOnSelect</code>,
                  "Boolean; true",
                  "Automatically copy and clear mouse selections on release."
                ],
                [
                  <code key="confirm">ConfirmOnExit</code>,
                  "Boolean; true",
                  "Ask for confirmation before closing the window when processes or agents are running."
                ],
                [
                  <code key="shell">ShellId</code>,
                  <>
                    String; <code>"system"</code>
                  </>,
                  "The shell profile ID to launch."
                ],
                [
                  <code key="custom">CustomShells</code>,
                  <>
                    Array; <code>[]</code>
                  </>,
                  "User-defined shell profiles."
                ],
                [
                  <code key="layouts">SavedLayouts</code>,
                  <>
                    Array; <code>[]</code>
                  </>,
                  "Saved pane launch templates, not the active session."
                ]
              ]}
            />
            <p>
              The example below selects a custom Debian WSL profile and the One
              Dark terminal theme. Debian must already be installed in WSL.
              Merge the relevant keys into your existing settings rather than
              discarding saved profiles or layouts.
            </p>
            <CodeBlock language="json">{settingsExample}</CodeBlock>
            <p>
              For theme behavior, see <a href="/docs/appearance">appearance</a>.
              Create and edit pane templates through the{" "}
              <a href="/docs/workspace#saved-layouts">saved layout controls</a>.
            </p>
          </>
        )
      },
      {
        id: "shell-profiles",
        title: "Shell profiles",
        body: (
          <>
            <DocTable
              caption="Detected shell profile IDs"
              headers={["ID", "Shell"]}
              rows={[
                [
                  <code key="system">system</code>,
                  "System default shell (the default selection)"
                ],
                [<code key="pwsh">pwsh</code>, "PowerShell 7"],
                [<code key="nu">nu</code>, "Nushell"],
                [
                  <code key="powershell">powershell</code>,
                  "Windows PowerShell"
                ],
                [<code key="cmd">cmd</code>, "Command Prompt"],
                [<code key="gitbash">gitbash</code>, "Git Bash"],
                [<code key="wsl">wsl</code>, "WSL"]
              ]}
            />
            <p>
              Detection runs once per start and offers installed shells. Git
              Bash is checked in common install locations; Windows PowerShell is
              resolved from System32. If a detected executable is no longer
              available, Vex falls back to the system default.
            </p>
            <p>
              Custom profiles use an <code>Id</code>, a display{" "}
              <code>Name</code>, a <code>Program</code>, and optional{" "}
              <code>Arguments</code>. <code>ShellId</code> must match the
              profile’s <code>Id</code>. Arguments are parsed as a command line,
              including quoting. You can also add custom shells in Settings.
            </p>
            <p>
              Older settings used a display-name <code>Shell</code> key. Vex
              migrates it to <code>ShellId</code> only when no{" "}
              <code>ShellId</code> key exists. The legacy key is no longer used
              for shell selection.
            </p>
          </>
        )
      },
      {
        id: "session-file",
        title: "Session file",
        body: (
          <>
            <p>
              <code>session.json</code> is written when the layout changes and
              on exit. It records what to reopen: projects, their tabs, each
              tab’s split tree, divider ratios, and the focused pane.
            </p>
            <p>
              Restoration starts fresh shells in recorded working directories.
              It does not resume running programs or replay saved template
              commands. A missing project directory causes that project to be
              dropped.
            </p>
            <p>
              Close Vex and delete this file to start with a clean workspace
              while retaining preferences and saved layouts.
            </p>
          </>
        )
      },
      {
        id: "reset",
        title: "Reset to defaults",
        body: (
          <>
            <p>
              To reset both preferences and the restored workspace, close Vex
              and run:
            </p>
            <CodeBlock language="powershell">
              {
                'Remove-Item "$env:LocalAppData\\Vex\\settings.json", "$env:LocalAppData\\Vex\\session.json"'
              }
            </CodeBlock>
            <aside className="doc-callout">
              <p>
                <strong>This removes saved configuration.</strong> Deleting{" "}
                <code>settings.json</code> also removes custom shell profiles
                and saved layout templates. Back up the files first if you may
                want them later. Vex recreates defaults on its next start.
              </p>
            </aside>
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
      "Read work, input, completion, and failure indicators, with shell integration limits.",
    lead: "Pane headers, tabs, project rows, and tab peek show static status glyphs. They let you spot a request for input or a finished command without switching to every pane.",
    sections: [
      {
        id: "status-indicators",
        title: "Status indicators",
        body: (
          <>
            <DocTable
              caption="Program status glyphs"
              headers={["Glyph", "Status", "Meaning"]}
              rows={[
                [
                  <span className="doc-status-glyph" key="working">
                    ◌
                  </span>,
                  "Working",
                  "The shell or program reports work in progress."
                ],
                [
                  <span className="doc-status-glyph" key="input">
                    ?
                  </span>,
                  "Needs input",
                  "The program reports that it is waiting for input."
                ],
                [
                  <span className="doc-status-glyph" key="finished">
                    ✓
                  </span>,
                  "Finished",
                  "A completion result is available."
                ],
                [
                  <span className="doc-status-glyph" key="failed">
                    !
                  </span>,
                  "Failed",
                  "The shell or program reports failure."
                ]
              ]}
            />
            <p>
              Hover an indicator for the program title, message, and progress
              when supplied. Aggregate tooltips identify the source tab and pane
              separately from program-provided text.
            </p>
            <p>
              A blocked pane takes priority over other statuses, including in
              background tabs and projects. Typing in a pane acknowledges
              finished or failed results. A request for input remains until the
              program clears it or returns to the shell prompt.
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
              Built-in PowerShell and Nushell launches provide prompt and
              command markers without editing your profile files. Short
              successful shell commands return to idle; successful commands
              lasting at least two seconds show a completion result.
            </p>
            <p>
              Command Prompt supplies prompt markers only. Vex does not claim
              command-running or error detection for cmd. Other shell profiles
              keep their launch arguments and can supply their own OSC 133
              markers or OSC 7501 reports.
            </p>
            <aside className="doc-callout">
              <p>
                <strong>Detailed status requires a report.</strong> A program
                must emit the status protocol to describe approval requests or
                other input needs. Vex does not infer those requests just
                because a terminal application is open.
              </p>
            </aside>
          </>
        )
      },
      {
        id: "program-reports",
        title: "Program reports and progress",
        body: (
          <>
            <p>
              Vex consumes{" "}
              <a href="https://www.superlogical.com/rex/docs/build/program-status">
                OSC 7501 program status
              </a>{" "}
              reports and capability queries, with bounded hierarchical records
              and messages.
            </p>
            <p>
              OSC 9;4 progress acts as a fallback until a pane receives an
              explicit program status report. These statuses are separate from
              alternate-screen mode: entering a full-screen TUI is not itself a
              status report.
            </p>
            <p>
              Pane header updates are batched at most ten times per second,
              without idle polling or animated status indicators.
            </p>
            <p>
              For the shell selection behind a pane, see{" "}
              <a href="/docs/configuration#shell-profiles">shell profiles</a>.
              For the surfaces that aggregate status, see{" "}
              <a href="/docs/workspace">workspace</a>.
            </p>
          </>
        )
      }
    ]
  }
];

// Derive navigation metadata from the content so section anchors cannot drift.
// oxlint-disable-next-line react/only-export-components -- Navigation intentionally shares article anchors.
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

  if (!page) {
    return (
      <p className="doc-lead">
        This documentation page does not exist. Start with{" "}
        <a href="/docs/getting-started">installation</a>.
      </p>
    );
  }

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
          <h2 id={`${id}-heading`}>{title}</h2>
          {body}
        </section>
      ))}
    </>
  );
}

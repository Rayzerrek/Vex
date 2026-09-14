# Changelog

All notable changes to Vex are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Native mouse input: the terminal reports clicks, drags, and wheel events to
  TUI applications using the tracking mode they negotiated.
- Visual tab overview (tab peek) with live pane previews.
- Pane attention indicators in the tab strip.
- Light appearance with live switching; the app follows the OS setting on
  first run.
- Built-in editor with One Dark and One Light syntax themes for C#, C++, Java,
  JavaScript, JSON, Python, Shell, and XML.
- Command palette, theme picker with live preview, and custom theme editor.

### Changed

- Theme picker now offers only themes matching the current appearance.
- Settings shell selection replaced by named shell profiles with custom
  program and argument support.

### Fixed

- Mouse routing and the encoder now share live DEC mode bits, so an
  application resetting one tracking mode no longer silences every event.
- Fast query responses and ConPTY-stripped DCS filtering.
- Ctrl+Backspace deletes a whole word, matching Windows Terminal.
- Terminal grid resumes correctly after the sidebar animation.
- Tab strip no longer pushes the caption buttons off the window.

### Performance

- Parallel startup with asynchronous ConPTY creation and background settings
  preload.
- Half-debounced file search and thread-safe settings persistence.
- Source-generated regex, frozen lookups, and sealed leaf types.
- Lazy file tree and deferred session start.

## [1.2.1] - 2026-08-18

### Added

- `Vex-1.2.1-win-x64.zip.sha256` checksum alongside the release archive.

### Fixed

- Full-screen terminal applications now receive `Ctrl+Shift` shortcuts instead
  of Vex intercepting them.
- Newly created terminal tabs reliably receive keyboard focus.
- TUI input and focus prioritisation.

### Changed

- The default cursor is a text insertion bar; TUI applications can still
  request their own cursor shape.

## [1.2.0] - 2026-08-15

### Added

- Detected URLs are underlined and open in the browser on `Ctrl+Click`.
- Quick theme switcher with theme-tinted window chrome.
- `Escape` closes settings; clicking empty space releases search focus.

### Performance

- Partial dirty-row redraws, a feed fast path, and cached ASCII strings.
- Deferred editor file reads, search indexing, and syntax highlighting until
  the data is actually needed.

## [1.1.0] - 2026-08-13

### Changed

- Terminal engine migrated from XtermSharp to **libghostty-vt**, Ghostty's
  terminal core. This brings far more compliant VT emulation, real reflow, and
  grapheme cluster support.

### Fixed

- Full-screen backgrounds of TUI applications (nvim, less, btop, tmux, fzf)
  now paint correctly instead of only under text.
- Underline and strikethrough decorations are visible; they were previously
  drawn with a transparent pen.
- Emoji occupy a single column in the grid, matching Ghostty and iTerm2
  semantics.
- Selection uses Ghostty's drag semantics (60% cell threshold, word and line
  gestures).

### Added

- 16 deterministic pixel self-tests covering clear, wide advance, scroll
  snap-back, alternate screen, chunked feeds, caret races, and nvim popups.
- Live nvim scenarios and an output flood stress test.

## [1.0.5] - 2026-08-12

### Added

- Centered settings panel with animated pages, theme swatch cards, toggles,
  and a segmented shell picker.
- Translucent terminal surface over the acrylic backdrop.
- Overlay scrollbar that widens on hover and supports click-to-page.
- Drag-to-split: drag a pane title bar to an edge, with a per-pane preview.
- Session persistence for projects, tabs, the split tree, and divider ratios.

### Performance

- Row-hash scan replaces the scroll-shift redraw path.

### Fixed

- Windows 10 blur-behind fallback, and blur is disabled during move and resize
  to remove drag lag.

## [1.0.0] - 2026-07-28

### Added

- Initial Windows release: ConPTY terminal sessions, tabs, split panes, a file
  tree, fuzzy file search, and a built-in editor.
- WiX MSI installer with per-user and per-machine install scopes.
- Self-contained win-x64 publish with ReadyToRun compilation.

[Unreleased]: https://github.com/Rayzerrek/Vex/compare/v1.2.1...HEAD
[1.2.1]: https://github.com/Rayzerrek/Vex/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/Rayzerrek/Vex/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/Rayzerrek/Vex/compare/v1.0.5...v1.1.0
[1.0.5]: https://github.com/Rayzerrek/Vex/compare/v1.0.0...v1.0.5
[1.0.0]: https://github.com/Rayzerrek/Vex/releases/tag/v1.0.0

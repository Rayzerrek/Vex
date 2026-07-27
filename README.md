# Kero for Windows

> **Windows port of [egoist/kero](https://github.com/egoist/kero).**
> The Windows app lives in [`windows/`](windows) — see
> [`docs/PORTING.md`](docs/PORTING.md) for the analysis of the upstream
> codebase, the porting strategy, and build instructions. The original macOS
> sources are kept in the tree as the reference implementation.
>
> ```sh
> dotnet build windows/Kero.sln
> dotnet run --project windows/Kero.App
> ```

---

# Kero

A native terminal workspace for macOS.

![preview](https://kero.sh/kero-screenshot.png)

## Features

- Swift + libghostty by default, with an optional Alacritty backend
- Native design
- Split panes
- Git intergration
- Group by projects
- File tree

## Download

https://kero.sh

Or with Homebrew:

```sh
brew install egoist/tap/kero
```

## Contributing

[CONTRIBUTING.md](CONTRIBUTING.md)

## License

GPLv3

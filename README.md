# zstd-macos-net11-repro

Minimal reproduction for a suspected .NET 11 issue affecting
[ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp).

## Background

`ZstdSharp.Compressor.WrapStream`/`FlushStream` called in a loop intermittently
hangs on CI, but only:

- on the **macOS Intel (x64)** GitHub Actions runner,
- when built/run against the **.NET 11** SDK.

It never reproduces on .NET 10 (same runner), nor on macOS arm64, Linux, or
Windows under .NET 11.

This repo calls `WrapStream`/`FlushStream` directly in a loop, sequentially and
concurrently, across compression levels 1, 3, and 22, with a watchdog that flags
any single call that doesn't return within a generous timeout.

See `src/Program.cs` for the repro logic and `.github/workflows/repro.yml`
for the OS x TFM matrix.

## Running it

```bash
cd src
dotnet run -c Release -f net10.0
dotnet run -c Release -f net11.0
```

Or push to GitHub / trigger the `Repro` workflow manually
(`workflow_dispatch`) to run it across the full OS matrix.

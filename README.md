# zstd-macos-net11-repro

Minimal reproduction for a suspected .NET 11 issue affecting
[ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp), observed in
[GenHTTP](https://github.com/Kaliumhexacyanoferrat/GenHTTP).

## Background

GenHTTP wraps `ZstdSharp.Compressor` in a streaming sink
(`ZstdCompressingSink`) that calls `WrapStream`/`FlushStream` in a loop to
compress an HTTP response body. On CI, a test exercising this code
(`ZstdTests.TestCompressionLevels`) intermittently hangs - the request never
completes - but only:

- on the **macOS Intel (x64)** GitHub Actions runner,
- when built/run against the **.NET 11** SDK.

It never reproduces on .NET 10 (same runner), nor on macOS arm64, Linux, or
Windows under .NET 11. It also does not reproduce locally so far.

This repo strips out GenHTTP, HTTP, and sockets entirely - it only calls
`ZstdSharp.Compressor.WrapStream`/`FlushStream` directly, the same way
GenHTTP's sink does, in a loop, sequentially and concurrently, across the
three compression levels GenHTTP uses (1, 3, 22), with a watchdog that flags
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

## Status

- [ ] Reproduced on macOS Intel + .NET 11
- [ ] Root cause identified
- [ ] Reported upstream to oleg-st/ZstdSharp

# GTA-IV-RAG-MITM-Proxy

Compatibility MITM proxy for running legacy GTA IV-era RAG traffic against newer desktop RAG tooling.

It sits between the game and RAG, translates the old bootstrap/bank protocol into the newer packet formats RAG expects, and logs packet flow when `--verbose` is enabled.

## Project Layout

- `X:\Repositories\RagProxy\RagProxyCompat`
  - .NET proxy application

## Requirements

- Windows
- .NET 9 SDK

## Build

Debug build:

```powershell
dotnet build X:\Repositories\RagProxy\RagProxyCompat\RagProxyCompat.csproj
```

Publish a release-style build locally:

```powershell
dotnet publish X:\Repositories\RagProxy\RagProxyCompat\RagProxyCompat.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  -o X:\Repositories\RagProxy\artifacts\publish\win-x64
```

## Run

Typical local setup:

```powershell
dotnet run --project X:\Repositories\RagProxy\RagProxyCompat\RagProxyCompat.csproj -- `
  --listen-address 0.0.0.0 `
  --listen-port 2001 `
  --target-address 127.0.0.1 `
  --target-port 2000 `
  --proxy-base-port 61000 `
  --verbose
```

Meaning:

- `--listen-address` / `--listen-port`
  - what the game connects to
- `--target-address` / `--target-port`
  - where desktop RAG is actually listening
- `--proxy-base-port`
  - local base port used for the relayed `bank`, `output`, and `events` sockets

## Patching The Game RAG Endpoint

For GTA IV, the RAG bootstrap port is often hard-coded in the game build.

The important rule is:

- the game’s hard-coded RAG port must match the proxy `--listen-port`

If the game is hard-coded to `2000`, the simplest setup is:

- keep the proxy on `--listen-port 2000`
- point the game at the proxy host IP

If you need to change the game’s RAG port, patch the hard-coded port value in the game binary or launcher configuration to match the proxy.

there is exactly one byte difference for the port patch:

- file offset: `0x31F8B7`
- old byte: `D0`
- new byte: `D1`

That changes the immediate value:

- `0x07D0` = `2000`
- to `0x07D1` = `2001`

### Patch signature

Search for this byte sequence in the decrypted XEX:

```text
3B C0 00 0A 38 A0 07 D0 7E A4 AB 78 38 61 00 50 4B FF 44 A9
```

Base signature centered on the hard-coded port immediate:

```text
38 A0 07 D0 7E A4 AB 78 38 61 00 50
```

Patch it to:

```text
3B C0 00 0A 38 A0 07 D1 7E A4 AB 78 38 61 00 50 4B FF 44 A9
```

Base-signature patched form:

```text
38 A0 07 D1 7E A4 AB 78 38 61 00 50
```

The only changed byte is:

```text
D0 -> D1
```

### What it means

This is the startup/bootstrap port used by the game when it connects to RAG.

- `07 D0` means the game connects to port `2000`
- `07 D1` means the game connects to port `2001`

So if you want the game to connect to a proxy on a different bootstrap port, patch that immediate to match the proxy `--listen-port`.

# RagProxyCompat

Small compatibility bridge for older RAGE bank handshakes against the GTA V-era RAG protocol.

## What it currently does

- Accepts the legacy GTA IV-style bootstrap connection.
- Detects a pre-v2 `HANDSHAKE` packet and preserves the original legacy version semantics.
- Rewrites the incoming legacy handshake version to the modern v2 handshake expected by the newer RAG stack.
- Rewrites the reply back down to the legacy format expected by the old game.
- Rewrites the returned base port to a local proxy triplet and relays the later `bank`, `output`, and `events` sockets through the compatibility layer as well.
- Normalizes legacy `bank` ingress into complete `RemotePacket` records and re-wraps them as one `CHN:` frame per packet for the modern RAG bank parser.
- Relays `output`, `events`, and `bank` egress as raw byte streams instead of trying to reinterpret them as bootstrap packets.
- For GTA IV specifically, a zero-length bootstrap request is treated as a legacy handshake that still expects a `1.92` version in the reply.

## Current scope

- This build now proxies both the bootstrap socket and the later `bank`, `output`, and `events` sockets.
- The default local relay range starts at `61000`, and the proxy reserves the first free consecutive triplet from there.
- Only the bootstrap socket gets legacy-handshake translation.
- `output`, `events`, and `bank` egress are forwarded byte-for-byte.
- `bank` ingress is stateful: compressed chunks are decompressed, partial `RemotePacket` tails are buffered, and only complete packets are re-emitted toward RAG.

## Current protocol assumption

This is based on the GTA V source in:

- `X:\gta5\src\dev_ng\rage\base\src\bank\packet.cpp`
- `X:\gta5\src\dev_ng\rage\base\tools\ui\rag\rag\RageApplication.cs`
- `X:\gta5\src\dev_ng\rage\base\tools\ui\rag\ragTray\Proxy\Proxies\Rag\TrayApplicationManager.cs`

The relevant difference is:

- Modern game -> RAG handshake payload: `float RAG_VERSION` where `RAG_VERSION == 2.0f`
- Modern RAG -> game reply payload: `s32 basePort` plus optional `float RAG_VERSION`
- GTA IV bank code sends and expects `1.92f`, not `2.0f`
- This proxy now translates `1.92f -> 2.0f` toward RAG and `2.0f -> 1.92f` back toward GTA IV
- A zero-length legacy bootstrap is also translated to a `1.92` reply because GTA IV still expects the legacy version on the return path

## Run

```powershell
dotnet run --project X:\Repositories\RagProxy\RagProxyCompat -- `
  --listen-address 0.0.0.0 `
  --listen-port 2000 `
  --target-address 127.0.0.1 `
  --target-port 2001 `
  --proxy-base-port 61000 `
  --verbose
```

Example topology:

1. Point the legacy game at this proxy host and `--listen-port`.
2. Point this proxy at the modern RAG listener with `--target-address` and `--target-port`.

If the real modern RAG endpoint is fixed to `2000`, run it on a different IP or place this proxy in front of it with port forwarding.

## Likely next step

If GTA IV diverges after the bootstrap exchange, the next work item is to capture and classify the first post-handshake packets and add targeted transforms instead of raw passthrough.

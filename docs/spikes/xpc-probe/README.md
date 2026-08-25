# XPC probe (spike, 2026-08-25)

A throwaway .NET 10 console that talks to Apple's `container-apiserver` (1.3.0) directly over XPC
via P/Invoke into `libSystem.B.dylib`, with a hand-built Objective-C global block for the
connection event handler. It proved the transport and measured the floor:

| Operation | XPC (min / median / p99 ms) | CLI |
|---|---|---|
| `ping` | 0.021 / 0.025 / 0.104 | `container system status` ≈ 18.7 |
| `containerList` | 0.090 / 0.104 / 0.175 | `container ls -a` ≈ 19 |
| `containerCreate` (alpine) | 7.7 / 11.3 / 17.0 | `container create` ≈ 47 |
| `containerDelete` | 0.64 / 0.84 / 1.37 | `container delete` ≈ 19 |

Run: `cd XpcProbe && dotnet build -c Release && dotnet bin/Release/net10.0/XpcProbe.dll all`
(`XPCPROBE_VERBOSE=1` prints connection events). Full report: `../xpc/04-dotnet-xpc-probe-report.md`.
`ref-inspect.json` is `container inspect` output of the reference container the create payload was
modelled on (note: inspect prints ISO dates for display; the wire uses seconds since 2001-01-01).

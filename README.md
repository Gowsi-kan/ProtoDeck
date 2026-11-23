# ProtoDeck

![ProtoDeck logo](Assets/readme-icon.svg)

**A desktop workbench for exploring and invoking gRPC services.**

Load a `.proto` file or connect through server reflection, edit JSON requests, and test every gRPC call pattern from one cross-platform app.

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/UI-Avalonia-8B5CF6)](https://avaloniaui.net/)
[![gRPC](https://img.shields.io/badge/Protocol-gRPC-244C5A)](https://grpc.io/)
[![MIT License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
![Platforms](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-blue)

---

## Contents

- [Features](#features)
- [Tech stack](#tech-stack)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Using the app](#using-the-app)
- [JSON conventions](#json-conventions)
- [Saved collections](#saved-collections)
- [Publishing](#publishing)
- [Limitations](#limitations)
- [License](#license)

## Features

- Load `.proto` files and resolve imports from additional directories.
- Discover services through gRPC server reflection (v1, with v1alpha fallback).
- Invoke unary, server-streaming, client-streaming, and bidirectional-streaming methods.
- Edit JSON request bodies pre-filled with default values and inspect response shapes.
- Configure metadata, deadlines, cancellation, and view response headers and trailers.
- Save reusable requests to local collections.
- Connect over plaintext HTTP/2 or TLS.

## Tech stack

| Area | Technology |
| --- | --- |
| Language and runtime | C# / .NET 10 |
| Desktop UI | Avalonia UI 11 |
| gRPC transport | `Grpc.Net.Client` |
| Protocol definitions and messages | Protocol Buffers / `Google.Protobuf` |
| `.proto` parsing | `protobuf-net.Reflection` |
| UI architecture | MVVM with CommunityToolkit.Mvvm |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A gRPC service, or local `.proto` files to load

## Getting started

From the project directory:

```sh
dotnet run -c Release
```

To build without launching the app:

```sh
dotnet build ProtoDeck.csproj -c Release
```

## Using the app

1. Select **Load .proto files…** to load one or more files. Add import directories if your proto files import definitions from elsewhere.
2. Or enter a server address and select **Connect (reflection)**. Reflection must be enabled by the server.
3. Choose a service and method. The request editor is populated with a template based on the method's input message.
4. For unary and server-streaming methods, select **Send**. Select **Cancel** to stop an in-progress call.
5. For client-streaming and bidirectional methods, select **Open stream**, then edit and **Send message** as needed. Select **End stream** to finish sending or **Abort** to cancel.
6. Add metadata as `key: value`, one entry per line. Use base64 values for `-bin` metadata keys; lines beginning with `#` are comments.
7. Select **Save to collection** to save the address, method, request body, metadata, and deadline.

The app supports plaintext HTTP/2 (`http://`) and TLS (`https://`). A certificate-check bypass is available for development use; avoid it for production connections.

## JSON conventions

The request and response editors use protobuf JSON conventions:

| Protobuf type | JSON representation |
| --- | --- |
| `int64`, `uint64`, `fixed64`, `sint64` | String (numbers are also accepted as input) |
| `bytes` | Base64 string |
| `enum` | Enum name (numbers are also accepted as input) |
| `Timestamp` | RFC 3339 string, for example `"2026-11-22T10:00:00Z"` |
| `Duration` | Duration string, for example `"1.5s"` |
| Wrapper types such as `StringValue` | Bare scalar |
| `map` | JSON object |
| `NaN`, positive/negative infinity | `"NaN"`, `"Infinity"`, `"-Infinity"` |

`Struct`, `Value`, `Any`, and `FieldMask` are currently handled as regular messages using their raw field layout.

## Saved collections

Collections are stored locally as JSON in the original directory to preserve existing saved requests:

- **Linux:** `~/.config/GrpcWorkbench/collections.json`
- **Windows:** `%APPDATA%\GrpcWorkbench\collections.json`

## Publishing

Publish a self-contained, single-file build for a target runtime:

```sh
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

For macOS, create an app bundle. Use `osx-arm64` for Apple silicon or `osx-x64` for Intel:

```sh
sh Packaging/macos/build-app.sh osx-arm64
```

The bundle is written under `bin/publish/<runtime>/ProtoDeck.app`. Signing and notarization are not included. The commands above target x64 Windows/Linux; change the runtime identifier (`-r`) to publish for another supported architecture.

### Linux desktop integration

Make the published executable available as `ProtoDeck` on your desktop session's `PATH`. Then install the desktop entry and icon for your user:

```sh
mkdir -p ~/.local/share/applications ~/.local/share/icons/hicolor/scalable/apps
cp Packaging/linux/protodeck.desktop ~/.local/share/applications/
cp Assets/icon.svg ~/.local/share/icons/hicolor/scalable/apps/protodeck.svg
```

The desktop entry runs `ProtoDeck` by name. If the executable is not on the desktop session's `PATH`, edit `Exec` in the installed desktop entry to use the executable's absolute path.

## Limitations

- Client certificates for mTLS are not supported yet.
- Environments and variables are not supported yet.
- Requests are edited as JSON; there is no per-field form editor.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.

<p align="center">
  <img src="Assets/readme-icon.svg" alt="ProtoDeck app icon" width="112" />
</p>

<h1 align="center">ProtoDeck</h1>

<p align="center">
  Explore and test gRPC services from a cross-platform desktop app.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/C%23-Programming%20Language-512BD4?style=for-the-badge&logo=csharp&logoColor=white" alt="C sharp" />
  <img src="https://img.shields.io/badge/Avalonia-UI%20Framework-8B5CF6?style=for-the-badge" alt="Avalonia UI" />
  <img src="https://img.shields.io/badge/gRPC-API%20Protocol-244C5A?style=for-the-badge" alt="gRPC" />
</p>

ProtoDeck is a .NET 10 desktop app built with Avalonia for exploring and
invoking gRPC services. Load local `.proto` files, or connect to a server
that supports reflection to discover its services and methods directly.
Edit JSON requests, inspect responses, and save reusable calls in collections.

## Features

- Load `.proto` files and resolve imports from additional directories.
- Discover services and methods through server reflection (v1, with v1alpha
  fallback) when reflection is enabled on the server.
- Invoke unary, server-streaming, client-streaming, and bidirectional-streaming
  methods.
- Edit JSON requests using templates generated from method input messages.
- Configure metadata and deadlines, cancel calls, and inspect response headers
  and trailers.
- Save requests to local collections for reuse.
- Connect over plaintext HTTP/2 or TLS, and switch between light and dark themes.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows, Linux, or macOS
- Local `.proto` files or a gRPC server with reflection enabled for service
  discovery
- A reachable gRPC server to invoke methods

## Get started

Clone the repository and launch the app:

```sh
git clone https://github.com/Gowsi-kan/ProtoDeck.git
cd ProtoDeck
dotnet run -c Release
```

To build without launching the app:

```sh
dotnet build ProtoDeck.csproj -c Release
```

## Using the app

1. Select **Load .proto files...** to load local definitions. Add import paths
   separated by semicolons when needed.
2. Alternatively, enter a server address and select **Connect** to discover
   services using server reflection. The server must have reflection enabled.
3. Select a service and method. The request editor is populated with a
   template based on the method's input message.
4. For unary and server-streaming methods, select **Send**. Select **Cancel**
   to stop an in-progress call.
5. For client-streaming and bidirectional methods, select **Open stream**, then
   edit and **Send message** as needed. Select **End stream** to finish
   sending or **Abort** to cancel.
6. Add metadata as `key: value`, one entry per line. Use base64 values for
   `-bin` metadata keys; lines beginning with `#` are comments.
7. Use **Save** to store the address, method, request body, metadata, and
   deadline in a collection.

## Connections and saved collections

ProtoDeck supports plaintext HTTP/2 (`http://`) and TLS (`https://`). **Skip TLS
verification** accepts untrusted certificates and is intended for development
only; leave it disabled for production connections.

Collections are stored locally as JSON in the original app directory to
preserve existing saved requests:

- **Linux:** `~/.config/GrpcWorkbench/collections.json`
- **Windows:** `%APPDATA%\GrpcWorkbench\collections.json`

## JSON conventions

Request and response editors use protobuf JSON conventions:

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

`Struct`, `Value`, `Any`, and `FieldMask` are currently handled as regular
messages using their raw field layout.

## Publishing

Publish a self-contained, single-file build for a target runtime:

```sh
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

For macOS, create an app bundle. Use `osx-arm64` for Apple silicon or `osx-x64`
for Intel:

```sh
sh Packaging/macos/build-app.sh osx-arm64
```

The bundle is written under `bin/publish/<runtime>/ProtoDeck.app`. Signing and
notarization are not included. The Windows and Linux commands above target
x64; change the runtime identifier (`-r`) for another supported architecture.

### Linux desktop integration

Make the published executable available as `ProtoDeck` on your desktop
session's `PATH`. Then install the desktop entry and icon for your user:

```sh
mkdir -p ~/.local/share/applications ~/.local/share/icons/hicolor/scalable/apps
cp Packaging/linux/protodeck.desktop ~/.local/share/applications/
cp Assets/icon.svg ~/.local/share/icons/hicolor/scalable/apps/protodeck.svg
```

The desktop entry runs `ProtoDeck` by name. If the executable is not on the
desktop session's `PATH`, edit `Exec` in the installed desktop entry to use the
executable's absolute path.

## Project structure

| Path | Description |
| --- | --- |
| [`Views/`](Views/) | Avalonia desktop window and UI |
| [`ViewModels/`](ViewModels/) | UI state and commands |
| [`Services/`](Services/) | Schema loading, reflection, invocation, JSON conversion, and collection storage |
| [`Models/`](Models/) | Application data models |
| [`Protos/`](Protos/) | gRPC reflection protocol definitions |
| [`Packaging/`](Packaging/) | Linux desktop integration and macOS app bundling |
| [`Assets/`](Assets/) and [`Styles/`](Styles/) | App icons and UI styling |

## Current limitations

- Server reflection must be enabled to discover services without local
  `.proto` files.
- Client certificates for mTLS are not supported yet.
- Environments and variables are not supported yet.
- Requests are edited as JSON; there is no per-field form editor.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.

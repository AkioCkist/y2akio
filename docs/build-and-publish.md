# Build and Publish

## Debug build

From the repository root:

```powershell
dotnet restore .\y2akio.sln
dotnet build .\y2akio.sln -c Debug -p:Platform=x64
```

Run:

```powershell
.\y2akio\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\y2akio.exe
```

You can also open `y2akio.sln` in Visual Studio, select an architecture, and press F5.

## Release publish

The application must be distributed as a folder; a WinUI 3 self-contained deployment is not a single executable.

```powershell
dotnet publish .\y2akio\y2akio.csproj `
  -c Release `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 `
  -p:PublishTrimmed=false
```

`PublishTrimmed=false` is recommended for this application because trimming XAML-reflected types and WinUI dependencies can cause runtime failures. The output is expected under:

```text
y2akio\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

## Deployment contents

Ship the complete publish directory. Confirm it includes:

```text
publish/
├── y2akio.exe
├── Assets/
├── bin/
│   ├── yt-dlp.exe
│   └── ffmpeg-9.0.1-full_build/bin/ffmpeg.exe
├── Microsoft.WindowsAppRuntime.Insights.Resource.dll
└── runtime and application DLLs
```

The project contains a custom target that extracts `Microsoft.WindowsAppRuntime.Insights.Resource.dll` from the Windows App Runtime package for self-contained deployment. Verify this file on a clean machine; development machines can hide a missing runtime resource because the runtime is already installed.

## Other architectures

The solution declares x86, x64, and ARM64. Match `Platform` and `RuntimeIdentifier`, for example `ARM64` with `win-arm64`. External binaries must also support the target architecture. The checked-in project defaults remain x64, so validate non-x64 publish paths and runtime behavior explicitly.

## Startup diagnostics

Run the published executable from PowerShell and inspect the exit code:

```powershell
.\y2akio.exe
$LASTEXITCODE
```

Inspect recent Windows application faults:

```powershell
Get-WinEvent -FilterHashtable @{ LogName='Application'; Id=1000,1026 } -MaxEvents 20 |
  Format-List TimeCreated, ProviderName, Message
```

For runtime-only XAML or resource failures, reproduce under the Visual Studio debugger with the Release configuration.

## Release checklist

1. Restore and build from a clean checkout.
2. Confirm the intended `yt-dlp` and FFmpeg files were copied.
3. Publish with trimming disabled.
4. Launch on a machine without a preinstalled Windows App Runtime when possible.
5. Exercise navigation, one download, one FFmpeg operation, settings persistence, and a completion toast.
6. Zip the entire publish directory, not only `y2akio.exe`.

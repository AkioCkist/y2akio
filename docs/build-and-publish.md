# Build and Publish

## Build and run in development

```powershell
dotnet build -c Debug -p:Platform=x64
.\\bin\\x64\\Debug\\net8.0-windows10.0.19041.0\\win-x64\\y2akio.exe
```

Alternatively, open `y2akio.sln` or `y2akio/y2akio.csproj` in Visual Studio and press **F5**. The app is unpackaged, so no MSIX installation is required for development.

On first launch, the app registers the toast AUMID and Start-menu shortcut used for notifications.

## Publish a standalone deployment folder

```powershell
dotnet publish -c Release -p:Platform=x64 -p:PublishTrimmed=false
```

`PublishTrimmed=false` is required because WinUI 3 apps can break when trimming is enabled.

The output is under:

```text
bin\\Release\\net8.0-windows10.0.19041.0\\win-x64\\publish\\
```

WinUI 3 unpackaged apps cannot be distributed as a single executable. Ship or zip the complete `publish` folder, including the runtime DLLs.

Copy yt-dlp and ffmpeg into `publish\\bin`:

```powershell
cd bin\\Release\\net8.0-windows10.0.19041.0\\win-x64\\publish
mkdir bin
copy ..\\..\\..\\..\\..\\bin\\yt-dlp.exe .\\bin\\
xcopy ..\\..\\..\\..\\..\\bin\\ffmpeg-9.0.1-full_build .\\bin\\ /E
```

The final layout should contain `publish\\y2akio.exe` and the media tools below `publish\\bin`. Run `y2akio.exe` from the complete folder.

## Debugging startup failures

1. Run the executable from a terminal and inspect the exit code:

   ```powershell
   .\\y2akio.exe; echo "exit=$LASTEXITCODE"
   ```

2. Inspect Windows Application events:

   ```powershell
   Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1026} -MaxEvents 20 |
     Format-List TimeCreated, ProviderName, Message
   ```

   Event 1000 identifies application faults; event 1026 contains managed .NET exceptions.

3. Reproduce under the Visual Studio debugger. For published-only failures, debug the Release configuration too.

Known unpackaged-app constraints are documented in the source: `Windows.Storage.ApplicationData` needs package identity, and XAML `StaticResource`, `x:Bind`, and initialization-time event errors may appear only at runtime.

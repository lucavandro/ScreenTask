# Reproducing Windows portable builds

Run these commands from the repository root on Windows with the .NET 9 SDK.
The self-contained x64 build includes .NET and can run without installation:

```powershell
dotnet publish ScreenTask/ScreenTask.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -o build/portable-win-x64
Copy-Item README.md build/portable-win-x64/
Compress-Archive -Path build/portable-win-x64/* `
  -DestinationPath build/ScreenTask-win-x64-portable.zip -Force
Get-FileHash build/ScreenTask-win-x64-portable.zip -Algorithm SHA256
```

Extract all ZIP contents into a folder before starting `ScreenTask.exe`. Close
any older running copy, including its notification-area instance. Do not copy
only the executable when the package contains accompanying notices or files.

For a smaller package that requires the matching .NET Desktop Runtime, change
`--self-contained true` to `--self-contained false` and omit
`IncludeNativeLibrariesForSelfExtract`. The GitHub release workflow already
publishes standalone and framework-dependent variants when an explicit `v*`
release tag is pushed; an ordinary source push does not publish a release.

Generated binaries and ZIPs stay under the ignored `build/` directory. Git tracks
the sources and instructions, not the portable executable or local build output.
Once the GPU capture dependency is present, its `DirectX-NOTICE.txt` is copied by
the project into the publish folder and must remain in the distributed package.

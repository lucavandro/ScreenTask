# Windows development SDK

ScreenTask targets .NET 9 Windows desktop. Building it requires the **.NET 9 SDK**,
not just the Desktop Runtime. The local implementation and performance work in
this conversation used SDK **9.0.321 x64** and runtime **9.0.23**.

Check the installation in a new PowerShell terminal:

```powershell
dotnet --list-sdks
dotnet --info
dotnet restore ScreenTask.sln
dotnet build ScreenTask.sln -c Release
```

If the SDK is absent, install the official Microsoft .NET 9 SDK for Windows x64.
The Windows installer may require UAC authorization. The SDK installation is a
machine-level prerequisite and cannot be included in a Git commit. No SDK
installer or machine-specific installation path is committed to this repository.

Users of the self-contained portable build do not need to install the SDK or
runtime. See [Windows portable builds](WINDOWS_PORTABLE.md).

# Sakana Launcher

Sakana is an independent Windows 11 launcher for Minecraft: Java Edition, built with WinUI 3. It focuses on isolated instances, Modrinth modpacks, and in-launcher customization.

**AI development disclosure:** Sakana is developed with substantial assistance from AI tools.

> Sakana is not an official Minecraft product and is not approved by or associated with Mojang or Microsoft.

## Project status

Sakana is in early development. Features and workflows are still changing, and some integrations need more real-world verification.

Current development includes:

- Creating and managing isolated Vanilla, Fabric, Quilt, Forge, and NeoForge instances.
- Importing and exporting Modrinth `.mrpack` files, browsing Modrinth modpacks, and reviewing or applying linked pack updates.
- Searching for compatible Modrinth mods, resource packs, shaders, and data packs, plus local content management.
- Organizing instances with groups, tags, search, and per-instance custom icons.
- Global and per-instance Java, memory, JVM argument, environment variable, and game-window settings.
- Microsoft device-code sign-in flow and Minecraft account checks.

Forge and NeoForge still need end-to-end installation and launch verification. The Microsoft login application is awaiting Minecraft Services authorization; until that is approved, Minecraft account verification and launching may be unavailable. CurseForge integration and downloads are out of scope.

See the [roadmap](docs/ROADMAP.md) for current implementation status and planned work.

## Requirements

- Windows 11
- .NET 10 SDK
- WinUI 3 / Windows App SDK 2.7

## Build and run

Open `Sakana.slnx` in Visual Studio, or run the app project from PowerShell:

```powershell
Set-Location .\src\Sakana.App
dotnet run
```

The first install downloads Minecraft files and Java components from their official metadata and file hosts. Sakana stores its data under `%LOCALAPPDATA%\Sakana`.

## Project documentation

- [Product brief](docs/PRODUCT_BRIEF.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Roadmap and decisions](docs/ROADMAP.md)
- [Microsoft account setup](docs/ACCOUNT_SETUP.md)
- [Mod ecosystem](docs/MOD_ECOSYSTEM.md)

## Privacy and accounts

Sakana requires a Microsoft account that owns Minecraft: Java Edition before launching the game. It does not offer offline or cracked launching. Refresh credentials are protected for the current Windows user with DPAPI; do not share passwords, device codes, or access tokens.

## License

Sakana's own code is licensed under the [MIT License](LICENSE). Third-party NuGet packages, bundled assets, and external services remain subject to their respective licenses and terms.

## Third-party services

Sakana uses Mojang/Microsoft services for Minecraft metadata, game files, Java runtime components, and account verification. Modrinth is the supported online mod and content provider. Each provider's terms apply when using its services.

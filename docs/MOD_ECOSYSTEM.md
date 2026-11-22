# Mod ecosystem implementation plan

This document turns Sakana's modpack and content goals into a sequence that can be implemented and reviewed. The launcher stays Windows 11 / WinUI 3 and keeps Microsoft account authentication for online Minecraft play.

## Product behaviors to target

- Each instance owns its loader, mods, configurations, resource packs, shader packs, saves, and launch settings. Updating one instance must not mutate another.
- A user can install a loader for a chosen Minecraft version, then install, enable, disable, update, and remove compatible mods from that instance.
- A user can import and export common modpack formats, review the Minecraft/loader versions and optional content before installation, and see clear progress and actionable failures.
- Resource packs and shader packs are managed as instance content. The UI explains when a shader pack needs a compatible shader renderer such as Iris or Oculus. Data packs are installed and managed inside a selected save's `datapacks` folder, matching Minecraft's per-world storage model.
- Local drag/drop and file-based installs remain available if a provider catalog is unavailable.

## Architecture boundaries

Keep the WinUI pages as clients of `Sakana.Application` services. Put formats and external services behind small interfaces:

- `IMinecraftLoaderInstaller`: resolves compatible loader versions and prepares a loader-specific version profile and runtime files.
- `IModpackReader` / `IModpackWriter`: reads and writes one pack format at a time; the first reader target is Modrinth `.mrpack`.
- `IContentCatalog`: searches a provider for projects and compatible releases; provider API rules stay inside that adapter.
- `IInstanceContentService`: lists, installs, enables/disables, updates, and removes files inside one instance.

Extend the instance model with a base Minecraft version, loader ID/version, and optional modpack origin/version. Keep shared immutable libraries and download caches under Sakana's shared data root; put all mutable game content under that instance's own `game` directory.

## Delivery order

1. [x] **Loader-aware instance model and launch recipe.** Store loader identity/version, migrate existing profiles as Vanilla, and compose loader launch metadata with Mojang metadata.
2. **Fabric, Quilt, Forge, and NeoForge adapters (implemented; runtime verification pending).** Resolve compatible versions from official metadata. Forge and NeoForge use their official installer jars to generate the launch profile and patched client jar inside the instance, then download and verify the generated launch libraries. Finish end-to-end create, install, verify, repair, and launch verification on Windows.
3. [x] **Local `.mrpack` import v1.** Read `modrinth.index.json`, show pack metadata, import Vanilla/Fabric/Quilt/Forge/NeoForge packs, select optional client files, download client files with SHA-1/SHA-512 checks and mirror fallback, and apply `overrides/`. Import is intentionally limited to Modrinth `.mrpack`; CurseForge integration and downloads are permanently excluded. Arbitrary hosts and server-only files are not imported into the client instance. End-to-end runtime verification is still pending.
4. [x] **Modrinth mod manager v1.** Inspect Fabric/Quilt/Forge/NeoForge profiles, import local `.jar` files, search Modrinth by Minecraft/loader compatibility, select a compatible version, install required dependencies, enable/disable, and remove. Provider-aware updates, dependency change previews, and drag/drop remain future work.
5. [x] **Resource and shader pack management v1.** Search Modrinth for the selected Minecraft version, select ZIP versions, validate and install into per-instance folders, inspect resource-pack descriptions, enable/disable, and remove. The UI explains the shader renderer requirement. Compatibility labels and drag/drop remain future work.
6. [x] **Online Modrinth modpacks v1.** Search, inspect supported versions, download and hash-verify `.mrpack` files, then reuse the local import preview and install flow. Online Vanilla/Fabric/Quilt/Forge/NeoForge packs are supported.
7. **Forge and NeoForge runtime validation.** Verify installer-generated profile IDs, client jars, modern module-path launch arguments, legacy Forge arguments, repair, and Java-version requirements with real instances.
8. [x] **Modrinth catalog quality v1.** Search modpacks, mods, resource packs, shaders, and data packs with supported sort orders and paginated results; validate data pack versions and install them into a selected world. Continue with compatibility labels, renderer dependency detection, and provider-aware update tracking.
9. [x] **Modrinth `.mrpack` export and linked updates v1.** Export tracked Modrinth files plus eligible local overrides; unknown local JAR files are omitted and reported. Online-installed packs retain project/version identity and can either create a separate updated instance or update in place when Minecraft and loader versions match. In-place updates stage and verify files, back up replacements, preserve local conflicts unless explicitly selected for overwrite, and retain files removed from the new pack. Automatic transfer of saves/personal files remains future work. Modrinth remains the only supported online provider; CurseForge integration and downloads are permanently excluded.

## Import safety and integrity rules

- Treat pack archives and their JSON as untrusted input. Reject absolute paths, drive-prefixed paths, `..` segments, and any resolved destination outside the instance root.
- Limit archive entry count, total expanded size, individual file size, and metadata size before extraction to prevent resource exhaustion.
- Download to temporary files, validate declared sizes and hashes, and move into place only after validation. Keep a failed import retryable and never mark it installed early.
- During routine installs, reuse existing Mojang assets only when the official SHA-1 is also the asset filename and the declared size matches; downloaded assets are hashed before commit. The explicit Repair action performs full hash checks of existing Mojang game and Java runtime files.
- For `.mrpack`, require the documented root `modrinth.index.json` and supported format version. Validate each file path and HTTPS download URI against the supported host list. Require and verify SHA-1 and SHA-512. Install `overrides/` into the instance root without replacing files that are already there, so repairs preserve user edits.
- Treat `server-overrides/` and client/server `env` values deliberately; do not install server-only files into a client instance. Ask users about optional files.
- Report files without an accessible download URL as an actionable import issue instead of silently producing an incomplete pack.

## Product references

- [Prism Launcher modpack import](https://prismlauncher.org/wiki/getting-started/download-modpacks/) covers local `.zip` / `.mrpack` imports and catalog-backed pack sources.
- [Prism Launcher mod management](https://prismlauncher.org/wiki/help-pages/loader-mods) presents instance-local mods with metadata, drag/drop, enable/disable, and provider downloads.
- [HMCL](https://github.com/HMCL-dev/HMCL) is a reference for broad loader coverage and modpack creation.
- [PCL source repository](https://github.com/Meloong-Git/PCL) is a reference for the Chinese launcher workflow and its download / Minecraft launch modules.
- [Modrinth App](https://modrinth.com/app) is a reference for keeping mod discovery, installed content, modpack sharing, and existing-instance import in the same launcher flow.
- [Modrinth `.mrpack` format](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack) defines the manifest, file hash, path, environment, and override rules.

These projects are product references; Sakana will implement its own code, UI, and service adapters.

# Product brief

## Product

- **Product name:** Sakana Launcher
- **Software name:** Sakana
- **Project identifier:** `SAKANA`
- **Executable:** `Sakana.exe`
- **GitHub repository:** `sakana-launcher`
- **Version name:** Sakana x.x (current: **Sakana 0.1**)
- **Tagline:** A new way to play Minecraft.
- **Platform:** Windows 11
**Product:** A modpack-first launcher and customization workspace for Minecraft: Java Edition.

Sakana combines Windows Fluent design with a restrained gaming presentation. Fluent supplies native navigation, typography, accessibility, and interaction patterns. Game artwork and instance identity supply the gaming character. Its primary job is to make finding, installing, and personalizing modpacks feel safe and approachable without asking players to manage files by hand.

## Product principles

1. **Native:** keyboard, mouse, touch, high DPI, system theme, and Windows conventions work naturally.
2. **Clear:** users can tell which instance will launch and what the launcher is doing.
3. **Reliable:** install and update operations can report progress, cancel safely, resume where practical, and explain failures.
4. **Private:** credentials are stored through Windows credential protection and never written to ordinary settings or logs.
5. **Independent:** Sakana owns its UI, data model, and engine. No Prism code or Prism data-format dependency.

## Product experience

### Modpack-first

- Browse and import a pack, inspect its Minecraft version, loader, included content, optional files, and expected download size before installation.
- Keep each pack in an isolated instance. Repairing or changing one pack must not alter another instance's saves, settings, or content.
- Make updating a pack reviewable: show added, changed, and removed files; preserve user-edited configuration and worlds; offer a rollback point.

### Friendly to players who customize their game

- Discover and install compatible mods, resource packs, shaders, and data packs from inside Sakana, using Modrinth as the supported online content provider. Install data packs into a selected world because Minecraft stores them per save.
- Present content together in an instance's “装修” workspace: what is installed, which version it targets, what it depends on, and whether it needs a renderer such as Iris.
- Allow local import and temporary enable/disable for every content type, with an honest explanation when version compatibility or dependencies cannot be confirmed.
- Offer a safe way to try a new look or mod set without losing the current one: duplicate the instance or save a named content setup before applying changes.

### Sakana's own direction

Treat an instance as a playable collection rather than a folder. The library card stays focused on launch; its overflow menu holds repair, editing, content, and future maintenance actions. The instance detail view becomes the workspace for pack identity, content, worlds, screenshots, and recovery. A later “装扮方案” can save a reusable selection of resource packs, shaders, and UI-friendly options, then apply it to another compatible instance without copying saves.

## Main navigation

| Area | Responsibility | MVP behavior |
| --- | --- | --- |
| Home | Featured instance, recent launches, primary actions | Real data from the local library; useful empty state |
| Library | Search, sort, create, inspect, and launch instances | Pack-oriented cards with launch as the primary action and secondary actions under `⋯` |
| Browse | Discover modpacks and compatible content | Modrinth modpacks and instance content, filtered to installable versions |
| Instance workspace | Customize, inspect, and repair one instance | Mods, resource packs, shaders, data packs, worlds, screenshots, and pack update review |
| Downloads | Active and completed install/download jobs | Per-task progress, cancellation, retry, and clear file validation |
| Accounts | Sign-in state and active game profile | Microsoft sign-in for accounts that own Minecraft: Java Edition; no offline or cracked launch mode |
| Settings | Theme, global launch defaults, Java detection, local data | Shared Java, memory, arguments, environment, and window preferences |

The current app is still an early foundation. The Library can browse Modrinth Vanilla/Fabric/Quilt/Forge/NeoForge modpacks, install compatible mods for each mod loader, and search/install resource packs, shaders, and world-scoped data packs. Per-instance controls manage local content and Java/window settings; new instances follow global defaults until customized. Forge/NeoForge runtime validation remains in development. CurseForge ZIP import and API-backed downloads are supported when an approved API key is available; files unavailable for third-party distribution fall back to their official page and manual import.

## Visual direction

- Default to dark theme, with light and follow-system modes.
- Use Mica for the window/background layer and Acrylic for transient surfaces; keep content cards opaque enough for readable text.
- Use Segoe UI Variable and Fluent icons where available.
- Use Sakana Blue for the primary action, with ice-blue and muted violet as limited accents.
- Keep motion short and functional: navigation, hover/focus feedback, and progress-state transitions.
- Support compact and wide layouts without hiding essential actions.

## MVP boundary

The current engine target supports:

- create and manage isolated Vanilla, Fabric, Quilt, Forge, and NeoForge instances;
- resolve official version metadata and required game files;
- download, verify, and install those files;
- detect a compatible system Java and download the Java runtime component named by version metadata only when none is available;
- require Microsoft account sign-in and Minecraft: Java Edition ownership before any game launch; never offer an offline or cracked launch mode;
- show the game process state and capture its output for troubleshooting;
- preserve the library and settings across app updates.

The current implementation supports local `.mrpack` and CurseForge `manifest.json` ZIP import, online Modrinth content search, `.mrpack` export, and linked pack-version previews that can create an isolated updated instance or safely update in place with per-file conflict choices; compatible mod search/install/update checks for each mod loader; resource/shader discovery; data pack installation into a selected save; active-account full-body front/back skin preview with an official skin-management handoff; saved instance-library views; and cross-instance launch-setting copy/templates. When Minecraft provides a texture URL, Sakana renders it with an outer-layer toggle; if no texture is available, it clearly labels a default-character illustration. It provides global and per-instance Java, memory, JVM argument, environment, fullscreen, and window-size settings. Forge/NeoForge end-to-end runtime verification, automatic save transfer, content update tracking, broader world management, cloud sync, social features, and cross-platform support remain staged work.

## MVP acceptance criteria

1. A clean install opens to an honest empty library with a working create-instance action.
2. A user can install a selected Vanilla, Fabric, or Quilt release and see separate resolving, downloading, verifying, and ready states.
3. A failed download identifies the failed item and offers retry without corrupting the instance.
4. A ready instance launches with the selected account; process output can be opened after launch.
5. Restarting Sakana preserves instances, settings, and unfinished task information.
6. No screen reports success until its operation has completed and persisted.

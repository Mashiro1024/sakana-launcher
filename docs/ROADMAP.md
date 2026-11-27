# Roadmap and decisions

## Phases

### 0. Foundation preview

**Status: complete**

- Create the WinUI 3 shell and design tokens.
- Implement Home, Library, Downloads, Accounts, and Settings navigation.
- Add responsive empty/loading/error states and keyboard navigation.
- Add SQLite migrations, settings, logging, and the Sakana data root.
- Use real local instance profiles and settings; use clear empty/loading/error states.

**Exit:** the UI shell builds, its local profile/settings state is real, and no unavailable launcher action is represented as working.

### 1. Vanilla engine MVP

**Status: in progress**

- Read official Mojang release metadata and resolve version manifests.
- Download and verify client, library, logging, and asset files with bounded concurrency.
- Show installation progress, persist instance state, cancel work, and retry failed or canceled installs.
- Extract Windows native libraries with archive path checks.
- Build an in-memory launch plan from Mojang JVM/game arguments, OS rules, and account-session placeholders.
- Record interrupted installs on the next startup for a safe retry.
- Create and list isolated instances.
- Resolve Mojang's Java component for the selected version and prepare its Windows runtime.
- Show persisted launch history and active process state on Home.
- Show bounded stdout/stderr output and process state on the Library page.

**Current boundary:** an instance is marked installed when its version metadata, game jar, libraries, assets, Windows natives, selected loader files (when applicable), and a compatible Java runtime are present and verified. Sakana detects compatible system Java first and downloads the matching Mojang runtime when needed. It implements Microsoft device-code sign-in followed by Xbox/XSTS and Minecraft Services ownership/profile checks, and stores refresh credentials with Windows DPAPI. The launcher manages its Microsoft login application internally; no Client ID controls are shown in the UI. Installed instances have launch/stop controls and global or per-instance launch settings. Home displays launch history and active state; Library displays the latest 300 stdout/stderr lines. Per-file progress is in memory; interrupted jobs are marked for retry rather than resumed automatically. Fabric, Quilt, Forge, and NeoForge installation/profile integration are implemented but still need end-to-end verification.

**Exit:** a clean machine can create, install, and launch a Vanilla instance and recover from a failed/canceled install.

### 2. Instance quality

- [x] Verify installed Vanilla files against Mojang metadata and automatically repair missing or corrupted files without touching instance saves or configuration.
- [x] Add global defaults and per-instance Java/memory/window launch settings, including auto memory, Java selection, JVM arguments, environment variables, fullscreen, and window dimensions.
- [x] Detect compatible system Java and download the matching Mojang runtime only when no compatible runtime is available.
- [x] Persist the Vanilla/Fabric/Quilt/Forge/NeoForge loader choice and selected version; provide official metadata, installer/profile integration, repair, and launch-profile composition.
- [x] Rename an instance from its library menu, preserving its files and launch history.
- [x] Duplicate instances while retaining their isolated files and loader/pack metadata.
- Export, import, and back up full instances.
- [x] Warn before launch when a manually configured Java heap exceeds the recommendation derived from current free physical memory; show total/available RAM and Java selection context. Automatic allocation continues to use current free memory at launch.
- Add snapshots and more version choices based on user demand.

### 3. Mod ecosystem

- [x] Add the loader-aware instance model and Fabric metadata, install, repair, and launch integration.
- Verify Fabric, Quilt, Forge, and NeoForge installation, repair, and launch end to end.
- [x] Import local Modrinth `.mrpack` files for Vanilla/Fabric/Quilt/Forge/NeoForge, with metadata preview, optional-file selection, client downloads, verified SHA-1/SHA-512, and `overrides/` extraction.
- [x] Export installed instances as Modrinth `.mrpack`, including tracked Modrinth files and supported configuration/resource overrides.
- [x] Link online Modrinth `.mrpack` instances to their project/version and preview added, changed, removed, and locally modified files before creating an isolated updated instance. The previous instance remains available as rollback.
- [x] Update a linked Modrinth pack in place when Minecraft and loader versions match. New files are staged and verified, overwritten files are backed up for rollback, local conflicts default to preserved and require an explicit per-file choice to replace, and files removed from the pack remain in the instance.
- [x] Add CurseForge modpack import and API-backed project/file lookup. When a file is unavailable for third-party distribution, continue installing available content and offer the official file page for manual download/import; do not bypass author controls.
- [x] Add per-instance Fabric/Quilt/Forge/NeoForge mod management v1: inspect loader metadata, import `.jar` files, enable/disable, and remove without changing another instance.
- [x] Add Modrinth mod search, Minecraft/loader-compatible version selection, SHA-1/SHA-512 download validation, and required-dependency installation for Fabric/Quilt/Forge/NeoForge.
- Add provider-aware installed-version tracking, update checks, dependency change previews, and dependency status in the mod list.
- [x] Add per-instance local resource-/shader-pack import, listing, enable/disable, and removal; explain the shader renderer requirement.
- [x] Add sortable, paginated Modrinth search for modpacks, mods, resource packs, and shaders.
- [x] Add Modrinth data pack search, version validation, and import, enable/disable, and removal within a selected save's `datapacks` directory.
- [x] Show Minecraft compatibility labels in Modrinth content results.
- Add local file and drag-and-drop installation paths alongside provider catalogs. Keep catalogs behind provider interfaces and comply with their API terms and download restrictions.
- Keep loaders, pack-format readers, and content providers outside the UI layer so other supported sources can be added without rewriting instance screens.

### 4. Instance experience

- [x] Add instance duplication, rename, and profile-specific game-directory controls.
- [x] Add instance library search, groups, tags, and per-instance custom icons.
- [x] Filter instances by loader and install/run state; sort by creation time, name, or Minecraft version, and clear filters in one action.
- [x] Export and restore individual Minecraft worlds as ZIP backups, validating archive paths and preserving existing saves.
- Back up and export/import full instances with clear save and account-data handling.
- Add launch troubleshooting with Java, memory, missing dependencies, and crash-log guidance.
- Add resource-pack and shader-pack compatibility checks and per-instance content toggles.

### 5. Release readiness

- Select the distribution channel and signing approach.
- Add update behavior appropriate to the selected packaging model.
- Complete accessibility, localization, crash recovery, and clean-install/upgrade flows.
- Keep the shell on WinUI 3 `NavigationView` + `Frame` navigation with native theme transitions; extend motion only where it clarifies navigation or collection changes, and honor Windows animation preferences.

## Prism capability parity targets

Use Prism's feature breadth as a coverage target while implementing Sakana's own Windows 11 workflows and architecture. These are staged targets rather than a promise that every platform-specific Prism option belongs in Sakana.

| Area | Sakana today | Remaining parity work |
| --- | --- | --- |
| Instances | Isolated Vanilla/Fabric/Quilt/Forge/NeoForge instances, install/repair, per-instance memory, rename/duplicate, groups, tags, search, loader/state filters, sorting, custom icons, saved filter views | Clone with content choices, multiple game roots, profile shortcuts |
| Accounts and launch | Microsoft sign-in, ownership checks, multiple saved accounts, account switching at launch, launch/stop, output and history, server/world Quick Play; full-body front/back skin preview with outer-layer control and official skin-management handoff | In-launcher skin upload/customization, cross-instance account/profile sharing |
| Modpacks | Local `.mrpack` import, online Modrinth browsing/install, export, isolated update copies, safe in-place updates with per-file conflict review, and per-world ZIP backup/restore | Recovery UI for a failed rollback, optional-file change review, deeper save/world management |
| Mods | Local file management plus sortable/paginated Modrinth compatible search/install and required dependencies for Fabric/Quilt/Forge/NeoForge | Update tracking, dependency status/change preview |
| Other content | Online Modrinth resource/shader search; per-instance install, enable, disable, and remove; world-scoped data packs; per-instance world/server lists and Quick Play | Deeper save management, screenshots, content updates, and saved customization setups |
| Java and launch options | System Java detection, on-demand Mojang runtime download, global/per-instance memory, Java, JVM args, environment, and window options | More Java versions, pre/post-launch commands, deeper diagnostics |
| Diagnostics and shell | Installation progress, live process output, history, logs | Searchable log/crash views, system diagnostics, command-line launch/import, protocol links, desktop shortcuts |
| Launcher settings | Theme, global launch defaults, Java detection, Sakana data root, copy launch settings between instances and `.sakanlaunch` templates | Proxies, download mirrors, folder settings, auto-updates, backup/migration tools |

Use [Modrinth App](https://modrinth.com/app) as the main reference for browsing and installing modpacks and content in one workspace. Keep Prism's feature coverage as a parity target, with HMCL and PCL2 as references for selected Chinese-language workflows. The UI and data model remain Sakana's own.

## Product priority from user direction

1. **Worlds and servers:** per-instance world and server lists, Quick Play, and individual world ZIP backup/restore are implemented; continue improving save management, discovery, and launch feedback.
2. **Accounts and sharing:** multiple Microsoft accounts and a full-body skin preview are implemented; next add in-launcher skin customization and cross-instance account/profile sharing.
3. **Instance organization:** groups, tags, library search, loader/state filters, sorting, and custom icons are implemented; continue with saved-view management and profile shortcuts.
4. **Continue parity work:** complete Modrinth App and Prism workflows with Sakana's own WinUI 3 patterns; CurseForge import and API-backed access are supported subject to approval and author distribution settings.

## Decisions recorded

| Decision | Current choice | Revisit when |
| --- | --- | --- |
| Product identity | Sakana Launcher / Sakana / `SAKANA` | Not planned |
| OS | Windows 11 only | Product strategy changes |
| UI | WinUI 3 / Fluent + gaming | A tested constraint emerges |
| Process model | Modular monolith; game is a child process | Updater isolation or reliability needs a separate process |
| Data | Sakana-owned root and SQLite metadata | Portability/cloud sync becomes a requirement |
| First engine target | Vanilla | Core install/launch is reliable |
| Mod ecosystem | Loader adapters + pack-format readers + provider interfaces | Each integration is reviewed for compatibility and provider terms |
| CurseForge access | User-provided, CurseForge-approved API Key; preserve author distribution controls and provide official-page/manual-import fallback | Revisit if CurseForge changes its API or third-party distribution terms |
| Distribution | Prototype packaged; public channel undecided | Before public beta |

## Deferred choices

- Exact .NET and Windows App SDK versions: pin them when the solution is created, using supported stable releases at that time.
- Account provider implementation details: validate official provider requirements during implementation.
- Logo and Minecraft branding: review before external release.
- Automatic updates: choose only after distribution packaging is decided.

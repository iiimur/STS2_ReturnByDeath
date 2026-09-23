---
name: sts2-mod-builder
description: Create, inspect, modify, compile, test, package, share, and publish Slay the Spire 2 mods. Use for any STS2 mod task involving Harmony patches, game API discovery, local DLL/PCK/JSON mod installation, compatibility with game updates, Workshop packaging, or uploading/updating a Steam Workshop item.
---

# STS2 Mod Builder

Use this skill for the complete Slay the Spire 2 mod lifecycle. Treat the installed game assemblies as the source of truth: versions change frequently, and online decompilations may not match the user's build.

## Operating rules

- Inspect first. Do not modify files when the user asks only for an explanation, inventory, or compatibility check.
- Preserve unrelated mods and existing user changes. Never replace the whole `mods` directory.
- Use `rg`/`rg --files` for discovery and `apply_patch` for source edits.
- Keep source/build files separate from distributable Workshop content.
- Ask before any destructive cleanup, public publication, or external update unless the user has explicitly requested that exact action.
- Never upload `sts2.dll`, `0Harmony.dll`, game assets, secrets, or arbitrary build output.

## Workflow

### 1. Discover the local game and current mod state

Find the game root rather than assuming a fixed path. On macOS Steam it normally resembles:

```text
.../steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/
  Resources/release_info.json
  Resources/data_sts2_macos_arm64/sts2.dll
  Resources/data_sts2_macos_arm64/0Harmony.dll
  MacOS/mods/
```

On Windows or Linux, locate the equivalent `Resources`/data directory and `mods` directory. Read `release_info.json` and record the game version, commit, and available architecture directories. Prefer the local assembly matching the running game; an AnyCPU .NET DLL is usually portable, but compile against the local API.

Inventory `mods/` before editing. Preserve every existing mod. Identify whether the target mod is a loose folder, a Workshop install, or a manually copied DLL/JSON/PCK set.

For an existing source project, inspect its README, project file, manifest, and Git status before building or editing. Treat project-specific documentation and build settings as the source of truth for its conventions. Distinguish the source checkout, build output, installed runtime copy, and Workshop staging workspace; they may be separate directories and may not contain the same version. Check build targets for side effects: some projects install files as part of `dotnet build`. If doing a read-only review or isolated compile, use the project's supported opt-out (for example, `-p:InstallToGame=false`) so a build does not overwrite the live mod unexpectedly.

### 2. Turn the request into a mod design

Before coding, write down:

1. Mod ID, display name, author, version, minimum game version.
2. Gameplay/UI/content behavior and the exact trigger point.
3. Whether the mod needs a PCK, only a DLL, or no DLL.
4. Multiplayer implications and whether all players need the same version.
5. What counts as success and how to test it in-game.

Keep the ID consistent across the directory, assembly name, manifest JSON, namespace, Harmony ID, and Workshop content. For a new mod, use a dedicated directory such as `mods/MyMod/`; do not mix source files with other mods.

### 3. Inspect the current API before choosing hooks

Use local reflection or a small temporary .NET program when a type, namespace, method overload, parameter name, or enum is uncertain. Inspect `sts2.dll` and, when needed, `0Harmony.dll`. `strings` is useful for locating candidate names, but reflection is required to confirm signatures.

For Harmony patches:

- Match the exact overload with `[HarmonyPatch(typeof(...), nameof(...), new[] { ... })]` when overloads exist.
- Match original parameter names in Prefix/Postfix methods when Harmony binds by name.
- Confirm whether a hook takes `ICombatState` or `CombatState`, and locate the actual `PlayerChoiceContext` namespace.
- Re-run a load test after every API-signature change.

Useful STS2 patterns from the tested builds, which must still be verified locally:

- `MegaCrit.Sts2.Core.Hooks.Hook` exposes lifecycle and draw-related hooks such as `BeforeHandDraw` and `AfterShuffle`.
- `CardPileCmd.Draw` consumes `drawPile.Cards.FirstOrDefault()` after `ShuffleIfNecessary`.
- `CardPilePosition.Top`, `Bottom`, and `Random` describe how cards are added to a pile; do not infer an original “top card” merely from `Cards[0]`.
- `CardPile.AddInternal`, `RemoveInternal`, and `InvokeContentsChanged` can be used for silent, stable reordering when the local version exposes them.

See [references/api-inspection.md](references/api-inspection.md) for a repeatable reflection pattern and compatibility traps.

### Optional shared framework: RitsuLib

RitsuLib is an optional Workshop framework, not a baseline requirement. Do not add it merely
because it exists, and do not migrate a working mod wholesale. First confirm the user accepts
the new subscription dependency and that its focused API removes meaningful custom patching or
state-management risk. When a mod remains dependency-free, continue using the exact local-game
Harmony pattern.

If adopting RitsuLib:

- Inspect the locally installed `STS2-RitsuLib` manifest and compile against the variant matching
  the current game version, such as `lib/<game-version>/STS2-RitsuLib.dll`; inspect its XML API docs for
  the installed version rather than relying on online examples.
- Add a real manifest dependency, e.g. `{ "id": "STS2-RitsuLib", "min_version": "<verified version>" }`.
  Never copy or bundle RitsuLib's loader, DLL, PDB, or versioned `lib/` folder into the mod.
- Keep base-game hooks as the source of truth for sequencing. Use framework hooks only after
  confirming their dispatch order is suitable for the requested behavior.
- Load-test with the RitsuLib loader and the selected versioned library available, then test both
  the requested lifecycle transition and a restart/continue path in-game.

Read [references/ritsulib.md](references/ritsulib.md) only when a task may benefit from RitsuLib.

### 4. Scaffold and implement

For a Harmony-only mod, a minimal project can use `Microsoft.NET.Sdk`, `net9.0`, `AnyCPU`, and explicit references to the local `sts2.dll` and `0Harmony.dll`. Avoid copying those references into the mod output. Use the game/template's Godot SDK only when the mod actually needs Godot nodes or PCK assets.

The manifest normally contains:

```json
{
  "id": "MyMod",
  "name": "My Mod",
  "author": "Author",
  "description": "Short description",
  "version": "0.1.0",
  "min_game_version": "<local version>",
  "has_pck": false,
  "has_dll": true,
  "dependencies": []
}
```

Set `has_pck` to true only when a PCK is actually shipped. Add dependency objects only for real Workshop/mod-loader dependencies. Do not add a fake dependency merely to make a DLL load.

Choose hooks by behavior:

- Patch the narrowest lifecycle hook that runs at the correct time.
- For draw-order behavior, account for both the pre-draw hook and reshuffles that happen inside draw operations.
- For card placement, track the game's explicit placement semantics such as `CardPilePosition.Top`; never use list position as a substitute without proving that the current version does so.
- For UI/assets, create the PCK through the supported Godot/template workflow and test both the DLL and PCK.
- For custom music that must follow room/combat/victory state, consider RitsuLib's adaptive music
  API before writing patches against multiple native music-controller methods. Verify fade-out,
  restoration, reload, and quit behavior in-game.
- For custom metadata such as markers, journals, or settings, consider versioned/profile-scoped
  RitsuLib persistence. Do not replace native `SerializableRun` handling with it unless the full
  serialization and recovery behavior is independently verified.
- For checkpoint or recovery systems, identify which state belongs in the game's native run save and
  which state is mod-owned external metadata. Keep mutable player state out of Workshop content. If
  state storage is moving, copy and verify existing data before changing the read/write location;
  preserve a recoverable source until migration is confirmed. Version serialized formats and handle
  missing, older, or incompatible state explicitly.
- For custom card text, use a valid localization table; RitsuLib's localization bridge is a
  possible route. Do not borrow an unrelated vanilla `LocString` just to make a card render.

### 5. Compile and validate

Compile against the current local version. If no usable .NET SDK is installed, use an official SDK in a task-specific temporary directory; keep `DOTNET_CLI_HOME` in a writable temporary path so first-run setup does not touch protected user directories.

Before compiling an existing project, check whether its build copies files into the live game
directory and disable that behavior for review or isolated builds when the project supports it.

At minimum run:

```bash
dotnet build MyMod.csproj -c Release
```

Then perform a load test in a temporary process that loads the local `0Harmony.dll`, local `sts2.dll`, the built mod DLL, and invokes the `[ModInitializer]` entry point. This catches missing types, wrong namespaces, wrong overloads, and Harmony target failures without requiring a full game launch.

If the load test fails, fix the exact local signature; do not paper over it with broad reflection or silently disable the patch. If possible, launch the game once and inspect its mod log, then test the requested behavior in a fresh run.

For mods with persistent state, recovery, or multi-step narrative flows, a successful initializer load
is not sufficient. Exercise the relevant lifecycle boundaries: fresh state, save/quit/continue,
recovery or reload, and migration from an existing state format when applicable. Check that a failed
or interrupted transition leaves the base game usable and does not silently discard the only copy
of player data. Choose scenarios based on the feature being changed; do not run an unrelated full
regression suite for a narrow edit.

### 6. Install locally and prepare shareable content

Install only the runtime files into `Contents/MacOS/mods/<ModId>/` (or the equivalent platform directory): usually the manifest JSON, DLL, PCK, and required runtime asset folders. Keep `.cs`, `.csproj`, `obj/`, `bin/`, `.pdb`, and temporary reflection tools out of the runtime package unless a specific loader requires them.

For a manual friend share, zip the mod's distributable content. A .NET assembly built AnyCPU is generally platform-neutral; the receiver still needs a compatible STS2 version and the game's mod loader. For multiplayer gameplay mods, recommend matching versions for every player.

### 7. Publish to Steam Workshop

Use Mega Crit's official `sts2-mod-uploader`. The uploader workspace is:

```text
workspace/
  workshop.json
  image.png                 # required, under 1 MB
  content/
    MyMod.dll
    MyMod.json
    MyMod.pck                # only if needed
  mod_id.txt                 # written after first upload
```

`workshop.json` uses `title`, `description`, `visibility`, `changeNote`, `tags`, `dependencies`, and `contentDescriptors`. Use `private` while preparing unless the user explicitly wants public publication. For first publication, use the official uploader while Steam is open and logged in; after it returns a Workshop ID, preserve `mod_id.txt` and reuse the same workspace for updates.

The official command is:

```bash
ModUploader upload -w /absolute/path/to/workspace
```

Before uploading, stage from a clean runtime directory or use the bundled
`scripts/stage_workshop.py`. The staging helper copies root media files and
common asset directories (including non-ASCII `视频/` and `音效/`) and skips
known mutable state files such as `deathless-run.*` and `return-by-death.*`.
Never stage from a live install directory without checking for player state,
logs, backups, or a `待清理/` folder.

If the source project keeps assets under `Resource/`, map them to the runtime
layout explicitly before staging: copy `Resource/图片` files used at runtime
to the content root, and map `Resource/视频` and `Resource/音效` to matching
content subdirectories. Do not upload the `Resource/` wrapper itself unless
the DLL resolves assets through that wrapper.

For every asset layout, compare the code's runtime path construction with the project build's copy
rules and the staged tree. Do not assume that preserving the source tree layout is correct: the
runtime package must match the paths the mod actually reads. Keep debug-only and unreferenced source
assets out when practical, but do not remove assets based only on filenames; confirm references first.

If `ModUploader` is not installed, obtain it from the official
`megacrit/sts2-mod-uploader` repository and build the macOS binary with the
locally available .NET SDK. When the uploader targets an older runtime than
the installed SDK, use `DOTNET_ROLL_FORWARD=LatestMajor` only for launching
the uploader; do not alter the mod's target framework. Keep Steam open and
logged in.

Check the uploader output for the Workshop ID and the final success line.
`k_EItemUpdateStatusInvalid` may appear as an intermediate status while the
upload still finishes successfully; treat the final result and the generated
`mod-uploader.log` as authoritative. For an update, reuse the same workspace
and `mod_id.txt`, replace only the staged runtime content, update
`workshop.json`'s `changeNote`, and upload again. Never create a new workspace
for each release unless the user intends a new Workshop item.

Keep mutable mod state outside the Workshop install directory. If a project is
migrating old state files, copy and verify them before any cleanup; do not put
those files in `content/`, and do not silently delete a player's only copy.

See [references/workshop.md](references/workshop.md) and use [scripts/stage_workshop.py](scripts/stage_workshop.py) to stage runtime files without accidentally including source or game assemblies.

### 8. Report the handoff

State clearly:

- where the local mod was installed;
- what files are distributable;
- game version and mod version tested;
- compile/load/game-test status;
- Workshop ID and URL if published;
- known compatibility assumptions and multiplayer requirements.

If a public upload was not requested, stop at a prepared workspace or private item and ask for the visibility decision.

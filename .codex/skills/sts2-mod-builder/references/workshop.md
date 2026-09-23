# STS2 Workshop publishing reference

Slay the Spire 2 Workshop AppID is `2868840`. Mega Crit's official uploader is published at:

<https://github.com/megacrit/sts2-mod-uploader>

The uploader expects a workspace with `workshop.json`, `image.png`, and `content/`. The preview image must be under 1 MB. The runtime content should contain only files the game needs, normally the mod's JSON manifest, DLL, PCK, and required asset/resource folders.

For mods whose runtime layout uses localized root images or directories such as
`视频/` and `音效/`, verify the staged tree explicitly. The content directory
must not contain source code, `bin/`/`obj/`, logs, `待清理/`, player checkpoints,
or mutable JSON state files. A clean staging pass is safer than copying the
entire installed mod folder, which may contain files created during gameplay.
When the source uses a `Resource/` wrapper, stage the files according to the
paths the DLL actually reads; for example, root images may need flattening to
`content/`, while `Resource/视频` and `Resource/音效` become `content/视频` and
`content/音效`.

## `workshop.json`

```json
{
  "title": "My Mod",
  "description": "What the mod does.",
  "visibility": "private",
  "changeNote": "Initial release",
  "tags": ["Gameplay"],
  "dependencies": [],
  "contentDescriptors": []
}
```

Visibility values include `private`, `public`, `unlisted`, and `friends_only`. Use `private` while iterating. Set `public` only after the user has asked to publish publicly and has approved the title/description.

## First upload

Keep Steam open and logged in. Run:

```bash
/path/to/ModUploader upload -w /absolute/path/to/workspace
```

The uploader creates the item, uploads content and preview, and writes `mod_id.txt`. Record the returned ID and construct:

```text
https://steamcommunity.com/sharedfiles/filedetails/?id=<id>
```

The uploader may report an intermediate status such as `k_EItemUpdateStatusInvalid` while still finishing successfully; trust the final success line and verify the returned ID. If it reports failure, do not assume the item was updated—inspect the uploader log and Steam Workshop state.

## Updates

Reuse the same workspace and `mod_id.txt`. Replace the staged runtime files, update `workshop.json`'s `changeNote`, and run the same command. This updates the existing item rather than creating a duplicate.

Never include the game's `sts2.dll`, `0Harmony.dll`, secrets, source code, `obj/`, or broad game directories. Workshop content is public once published.

If the official uploader is missing on macOS, build it from the official
`megacrit/sts2-mod-uploader` repository with the available local SDK, then run
the published binary with `DOTNET_ROLL_FORWARD=LatestMajor` when necessary.
An intermediate `k_EItemUpdateStatusInvalid` status is not conclusive: inspect
the final uploader line and `mod-uploader.log` before retrying, so a successful
update is not submitted twice.

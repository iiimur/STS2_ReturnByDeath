# RitsuLib integration notes

Use this reference only after deciding that a real RitsuLib dependency is appropriate. Verify all
types and overloads against the installed version's XML documentation and DLL.

## Dependency and versioning

RitsuLib distributes a small loader plus version-specific assemblies under `lib/<game-version>/`.
The current game loader selects the matching variant. Compile against the local variant for the
current STS2 build and declare a normal mod dependency:

```json
"dependencies": [
  { "id": "STS2-RitsuLib", "min_version": "<installed version>" }
]
```

Do not bundle RitsuLib files in the mod or add a dependency to an otherwise standalone mod without
user approval. A Workshop subscriber must install/subscribe to the dependency.

## Focused templates

### Adaptive music

Use `GameAudioService.Shared.FollowAdaptiveMusic(...)` or
`AudioAdaptiveMusicDirector.Shared.Attach(...)` with `AudioAdaptiveMusicPlan` when a custom track
must react to room, combat, and victory lifecycle changes. Sources are created through
`AudioSource`, including `StreamingMusic`, `File`, and FMOD event sources. Configure restoration
and lifecycle behavior explicitly:

- `RestoreVanillaMusicOnStop`
- `RestoreVanillaMusicOnCombatEnd`
- `RefreshVanillaRoomStateOnRoomEnter`
- per-state `AudioPlaybackOptions` (volume, scope, fade-out policy, routing)

Use the returned handle to stop/detach the override. Test combat start, victory settlement, room
entry, save/reload, and quitting while the track is active. Keep short SFX and ambience on their
normal channels; an adaptive music plan is for long-lived BGM state.

### Persistent custom data

Use `PersistentDataEntry<T>` for mod-owned metadata, with `SaveScope.Global` or
`SaveScope.Profile` as appropriate. It exposes `Load`, `Save`, and `Modify`; `MigrationManager`
supports versioned JSON migrations. Suitable examples: one-time event flags, encounter journals,
and user-facing configuration.

Do not use it as a substitute for native full-run snapshots unless independently proving every
run-state field, lifecycle boundary, and recovery path. For run restoration, keep using the game's
`SerializableRun` and save APIs where that is the established feature mechanism.

### Localization and card descriptions

Use `I18NLocTableBridge.TryRegister` with `I18N` to expose a valid mod localization table. Use
this for custom titles, descriptions, keywords, and formatted dynamic variables instead of
redirecting text to an unrelated vanilla card. RitsuLib also offers computed card variables through
`Cards.DynamicVars.ModCardVars` and tooltip registration.

After adding localization, load-test the mod and render the relevant card in every needed context:
deck, hand, reward/transform preview, and card library if applicable.

### Combat hooks

Prefer the narrow shared hook when it matches the desired timing:

- `Cards.CardOnPlayHook`: before/after a card's own `OnPlay`, while preserving the wrapper flow.
- `Combat.Healing.HealHook`: modify heal amount with a context containing creature, original
  amount, missing HP, combat state, and run state.
- `Combat.AttackHits.AttackHitHook`: observe individual attack hits with targets, damage, source
  card, and results.

Confirm the hook's order against vanilla enchantments, afflictions, rewards, and death/victory
resolution before relying on it. A precise direct Harmony patch remains correct when framework
dispatch is too early or too late.

### UI

Use `Settings.ModSettingsRegistry` for settings pages, with toggles, sliders, choices, bindings,
and buttons. Use `ModTopBarButtonRegistry` for a persistent top-bar action. Use
`RitsuToastService` for non-blocking status messages. These are good fits for optional controls
and debug views; retain bespoke Godot UI for narrative scenes or presentation-specific overlays.

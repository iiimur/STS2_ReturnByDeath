# STS2 API inspection and compatibility notes

Use this file when implementing or updating a mod. The installed `sts2.dll` is authoritative for the user's exact game version.

## Locate assemblies

Search for:

```text
Resources/release_info.json
Resources/data_sts2_*/sts2.dll
Resources/data_sts2_*/0Harmony.dll
```

Read `release_info.json` first. Compile against the architecture's `sts2.dll`; for a pure managed mod, set `PlatformTarget` to `AnyCPU` unless the mod uses native code.

## Reflection probe

Create a temporary `net9.0` console project and load the assembly with `Assembly.LoadFrom`. Print the full names of candidate types and methods:

```csharp
var asm = Assembly.LoadFrom(sts2Path);
foreach (var t in asm.GetTypes().Where(t => t.FullName?.Contains("CardPile") == true))
    Console.WriteLine(t.FullName);

var hook = asm.GetType("MegaCrit.Sts2.Core.Hooks.Hook")!;
foreach (var m in hook.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                  BindingFlags.Static | BindingFlags.Instance)
                     .Where(m => m.Name is "BeforeHandDraw" or "AfterShuffle"))
    Console.WriteLine(m);
```

Use reflection to check overloads, parameter names, enum members, and accessibility of methods used by the patch. Delete or leave the probe in a temporary directory, never in the mod's distributable content.

## Harmony pitfalls

- A patch can compile and still fail at runtime if the target overload is ambiguous or missing.
- Harmony may bind original arguments by name. Use the exact names returned by reflection, especially for `PlayerChoiceContext` variants.
- Hook parameter order may differ from the order suggested by online snippets.
- The game may change a concrete type to an interface (`CombatState` versus `ICombatState`) without changing the hook's purpose.
- When an overload gains a parameter, update the `new[] { typeof(...) }` target selector and the Prefix/Postfix signature together.

## Card pile semantics

Current builds expose `CardPilePosition` values such as `Top`, `Bottom`, and `Random`. `CardPileCmd.Draw` generally draws from the first entry in the draw pile after any required shuffle, but that first entry is not automatically an original “top card” in the gameplay sense. If a mod needs to preserve cards intentionally placed at the top, patch or observe the API path that receives `CardPilePosition.Top` and track those cards explicitly.

After silent `CardPile` mutations, invoke the local version's contents-changed notification if it exposes one. Verify the method name and visibility against the installed assembly.

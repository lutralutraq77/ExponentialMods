# Exponential Mods

One rule, applied to every item: **each pickup moves your stack to the next rung of a `base^n` ladder.**

```
base 2   ->  1, 2, 4, 8, 16, 32, 64, 128, 256, ...
base 3   ->  1, 3, 9, 27, 81, 243, 729, 2187, ...
base 10  ->  1, 10, 100, 1000, 10000, ...
```

No categories, no presets, no per-item curves, no risk catalog. Pick a base, pick where it
stops, and every item in the game follows it.

---

## Settings

| Setting | Section | Default | What it does |
|---|---|---|---|
| **Enabled** | Ladder | `true` | Master switch. Off = vanilla +1 per pickup. |
| **Require Artifact** | Ladder | `true` | Gate scaling behind **Artifact of Exponents** so you can toggle it in the lobby. Off = always active. |
| **Base** | Ladder | `2` | The ladder base. Range 2–64. |
| **Show Ladder In Item Descriptions** | Ladder | `true` | One-line summary on item tooltips. |
| **Max Exponent** | Limits | `0` (auto) | Highest `n`, so the top rung is `base^n`. `0` = the largest `n` that still fits under **Max Items**. |
| **Max Items** | Limits | `16777216` | Ceiling on the stack this mod will grant. |
| **Scale &lt;tier&gt;** | Filters | mostly `true` | Per-tier on/off, including food and unknown modded tiers. |
| **Never Scale These Items** | Filters | *(empty)* | Comma-separated internal item names to leave alone. |

### Automatic max exponent

Leaving **Max Exponent** at `0` picks the tallest ladder that fits under **Max Items**:

| Base | Auto `n` | Top rung |
|---|---|---|
| 2 | 24 | 16,777,216 |
| 3 | 15 | 14,348,907 |
| 4 | 12 | 16,777,216 |
| 5 | 10 | 9,765,625 |
| 10 | 7 | 10,000,000 |
| 64 | 4 | 16,777,216 |

Because the top rung must be an exact power, it can settle a little below the ceiling —
base 3 under 16,777,216 tops out at `3^15 = 14,348,907`. Once you are there the mod stops
adding stacks and pickups go back to the vanilla +1. It never takes items away.

---

## Artifact of Exponents

The mod registers an artifact so you can switch scaling on and off per run, from the artifact
list in the lobby — no config editing, no restart.

**Require Artifact** is `true` by default, so out of the box the ladder only applies while the
artifact is enabled. Turn it off in the config if you would rather the mod were simply always
on; the artifact still appears in the list but stops gating anything.

The startup log states which mode is active:

```
Require Artifact is ON: scaling applies only while Artifact of Exponents is enabled for the run.
```

If the artifact ever fails to register (missing R2API, for example), the mod logs the error and
falls back to always-on rather than silently disabling itself.

---

## About the 32-bit limit

Risk of Rain 2 stores item stacks in a **32-bit signed int**. Three separate places in the
vanilla code decide what is actually safe:

1. **`Inventory.ChangeItemStacksCount`** widens to `long` and clamps a single stack with
   `Math.Clamp(count + countToAdd, 0L, 2147483647L)`. So one stack saturates at
   **int.MaxValue = 2,147,483,647** rather than wrapping. That is the absolute maximum
   **Max Items** accepts.

2. **`Inventory.UpdateEffectiveItemStacks`** adds the permanent, channeled and temporary
   stacks of one item together in **plain `int` arithmetic** and only clamps afterwards. A
   permanent stack near int.MaxValue that also receives a temporary copy wraps negative and
   then clamps to **0** — the stack disappears.

3. **`Inventory.GetTotalItemCountOfTier`** sums every stack in a tier into a plain `int`
   with **no clamp at all**.

The default ceiling of **16,777,216 (2^24)** sits far enough below int.MaxValue to keep 2
and 3 out of reach, and it is also the largest integer a 32-bit `float` represents exactly —
so stat math built on stack counts stays precise. You can raise it up to int.MaxValue, but
that is the point where the vanilla overflows above become reachable.

Internally the mod never performs an unchecked `(int)` cast: every rung is computed in
`long` and saturated at the ceiling.

---

## When the ladder applies

A pickup is accelerated only when **all** of these hold:

- **Enabled** is on.
- **Artifact of Exponents** is enabled for the run — unless **Require Artifact** is off.
- The game is granting exactly **one** stack. Bulk grants (command essence, scrappers,
  other mods handing out piles) pass through untouched.
- The code is running on the **server** — `Inventory.GiveItemPermanent` is `[Server]`-only in
  vanilla, so on a client it warns and returns without granting anything.
- The item's **tier** is enabled under Filters.
- The item is not in **Never Scale These Items**.

Otherwise you get ordinary Risk of Rain 2 behaviour.

---

## Requirements

- BepInEx (`bbepis-BepInExPack`)
- HookGenPatcher (`RiskofThunder-HookGenPatcher`) — provides the `MMHOOK_RoR2` assembly
- R2API Core, Language and ContentManagement — used to register the artifact

## Install

Drop `ExponentialMods.dll` into `BepInEx/plugins/`, or install the package through
r2modman / Thunderstore Mod Manager.

Config file: `BepInEx/config/com.lutralutra.exponentialmods.cfg`

---

## Building

The mod compiles against Risk of Rain 2, Unity, BepInEx and MMHOOK assemblies. **Those are
not redistributable, so they are not in this repository** — `libs/` is gitignored. Populate it
from your own installation:

From `<Risk of Rain 2>/Risk of Rain 2_Data/Managed/`:

```
RoR2.dll   Assembly-CSharp.dll   UnityEngine*.dll   com.unity.multiplayer-hlapi.Runtime.dll
```

From your BepInEx profile (r2modman puts these under `profiles/<name>/BepInEx/`):

```
core/BepInEx.dll        plugins/MMHOOK/MMHOOK_RoR2.dll
```

Do **not** copy `mscorlib.dll`, `netstandard.dll`, `System.dll` or `System.Core.dll` from the
Unity folder — they collide with the netstandard2.1 reference assemblies and the build fails
with a thousand "predefined type is not defined" errors.

Then:

```bash
dotnet build -c Release
```

The output `ExponentialMods.dll` goes in `BepInEx/plugins/`.

## Tests

The ladder maths is pure and has no Unity or RoR2 dependency, so it runs standalone:

```bash
dotnet run -c Release --project tests/LadderTests
```

It checks that each base lands on exact powers, that the ladder climbs strictly to its summit
and then holds, that nothing ever exceeds the ceiling or overflows a 32-bit int, and that
hostile inputs (negative stacks, base < 2, oversized bases, a stack already at `int.MaxValue`)
are all handled.

## Implementation note: hook `GiveItemPermanent`, not `GiveItem`

Worth recording for anyone writing a similar mod. The obvious targets for intercepting an item
grant are `Inventory.GiveItem` and `GenericPickupController.AttemptGrant`. **Both are traps in
current Risk of Rain 2:**

- `Inventory.GiveItem` is marked `[Obsolete("Use .GiveItemPermanent instead.", false)]`. The
  game's own code no longer calls it, so a hook there fires almost never.
- `GenericPickupController.AttemptGrant` is not on the Artifact of Command path — those grants
  go through `PickupPickerController.HandlePickupSelected` instead. It is also a crowded hook
  target that many character mods attach to.

Every public grant overload funnels into one method:

```
GiveItem(ItemIndex, int)         ─┐
GiveItem(ItemDef, int)           ─┼─→  GiveItemPermanent(ItemIndex, int) → ChangeItemStacksCount
GiveItemPermanent(ItemDef, int)  ─┘
```

So `Inventory.GiveItemPermanent(ItemIndex, int)` is the single chokepoint covering ground
pickups, Command, printers, scrappers and cauldrons alike. The only paths that bypass it are
the `AddItemsFrom` bulk-copy overloads, which a stacking mod should leave alone anyway.

## License

MIT — see [LICENSE](LICENSE).

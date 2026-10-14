# Mounted Weapons: code walkthrough

Written against build 0010 (commit 77633fa). If the code changes after that, the line-level details here can go stale; the shape usually won't.

This document assumes you know C# and have not written a RimWorld mod. Section 0 covers the RimWorld ideas the rest depends on. After that the sections follow the order the game runs things: startup, the XML, choosing a weapon, the mount itself, the two Harmony patches, and drawing. Each section ends with something you can try to check your understanding.

Vanilla code quoted here comes from the decompiled RimWorld 1.6 source. When this document says "vanilla", it means Ludeon's code in `Assembly-CSharp.dll`.

---

## Contents

0. RimWorld concepts you need first
1. The file map
2. Startup: `Mod.cs`, `MountSettings.cs`, `MWDefOf.cs`
3. The XML: how defs pick the C# classes
4. Choosing and installing a weapon
5. The mount: `Building_WeaponMount.cs`
6. The Harmony patches: `HarmonyPatches.cs`
7. Drawing: arcs and weapon centring (`FiringArc.cs`)
8. The settings window (`Mod.cs`, second half)
9. Bugs that were found and fixed, and why the code looks the way it does
10. Known limits
11. Where to change what

---

## 0. RimWorld concepts you need first

**Defs.** Almost everything in RimWorld is described in XML as a *Def*: a `ThingDef` for every item, building and creature, a `JobDef` for each kind of job, a `ResearchProjectDef`, and so on. At startup the game reads all XML from every active mod and builds one C# object per def, stored in `DefDatabase<T>`. A def is shared data: there is one `ThingDef` for "weapon mount", and every weapon mount on every map points at it. Change a field on the def and every instance sees the change.

**Things.** An actual object in the world is a `Thing` (or a subclass: `Building`, `Pawn`, `ThingWithComps`...). Its `def` field points to its `ThingDef`. The XML field `<thingClass>` tells the game which C# class to instantiate for that def. That is the main hook this mod uses: the mount defs say `<thingClass>MountedWeapons.Building_WeaponMount</thingClass>`, so every mount in the world is an instance of our class.

**Comps.** `ThingWithComps` holds a list of `ThingComp`s, small behaviour modules declared in XML under `<comps>`. Vanilla has `CompMannable` (a colonist can man this), `CompPowerTrader` (uses power), `CompEquippable` (a pawn can equip this, and it owns the weapon's verbs), `CompQuality`, and so on. The XML lists a `CompProperties_X` and the game creates the matching comp on each instance.

**DefModExtension.** A way to attach your own data to someone else's def type without subclassing it. `MountExtension` is one: the mount XML has a `<modExtensions>` block, and the code reads it with `def.GetModExtension<MountExtension>()`. It's just a class with public fields that the XML loader fills in by name.

**Verbs.** A `Verb` is "a way of attacking". A gun's `<verbs>` list in XML becomes `VerbProperties` (shared, on the def) and, per gun instance, `Verb` objects (owned by the gun's `CompEquippable`, through its `VerbTracker`). `Verb.caster` is whoever is firing. `Verb.TryStartCastOn(target)` starts a burst. `Verb_LaunchProjectile` and `Verb_Shoot` spawn projectiles; `Verb_ShootBeam` is beam weapons.

**Ticks.** The game advances in ticks, 60 per second at normal speed. Anything with `<tickerType>Normal</tickerType>` gets its `Tick()` method called every tick. Code that runs every tick needs to be cheap.

**Jobs, toils and work givers.** A pawn does things through *jobs*. A `WorkGiver` is asked "is there work for this pawn?" when the pawn is looking for something to do, and returns a `Job`. The job's `JobDriver` yields a sequence of *toils* (go there, pick this up, wait, do the thing). `WorkGiverDef` in XML ties a work giver to a work type such as Hauling, so the player's work tab decides who does it.

**Saving (Scribe).** Each `Thing` has `ExposeData()`, called both when saving and when loading. Inside it, `Scribe_Values.Look(ref field, "name")` writes the field when saving and reads it back when loading. `Scribe_Deep` saves a whole object inside this one; `Scribe_References` saves only a reference to a thing that is saved somewhere else. After loading, `ExposeData` is called once more with `Scribe.mode == LoadSaveMode.PostLoadInit`, which is where you rebuild anything that wasn't saved.

**Startup order.** This matters for several design choices:
1. The game constructs each mod's `Mod` subclass. Defs are not loaded yet.
2. XML is loaded, patched and turned into defs.
3. `[DefOf]` classes get their static fields filled with the matching defs.
4. Classes marked `[StaticConstructorOnStartup]` have their static constructors run. Defs exist by now, and this is on Unity's main thread, so loading textures is allowed.

**Harmony.** A library that lets a mod change the behaviour of a method in another assembly (vanilla or another mod) at runtime. A *prefix* runs before the original method, a *postfix* runs after it. Section 6 goes through both of ours in detail.

---

## 1. The file map

| File | Lines | Job |
|---|---|---|
| `Source/Building_WeaponMount.cs` | 590 | The mount building. Most of the logic. |
| `Source/Mod.cs` | 190 | Mod entry point, Harmony bootstrap, settings window. |
| `Source/MountSettings.cs` | 160 | Settings data, defaults, applying settings to defs. |
| `Source/FiringArc.cs` | 150 | Arc maths, arc outline, placement preview, weapon-art centring. |
| `Source/HarmonyPatches.cs` | 80 | The two Harmony patches. |
| `Source/MountUtility.cs` | 75 | Which weapons may be mounted. |
| `Source/JobDriver_InstallMountedWeapon.cs` | 55 | The install job's steps. |
| `Source/MountExtension.cs` | 50 | Per-mount numbers from XML. |
| `Source/WorkGiver_InstallMountedWeapon.cs` | 45 | Finds install work for haulers. |
| `Source/MWDefOf.cs` | 25 | Typed handles to our JobDef and textures. |
| `Source/Verb_MountEmpty.cs` | 15 | The do-nothing verb on an empty mount. |
| `Defs/...`, `Biotech/Defs/...` | | Buildings, research, the job, the placeholder gun. |
| `LoadFolders.xml` | | Loads `Biotech/` only when Biotech is active. |
| `Languages/English/Keyed/MountedWeapons.xml` | | Every string the C# shows the player. |

---

## 2. Startup

### 2.1 `Mod.cs`: the entry point

```csharp
public class MountedWeaponsMod : Mod
{
    public const string HarmonyId = "saintwacko.MountedWeapons";
    public static MountSettings Settings;

    public MountedWeaponsMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<MountSettings>();
        new Harmony(HarmonyId).PatchAll();
    }
```

RimWorld finds every non-abstract subclass of `Verse.Mod` in a mod's assemblies and constructs it once, very early (step 1 of the startup order above). Two things happen here:

- `GetSettings<MountSettings>()` reads the mod's settings file from disk by calling `MountSettings.ExposeData()` in load mode. The file is `Config/Mod_<folder>_MountedWeaponsMod.xml`, where `<folder>` is the mod's folder name, not its packageId (`LoadedModManager.GetSettingsFilename`). So your local copy (`f5542185d692`) and the Workshop copy (a Steam id number) keep separate settings files. The result is kept in a static so the rest of the code can reach it as `MountSettings.Get`.
- `new Harmony(id).PatchAll()` scans this assembly for classes marked `[HarmonyPatch]` and applies them. The id is just a name that identifies our patches, used by Harmony and by other mods that want to check whether ours are present.

Neither line touches defs. That matters: at this point `MWDefOf.MW_InstallMountedWeapon` is still null, and reading a def here is a classic way to crash a mod at startup.

`SettingsCategory()` is the name in Options > Mod settings. `WriteSettings()` is called when the player closes the settings window; ours saves and then calls `Settings.Apply()` so changes reach the defs and existing mounts immediately.

### 2.2 `MountSettings.cs`: data model

Two classes and a startup hook.

**`MountTuning`** holds the four numbers a player can change per mount: range multiplier, stability, aim-time factor and (automated only) turret accuracy. It implements `IExposable`, which is what lets `Scribe` save it inside the settings file. `Copy()` and `SameAs()` exist for the settings window (section 8).

**`MountSettings : ModSettings`** holds the global values (two arcs, core-loss chance, maximum weapon damage, the centring checkbox) and `tunings`, a `Dictionary<string, MountTuning>` keyed by defName.

The important idea is **the XML is the default, and only player changes are saved**:

- `Defaults` is a static dictionary filled at startup from what the defs say. It is never saved.
- `tunings` only contains entries for mounts whose numbers the player changed.
- `Apply()` uses the saved entry if there is one, otherwise the default.

Why bother? If the settings file saved every value, then any later change to the XML (a balance pass you make in v1.1, or another mod patching our defs) would be silently overridden by the old numbers in every player's settings file. With this design, a mount the player never touched follows whatever the XML currently says.

**`Apply()`** is the method that makes settings take effect:

```csharp
foreach (ThingDef def in MountDefs)
{
    if (!tunings.TryGetValue(def.defName, out MountTuning t) && !Defaults.TryGetValue(def.defName, out t)) continue;
    MountExtension ext = def.GetModExtension<MountExtension>();
    ext.rangeMultiplier = t.rangeMultiplier;
    ext.stability = t.stability;
    ext.warmupFactor = t.warmupFactor;
    if (!IsManned(def))
        StatUtility.SetStatValueInList(ref def.statBases, StatDefOf.ShootingAccuracyTurret, t.turretAccuracy);
}
```

It writes the chosen values straight onto the def's `MountExtension` and, for automated mounts, onto the def's `ShootingAccuracyTurret` stat. Because defs are shared (section 0), every mount reads the new values on its next shot. Then it calls `Notify_SettingsChanged()` on every mount on every map, because one value (range) is copied per mount and has to be refreshed (section 5.3).

`MountDefs` finds our defs by asking "which ThingDefs have a `MountExtension` and a `thingClass` that is `Building_WeaponMount` or a subclass?" so no defName list has to be kept in sync. `IsManned` is "does the def have a `CompMannable`?" and decides which arc applies and which settings tab a mount appears on.

`ExposeData()` clamps the loaded values, so a hand-edited or corrupted settings file can't produce a negative arc.

**`MountSettingsStartup`** is a `[StaticConstructorOnStartup]` class, so it runs after defs exist (step 4). It:
1. Records each mount def's XML values into `Defaults`.
2. Removes any saved entry that equals its default. That entry carries no player choice, and removing it lets future XML changes through. (Early test builds saved an entry just for opening the tab; this also cleans those up.)
3. Calls `Apply()`.

### 2.3 `MWDefOf.cs`

```csharp
[DefOf]
public static class MWDefOf
{
    public static JobDef MW_InstallMountedWeapon;
    static MWDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(MWDefOf)); }
}
```

A `[DefOf]` class is the standard way to get a typed reference to a def. After defs load, RimWorld fills each static field with the def whose defName equals the field name. The static constructor line is boilerplate that logs an error if something touches the class too early.

`MWTex` loads the three gizmo icons. `ContentFinder<Texture2D>.Get` must run on the main thread after content loads, which `[StaticConstructorOnStartup]` guarantees. (The Circinus launcher warns that these paths have "no string literal in .cs". They do; its scanner probably misses the fully-qualified `ContentFinder<UnityEngine.Texture2D>` form. The icons load fine in game.)

### Try it
Open Options > Mod settings > Mounted Weapons, move the weapon mount's range to 150%, close the window, then find `Mod_f5542185d692_MountedWeaponsMod.xml` in the Config folder. During a Circinus test launch that is the isolated one (`CircinusDevSuite/test-savedata/Config/`), not your normal RimWorld config. Only the weapon mount should have a `tunings` entry. Press "Reset this tab" and it disappears.

---

## 3. The XML: how defs pick the C# classes

### 3.1 Inheritance in XML

`Defs/ThingDefs_Buildings/MW_MountBases.xml` defines two **abstract** defs (`Abstract="True"` means "template only, never a real def"):

- `MW_WeaponMountBase` (parent `BuildingBase`, a vanilla template): everything every mount shares.
- `MW_MannedMountBase` (parent `MW_WeaponMountBase`): adds the gunner.

A concrete def says `ParentName="MW_MannedMountBase"` and inherits everything, adding its own label, cost, stats and `MountExtension`. Lists like `<comps>` are merged by default (child entries are appended to the parent's), which is why the Biotech auto-mounts can add `CompProperties_Power` on top of their base's comps.

Fields worth understanding in `MW_WeaponMountBase`:

- `<thingClass>MountedWeapons.Building_WeaponMount</thingClass>`: our class. Modded types in XML need the namespace.
- `<drawerType>MapMeshAndRealTime</drawerType>`: the base art is baked into the map's static mesh (cheap), and `DrawAt` is also called every frame for the moving parts (our weapon).
- `<building><turretGunDef>MW_EmptyMountGun</turretGunDef>`: vanilla turrets create their gun from this def. Ours is a placeholder (3.3).
- `<turretBurstWarmupTime>0</turretBurstWarmupTime>`: tells vanilla not to run its own fixed warmup, because the mount runs the weapon's own aim time in code (5.5).
- `<turretBurstCooldownTime>-1</turretBurstCooldownTime>`: same idea for cooldown; the mount overrides `BurstCooldownTime()` anyway.
- `<placeWorkers><li>MountedWeapons.PlaceWorker_FiringArc</li>`: draws the arc while placing a blueprint (7.3).
- `<minifiedDef>MinifiedThing</minifiedDef>`: the mount can be uninstalled and moved.

`MW_MannedMountBase` adds:
- `<hasInteractionCell>` with `<interactionCellOffset>(0,0,-1)</interactionCellOffset>`: the gunner stands one cell behind (offsets are for a north-facing building and rotate with it).
- `CompProperties_Mannable` with `<manWorkType>Violent</manWorkType>`: pawns incapable of violence can't man it.
- `<playTargetAcquiredSound>false</playTargetAcquiredSound>`: switches off the turret beep (vanilla checks this flag in `TryStartShootSomething`).

### 3.2 The concrete mounts

`MW_MannedMounts.xml` has three tiers; `Biotech/Defs/ThingDefs_Buildings/MW_AutoMounts.xml` has four more. The numbers that matter in play live in `<modExtensions>`:

```xml
<li Class="MountedWeapons.MountExtension">
  <rangeMultiplier>1.15</rangeMultiplier>
  <stability>0.2</stability>
  <warmupFactor>1.0</warmupFactor>
</li>
```

The auto-mounts set their accuracy through the vanilla stat `<ShootingAccuracyTurret>` in `statBases` (the same stat vanilla turrets use), and add:
- `<coreDef>`: which core to refund.
- `<building><leavingsBlacklist>`: stops vanilla's normal deconstruct refund (which returns a random share of the cost) from ever rolling the core, because the mount refunds it itself (5.8).
- `CompPowerTrader`, `CompFlickable`, `CompBreakdownable`, `CompStunnable`: vanilla behaviour for powered turrets. `Building_TurretGun.Active` checks power, flick and breakdown, so an unpowered auto-mount stops firing without any code of ours.
- `<allowForcedTarget>` on the high and persona tiers.

### 3.3 The placeholder gun

`Building_TurretGun` assumes `gun` is never null: it calls `gun.TryGetComp<CompEquippable>()` in many places. Rather than guarding every one of those (which would mean patching vanilla), an empty mount holds `MW_EmptyMountGun`, a turret-gun def whose only verb is `Verb_MountEmpty`:

```csharp
public class Verb_MountEmpty : Verb
{
    public override bool Available() => false;
    protected override bool TryCastShot() => false;
}
```

Vanilla's `TryStartShootSomething` returns early when `!AttackVerb.Available()`, so an empty mount never looks for targets. `HasWeapon` in the mount is just "is `gun` something other than the placeholder?".

### 3.4 `LoadFolders.xml`

```xml
<v1.6>
  <li>/</li>
  <li IfModActive="Ludeon.RimWorld.Biotech">Biotech</li>
</v1.6>
```

When a mod has this file, RimWorld loads only the listed folders for that game version. The root is always loaded; `Biotech/` (auto-mounts and their research) only when Biotech is active. Without Biotech the subcores don't exist, so the auto-mount defs would fail to load. Skipping the folder avoids that without any C#. The settings window hides the Automated tab when no automated defs exist.

### 3.5 Research and the job

`Defs/ResearchProjectDefs/MW_Research.xml` adds a research tab and three projects; the Biotech folder adds a fourth. `Defs/Jobs/MW_Jobs.xml` defines the `JobDef` (names the driver class) and the `WorkGiverDef` (puts the work under Hauling, priority 135 within Hauling, requires Manipulation).

### Try it
Without changing any C#, change `MW_WeaponMount`'s `<rangeMultiplier>` to `2.0` and launch. The mount's inspect pane shows the doubled range, because no settings entry overrides it. That is the "XML is the default" rule from 2.2.

---

## 4. Choosing and installing a weapon

The flow, start to finish:

1. The player selects a mount and clicks **Choose weapon** (a gizmo from `GetGizmos`).
2. A float menu lists eligible weapons on the map (`WeaponChoices`). Clicking one sets `mount.pendingWeapon`.
3. When a hauler looks for work, `WorkGiver_InstallMountedWeapon` sees a mount with a `pendingWeapon` and gives them the install job.
4. `JobDriver_InstallMountedWeapon` walks to the weapon, picks it up, walks to the mount, waits 3 seconds, then calls `mount.InstallWeapon(weapon)`.

### 4.1 `MountUtility.cs`: what may be mounted

`IsMountableDef(def)` answers "could this kind of weapon ever go on a mount?" The answer depends only on the def, so it is cached in a dictionary. A def qualifies when all of these hold:

- `def.IsRangedWeapon` and `equipmentType == Primary`.
- It has `CompEquippable` or a subclass (`HasComp<CompEquippable>()`). The hellcat rifle uses a subclass, which is why it was refused before build 9 (section 9).
- Not `destroyOnDrop` and not tagged `TurretGun`: excludes turrets' built-in guns and mech-only guns that can't exist as items.
- Not in the `Grenades` category.
- Its primary verb has a range, isn't `Verb_ShootOneUse` (doomsday and triple rocket launchers, which are consumed on firing), and is a `Verb_LaunchProjectile` or `Verb_ShootBeam`. Anything else (a modded verb that does something exotic) is refused, because the mount's firing code assumes a normal gun.

`CanMount(weapon, out reason)` adds the per-item check: a biocoded weapon is refused, since the mount isn't the pawn it's coded to.

Nothing here checks pawn-only restrictions such as VFE Pirates' warcasket-only guns or Dead Man's Switch heavy weapons. Those mods enforce their rules when a *pawn* equips something, and a mount never equips anything, so their guns pass.

### 4.2 The menu (`Building_WeaponMount.WeaponChoices`)

```csharp
IEnumerable<Thing> candidates = Map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon)
    .Where(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer) && MountUtility.CanMount(t, out _))
    .OrderBy(t => t.def.label)
    .ThenByDescending(t => t.TryGetQuality(out QualityCategory q) ? (int)q : -1);
```

`listerThings.ThingsInGroup(Weapon)` is vanilla's pre-built index of weapons on the map, so this doesn't scan every thing. `Spawned` excludes weapons a pawn is holding (equipped items are not spawned). The list is sorted by name, best quality first.

Then it builds `claimed`: every *other* player mount's `pendingWeapon`, mapped to that mount. Claimed weapons are sorted to the bottom and added as disabled options (an `action` of `null` is how a `FloatMenuOption` is greyed out), labelled "(chosen for weapon mount)". Hovering any option highlights the weapon on the map, and for a claimed one, the mount that claimed it too.

Note what selecting a weapon does: only `pendingWeapon = local`. Nothing is reserved or moved yet. The weapon stays where it is until a hauler takes the job.

### 4.3 The other gizmos

`GetGizmos()` first yields everything the base turret yields (hold fire, force attack, and so on), then, for player-owned mounts only:

- **Choose weapon**: always shown; opens the menu above.
- **Cancel**: shown while a weapon is pending; clears `pendingWeapon` on every selected mount.
- **Remove weapon**: shown when a weapon is installed; takes it off and drops it at the gunner's cell (or the mount's own cell for auto-mounts).

RimWorld merges identical gizmos when several things are selected and runs the action once, on one of them. `SelectedMounts()` therefore collects every selected mount so Cancel and Remove act on all of them.

### 4.4 `WorkGiver_InstallMountedWeapon.cs`

A `WorkGiver_Scanner` is asked, in order:

- `ShouldSkip(pawn)`: a cheap early-out. True when no player mount has a pending weapon, which is nearly always, so the rest rarely runs.
- `PotentialWorkThingsGlobal(pawn)`: the things that might be work. Here: player mounts with a pending weapon.
- `HasJobOnThing(pawn, mount)`: can *this* pawn do the work on *this* mount right now? The weapon must still be spawned on this map, neither thing forbidden, the mount not burning, the pawn able to reach and reserve the weapon, and the weapon still mountable.
- `JobOnThing(pawn, mount)`: makes the job with target A = mount, target B = weapon.

The XML puts this under Hauling, so it follows the player's Hauling priorities like any other hauling job.

### 4.5 `JobDriver_InstallMountedWeapon.cs`

```csharp
public override bool TryMakePreToilReservations(bool errorOnFailed)
{
    return pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);
}
```

Reservations stop two pawns from working on the same thing. Only the weapon is reserved. The mount is deliberately *not* reserved: a gunner manning it already holds a reservation on it, and a weapon should be swappable while someone is manning the mount.

`MakeNewToils()` is an iterator that yields the steps:

1. Fail conditions for the whole job: the mount is gone or forbidden, or `pendingWeapon` is no longer this weapon (the player cancelled or chose another).
2. `Toils_Goto.GotoThing(B)`: walk to the weapon.
3. `Toils_Haul.StartCarryThing(B)`: pick it up. It is now inside `pawn.carryTracker`, not on the map.
4. `Toils_Goto.GotoThing(A)`: walk to the mount.
5. `Toils_General.Wait(180)` with a progress bar: 3 seconds of installing.
6. A custom instant toil: check the pawn is still carrying this weapon, remove it from the carry tracker, call `Mount.InstallWeapon(carried)`.

If the job fails partway, vanilla deals with the carried weapon the way it does for any interrupted haul. `pendingWeapon` stays set, so once the weapon is back on the ground another hauler can take the job.

`Building_WeaponMount.PendingWeaponStillValid()` is checked every 250 ticks in the mount's `Tick` and clears `pendingWeapon` if the weapon was destroyed, left the map, or was equipped by someone. It treats "being carried by a pawn on this map" as still valid, because that is what happens mid-install.

### Try it
Choose a weapon for a mount, then draft every colonist. Nothing happens, since drafted pawns don't take work. Undraft one with Hauling enabled and watch them fetch it. Then select the mount while it's pending and read the inspect pane: the "waiting for" line comes from `GetInspectString`.

---

## 5. The mount: `Building_WeaponMount.cs`

### 5.1 The core trick

`Building_WeaponMount` extends vanilla's `Building_TurretGun`, the class behind the mini-turret, autocannon and mortar. A vanilla turret creates its own gun item from `turretGunDef`, keeps it in a public field `gun`, and fires that gun's verb. It saves the gun with `Scribe_Deep`, so the gun is a complete item saved inside the building.

The mod's whole approach is: **put the player's real weapon in `gun`**. Because `gun` is an ordinary `Thing`, the weapon keeps its quality, hit points, art, unique traits, comps and everything else, and it saves and loads with the building with no extra code. Taking it out gives back the same item.

Most of the rest of this file deals with the places where a real weapon differs from a turret gun:

| Difference | Where it's handled |
|---|---|
| Range should be boosted, for this weapon only | Range clones (5.3) |
| Each weapon has its own aim time, and the gunner's traits affect it | Warmup (5.5) |
| Each weapon has its own cooldown, affected by quality | `BurstCooldownTime` (5.5) |
| The gunner's skill should matter | Harmony postfix (6.1) |
| The gunner should learn Shooting | `GiveGunnerShootingXp` (5.5) |
| Quality and unique traits should affect damage | Harmony prefix (6.2) |
| A manned mount should only fire forwards | Firing arc (5.6) |
| The turret top should look like the weapon | `DrawAt` (7.4) |

### 5.2 Fields and small properties

```csharp
public Thing pendingWeapon;                      // chosen, not yet installed
private int fireReadyTick = -1;                  // when the current warmup ends (5.5)
private readonly List<VerbProperties> appliedProps; // range clones on the weapon's verbs (5.3)
private readonly List<float> baseRanges;         // each clone's original range
private MountExtension extInt;                   // cached def extension
```

- `Ext` returns the def's `MountExtension` (or an empty one if the XML forgot it, so nothing crashes). It caches the *reference*, and the settings code edits that same object, so cached or not, the values are always current.
- `HasWeapon` is "`gun` is not the placeholder". `MountedWeapon` is `gun` or null.
- `Gunner` is `mannableComp?.ManningPawn`. `mannableComp` is a vanilla field on `Building_TurretGun`, null for auto-mounts.
- `TurretTopMaterial` returns a clear material, so the vanilla turret top (normally the gun def's art) draws as nothing. `DrawAt` draws the weapon instead.
- `CanSetForcedTarget` decides whether the Attack gizmo works. Vanilla allows it for manned turrets with a colonist gunner. The mount adds "or the extension's `allowForcedTarget` is true and we're the player's" (high and persona auto-mounts), and requires a weapon either way.
- `HideForceTargetGizmo` hides Attack on an empty mount.
- `DropCell` is the interaction cell if there is one (where the gunner stands), otherwise the mount's own cell.

### 5.3 Putting a weapon on and taking it off

The weapon's `Verb` objects belong to the weapon's `CompEquippable`. When a pawn equips a gun, vanilla sets each verb's `caster` to the pawn. For a turret, vanilla's `UpdateGunVerbs` sets `caster` to the building and `castCompleteCallback` to `BurstComplete` (which starts the cooldown). The mount does the same in `PrepareMountedWeapon`, plus the range boost:

```csharp
foreach verb in eq.AllVerbs:
    verb.caster = this;
    verb.castCompleteCallback = BurstComplete;
    if (!appliedProps.Contains(verb.verbProps))
    {
        VerbProperties clone = verb.verbProps.MemberwiseClone();
        clone.range = verb.verbProps.range * Ext.rangeMultiplier;
        baseRanges.Add(verb.verbProps.range);
        verb.verbProps = clone;
        appliedProps.Add(clone);
    }
```

**Why clone?** `verb.verbProps` points at the `VerbProperties` object on the *def*, shared by every assault rifle in the game. Setting its `range` directly would make every assault rifle, including those carried by raiders, longer-ranged. A shallow copy (`MemberwiseClone`) gives this one weapon its own properties object with a different range and everything else identical. `appliedProps` remembers which objects are our clones, so calling this twice doesn't stack the bonus.

`baseRanges[i]` remembers the original range of clone `i`. `RescaleRanges()` sets each clone's range to `baseRanges[i] * Ext.rangeMultiplier`; it runs on spawn and when settings change, so a new range setting reaches weapons already mounted.

The clones are not saved. On load, vanilla rebuilds the weapon's verbs from the def, and `ExposeData` calls `PrepareMountedWeapon()` again in `PostLoadInit` to put fresh clones on.

**`InstallWeapon(weapon)`**: drops any existing weapon, assigns `gun = weapon`, clears the clone lists, prepares the new weapon's verbs, clears targeting and cooldown, and clears `pendingWeapon` if this was it. The caller (the job driver) must already have removed the weapon from the pawn carrying it; a `Thing` can only be in one container at a time.

**`RemoveWeapon()`**: replaces the weapon's `VerbTracker` with a new one. That one line discards the verbs with our caster, callback and range clone; vanilla creates fresh verbs from the def the next time anything asks, so a pawn equipping it gets a normal gun. Then `MakeGun()` (vanilla) recreates the placeholder, and the weapon is returned unspawned for the caller to place.

### 5.4 How a vanilla turret decides to shoot

This is vanilla's `Building_TurretGun.Tick`, trimmed to the parts that matter here:

```csharp
protected override void Tick()
{
    base.Tick();
    if (forcedTarget.IsValid && !CanSetForcedTarget) ResetForcedTarget();
    ...
    if (Active && (mannableComp == null || mannableComp.MannedNow) && !IsStunned && Spawned)
    {
        GunCompEq.verbTracker.VerbsTick();          // advances a burst in progress
        if (AttackVerb.state == VerbState.Bursting) return;
        if (WarmingUp)                               // burstWarmupTicksLeft > 0
        {
            burstWarmupTicksLeft--;
            if (burstWarmupTicksLeft <= 0) BeginBurst();
        }
        else
        {
            if (burstCooldownTicksLeft > 0) burstCooldownTicksLeft--;
            if (burstCooldownTicksLeft <= 0 && this.IsHashIntervalTick(15))
                TryStartShootSomething(canBeginBurstImmediately: true);
        }
        top.TurretTopTick();                         // turns the top toward the target
    }
    else ResetCurrentTarget();
}
```

And `TryStartShootSomething`, trimmed:

```csharp
if (!Spawned || holdFire... || !AttackVerb.Available()) { ResetCurrentTarget(); return; }
currentTargetInt = forcedTarget.IsValid ? forcedTarget : TryFindNewTarget();
if (newly acquired && def.building.playTargetAcquiredSound) play the beep;
if (currentTargetInt.IsValid)
{
    float warmup = def.building.turretBurstWarmupTime.RandomInRange;
    if (warmup > 0) burstWarmupTicksLeft = warmup.SecondsToTicks();
    else if (canBeginBurstImmediately) BeginBurst();
    else burstWarmupTicksLeft = 1;
}
```

Vanilla's `BeginBurst` is two lines: `AttackVerb.TryStartCastOn(CurrentTarget); OnAttackedTarget(CurrentTarget);`. When the burst ends, the verb calls `castCompleteCallback`, which is `BurstComplete`, which sets `burstCooldownTicksLeft` from `BurstCooldownTime()`.

So a turret's cycle is: every 15 ticks look for a target, warm up for a time set on the *building def*, fire a burst, cool down, repeat. The mount reuses this loop and changes three of the methods it calls: `BeginBurst`, `BurstCooldownTime` and `TryFindNewTarget`.

### 5.5 Warmup, firing, cooldown and XP

**The problem.** Vanilla turret warmup comes from the building def, one value for all shots. A mount should use the mounted weapon's own `warmupTime` (a sniper rifle aims longer than an SMG), scaled by the mount (`warmupFactor`) and by the gunner's `AimingDelayFactor` stat (Careful shooter, Trigger-happy). There is no virtual method for "how long is the warmup", so the mount sets `turretBurstWarmupTime` to 0 in XML and runs the warmup itself inside `BeginBurst`.

**How.** With warmup 0 in the def, vanilla calls `BeginBurst()` straight after acquiring a target. It also calls `BeginBurst()` when `burstWarmupTicksLeft` counts down to zero. The mount's override tells these two calls apart with `fireReadyTick`:

```csharp
protected override void BeginBurst()
{
    int now = Find.TickManager.TicksGame;
    if (fireReadyTick >= 0 && now >= fireReadyTick && now <= fireReadyTick + 2)
    {
        Fire();                    // second call: our warmup just ended
        return;
    }
    int warmup = WarmupTicks();
    if (warmup <= 0) { Fire(); return; }
    burstWarmupTicksLeft = warmup; // vanilla's Tick counts this down...
    fireReadyTick = now + warmup;  // ...and calls BeginBurst again at this tick
}
```

A timeline for a weapon with a 1.5 s (90 tick) warmup, at tick 1000:

| Tick | What happens |
|---|---|
| 1000 | `TryStartShootSomething` finds a target, calls `BeginBurst`. No ready tick, so warmup starts: `burstWarmupTicksLeft = 90`, `fireReadyTick = 1090`. |
| 1001 to 1089 | Vanilla `Tick` decrements `burstWarmupTicksLeft`. The top turns toward the target. |
| 1090 | Counter hits 0, vanilla calls `BeginBurst`. `now == fireReadyTick`, so `Fire()`. |

**Why the two-tick window** (`now <= fireReadyTick + 2`)? If the warmup is interrupted (the gunner walks off), vanilla resets the target but doesn't know about `fireReadyTick`, which stays set. Without an upper bound, the next `BeginBurst` at any later time would see `now >= fireReadyTick` and fire with no warmup at all. The window means only a call arriving right when the warmup was due counts as "warmup finished". A stale value just starts a fresh warmup.

**`WarmupTicks()`**: weapon `warmupTime` x `Ext.warmupFactor` x gunner's `AimingDelayFactor` (if there's a gunner), converted to ticks.

**`Fire()`**: re-checks the arc (the target may have moved out during the warmup), then does what vanilla's `BeginBurst` does, except that XP is given only if `TryStartCastOn` returns true (it can fail, for example if line of sight was lost).

**`GiveGunnerShootingXp()`**: vanilla's `Verb_Shoot` gives Shooting XP only when the caster is a pawn. On a mount the caster is the building, so the gunner would learn nothing. This method copies vanilla's numbers: 170 XP per second of the weapon's full cycle against hostile targets, 20 against others, nothing against downed pawns or colony mechs.

**`BurstCooldownTime()`**: vanilla reads the turret def's cooldown. The mount returns the weapon's `RangedWeapon_Cooldown` stat (which includes quality, as when a pawn holds it) times `Ext.cooldownFactor`. `BurstComplete` (vanilla) calls this when the burst ends.

**`OrderAttack(targ)`**: called when the player uses the Attack gizmo. The mount refuses targets outside the arc with a message. Otherwise it calls the base, and if `forcedTarget` actually changed, clears `fireReadyTick`, so a warmup that was almost finished on the old target doesn't fire instantly at the new one. It compares before and after because vanilla silently ignores out-of-range orders, and in that case nothing should be reset.

### 5.6 Targeting and the firing arc

The arc is a cone of `Arc` degrees centred on the direction the mount faces. `Arc` comes from settings: 120 for manned mounts, 360 (unlimited) for automated ones by default. `InArc(target)` asks `FiringArc.Contains` (section 7.1).

The arc is enforced in four places, because a target can enter the turret's attention four ways:

1. **Automatic target search: `TryFindNewTarget()`**. Vanilla's version calls `AttackTargetFinder.BestShootTargetFromCurrentPosition` with a private validator. The mount's override repeats that call with the same flags (needs a threat, line of sight, not already burning for incendiaries) and its own validator: `InArc(t) && IsValidMountTarget(t)`. `IsValidMountTarget` repeats vanilla's private rules: don't shoot your own prisoners, mortars don't target under thick roofs, auto-turrets don't shoot things machines like, manned turrets don't shoot their own animals. When the arc is unlimited or the mount is empty, it calls the base method instead. The override leaves out one vanilla branch: a hostile turret with an overhead (mortar-style) projectile sometimes targets a random player building. That only matters for an enemy-owned mount holding a mortar-like weapon.
2. **Player orders: `OrderAttack`** (5.5), which refuses with "Target is outside the mount's firing arc."
3. **Every tick, before the base `Tick`**: if the forced target has walked out of the arc, `ResetTargeting()`.
4. **At the moment of firing: `Fire()`**, for a target that left during the warmup.

The mount also clamps the turret top's rotation after each tick (`Top.CurRotation = FiringArc.Clamp(...)`), so the idle sweep of a manned turret, and its turn toward a target, never swings past the arc's edges. Visually the weapon can't point behind the gunner.

The search uses the **gunner** as the searcher when there is one (`IAttackTargetSearcher searcher = Gunner ?? this`), which is what vanilla does for manned turrets, so the gunner's own sight and faction decide what counts as an enemy.

`Notify_SettingsChanged()` (called by `MountSettings.Apply`) rescales ranges and drops a forced target that the new arc excludes.

`DrawExtraSelectionOverlays()` draws the arc outline when the mount is selected, at the weapon's range (12 cells if empty).

### 5.7 Who is "the shooter"?

The weapon's verb has `caster = mount`. So:

- For hit chance, vanilla's `ShotReport` sees a building and uses `ShootingAccuracyTurret`, not the gunner. That's correct for auto-mounts (the XML sets the stat per tier) but would ignore the gunner on manned mounts. Patch 6.1 fixes that.
- When launching the projectile, vanilla checks for a `CompMannable` on the caster. If someone is manning it, the projectile's `launcher` becomes the **gunner** (so kills and faction relations are credited to them) and its `equipment` becomes the **building** (so the kill log says "killed by weapon mount"). But the projectile reads damage quality and unique traits from `equipment`, so the mounted weapon's quality would be lost. Patch 6.2 fixes that.

On auto-mounts there is no gunner, vanilla passes the weapon itself as equipment, and neither patch does anything.

### 5.8 Destruction and refunds: `Destroy(mode)`

`DestroyMode` says why a thing is being destroyed: `Vanish` (removed by code, no leavings), `Deconstruct`, `KillFinalize` (killed in combat), and so on.

```csharp
if (wasSpawned && mode != DestroyMode.Vanish) weapon = RemoveWeapon();
base.Destroy(mode);
```

The weapon is taken out *before* the base destroys the building. `gun` is not on the map (it lives only in the building's field), so if nothing took it out it would simply disappear with the building. Map and position are saved first, since after `base.Destroy` the building no longer has them.

- **Weapon**: if the mount was killed (`KillFinalize` or `KillFinalizeLeavingsOnly`), the weapon loses a random 0 to `maxWeaponDamage` (default 40%) of its max hit points, never below 1 HP. Then it's placed near the wreck. On deconstruction it comes back undamaged.
- **Core** (auto-mounts): deconstructing always drops it. When killed, it's lost on a `coreLossChance` roll (default 50%), with a message either way. Remember the XML blacklists the core from vanilla's refund, so this is the only path a core comes back by.

None of this runs for a `Vanish` destroy, or for a mount that isn't spawned when it's destroyed (packed up as a minified item), since there's no map to drop anything onto. See section 10.

### 5.9 Inspect text

`GetInspectString()` adds lines below vanilla's: the mounted weapon, its boosted range and the bonus, "No weapon installed", the pending weapon, and "Unmanned" for a manned mount with nobody on it. All text goes through `Translate()` with keys in `Languages/English/Keyed/MountedWeapons.xml`.

### Try it
- Put a sniper rifle and an SMG on two mounts and watch the aim bar over each: the sniper's is longer. Then man one with a Trigger-happy colonist.
- With dev mode, check a gunner's Shooting XP before and after a fight on a manned mount.
- Destroy an auto-mount with dev tools (Destroy) and with Kill. Only Kill can lose the core.

---

## 6. The Harmony patches: `HarmonyPatches.cs`

### 6.0 How a Harmony patch is wired

```csharp
[HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitFactorFromShooter))]
public static class Patch_ShotReport_HitFactorFromShooter
{
    public static void Postfix(Thing caster, float distance, float? acc, ref float __result) { ... }
}
```

- The attribute names the target method. `PatchAll()` in the `Mod` constructor finds every class with this attribute and applies it.
- A static method named `Prefix` runs before the original; `Postfix` runs after.
- Parameters are matched **by name**, not position: `caster`, `distance` and `acc` must be spelled exactly like the original method's parameters. Names with two underscores are special: `__result` is the return value (with `ref`, you can change it), `__instance` is the object the method was called on.
- A `ref` parameter on a prefix lets you change the argument before the original sees it.
- When the target has overloads, the attribute must list the argument types to pick one (6.2 does this).

The compiler checks none of the binding. A typo in a target name or parameter name compiles, then throws at game start, and an exception inside `PatchAll` stops every patch after it from being applied. That's what `verify_patches` (run after every build) checks: both targets resolve. If a RimWorld update renames or changes either target method, this is the first place the mod will break.

Both patches are written to do nothing for anything that isn't one of our mounts, and to let the original method and other mods' patches run normally.

### 6.1 Gunner accuracy: postfix on `ShotReport.HitFactorFromShooter`

Vanilla (trimmed):

```csharp
public static float HitFactorFromShooter(Thing caster, float distance, float? acc = null)
{
    float f = acc ?? (caster is Pawn
        ? caster.GetStatValue(StatDefOf.ShootingAccuracyPawn)
        : caster.GetStatValue(StatDefOf.ShootingAccuracyTurret));
    float num = Mathf.Pow(f, distance);          // per-cell accuracy compounded over distance
    if (caster is Pawn) num *= <pawn's Touch/Short/Medium/Long accuracy factors>;
    return Mathf.Max(num, 0.0201f);
}
```

`ShootingAccuracyPawn` is the per-cell hit chance from the pawn's Shooting skill, sight, manipulation and traits. Over 20 cells, a per-cell value of 0.96 gives 0.96^20 = 44%. The caster here is the mount, so vanilla uses the turret stat and ignores the gunner.

Our postfix:

```csharp
if (acc.HasValue) return;                         // someone passed an explicit accuracy: leave it
if (!(caster is Building_WeaponMount mount)) return;
Pawn gunner = mount.Gunner;
if (gunner == null) return;                       // auto-mounts keep vanilla's turret stat

float perCell = gunner.GetStatValue(StatDefOf.ShootingAccuracyPawn);
perCell = 1f - (1f - perCell) * (1f - mount.Ext.stability);
__result = ShotReport.HitFactorFromShooter(gunner, distance, perCell);
```

`stability` removes that fraction of the gunner's per-cell *miss* chance. A gunner at 0.96 per cell on the basic mount (stability 0.2) misses 4% per cell; the mount removes a fifth of that, giving 0.968. Over 20 cells that's 52% instead of 44%. Because it works on the miss chance, a good shooter gains less in absolute terms than a poor one, and no setting can push accuracy past 100%.

The last line calls the original again with the **gunner** as caster and the adjusted accuracy passed in as `acc`. That way the pawn-only distance factors are applied using the gunner's stats, with vanilla's own code. The call re-enters this postfix, but `acc.HasValue` is true on that call, so the first line returns immediately; there is no infinite loop.

The weapon's own accuracy (its Short/Medium/Long values and quality) isn't part of this method. Vanilla applies it separately from the verb's equipment, which is the weapon, so it still counts.

Why a postfix and not a prefix that skips the original? A skipping prefix would also skip any other mod's patches on this method. A postfix lets everything run and then overwrites the result only for our manned mounts. The cost is one type check on every hit-chance calculation in the game, which is negligible.

### 6.2 Weapon quality and traits: prefix on `Projectile.Launch`

`Projectile.Launch` has two overloads (6 and 8 parameters), so the attribute lists all eight types to pick the 8-parameter one, which is the one vanilla's `Verb_LaunchProjectile` calls.

As 5.7 explained, for a manned turret vanilla passes the **building** as `equipment`. `Launch` reads the damage quality from it (`equipment.TryGetQuality(out equipmentQuality)` in vanilla), and a building has no quality, so a legendary weapon would fire normal-quality damage. Earlier in the same shot, vanilla applies unique weapon traits (Anomaly/Odyssey unique weapons) from that same `equipment`, and the building has none.

```csharp
public static void Prefix(Projectile __instance, ref Thing equipment)
{
    if (!(equipment is Building_WeaponMount mount)) return;
    Thing weapon = mount.MountedWeapon;
    if (weapon == null) return;
    equipment = weapon;                    // Launch now reads quality from the real weapon
    // then re-applies each unique trait's damage override and extra damages
}
```

The trait loop is a copy of vanilla's loop in `Verb_LaunchProjectile.TryCastShot`. It only runs on the swap path, where vanilla applied nothing (the building had no traits), so traits are never applied twice. Auto-mounts never reach it, because vanilla already passes the weapon for them.

One side effect: since the projectile's equipment is now the weapon, the combat log should credit the weapon rather than the mount.

### Try it
Spawn a row of dummy targets (or a slow raid) at 20 cells and let a Shooting 3 colonist and then a Shooting 15 colonist man the same mount. The difference in hits comes from patch 6.1; before it existed, both would have hit exactly as often. To see the patch's code run, put a breakpoint-style `Log.Message` in the postfix and watch the dev log.

---

## 7. Drawing: arcs and weapon centring

### 7.1 `FiringArc.Contains`

```csharp
public static bool Contains(Vector3 origin, Rot4 facing, float arc, Vector3 point)
{
    if (Unlimited(arc)) return true;          // arc >= 359.5
    Vector3 d = point - origin; d.y = 0f;
    if (d.sqrMagnitude < 0.01f) return true;  // target on top of us: don't refuse it
    return Mathf.Abs(Mathf.DeltaAngle(facing.AsAngle, d.AngleFlat())) <= arc / 2f;
}
```

RimWorld's angles: 0 is north, increasing clockwise (east is 90). `Rot4.AsAngle`, `Vector3.AngleFlat()` and the turret top's `CurRotation` all use that convention, so they compare directly. `Mathf.DeltaAngle` gives the signed shortest difference (-180 to 180), which handles the wrap at 0/360. A 120° arc accepts anything within 60° either side of the facing.

`Clamp` does the same sum and limits the difference to the arc's half-width, which keeps the turret top inside the arc.

### 7.2 The arc outline

`DrawField(center, facing, arc, radius, map)` collects every cell within `radius` that `Contains` accepts and hands the list to vanilla's `GenDraw.DrawFieldEdges`, which outlines a set of cells (the same call that draws a turret's range ring). Selection overlays draw every frame, so the cell list is cached in static fields and only rebuilt when the centre, facing, arc or radius changes.

`PlaceWorker_FiringArc.DrawGhost` is called by vanilla while you're placing or rotating a blueprint, and draws the same outline at a 12-cell radius (or the weapon's range when reinstalling a minified mount that has one).

### 7.3 Drawing the weapon: `DrawAt`

```csharp
base.DrawAt(drawLoc, flip);        // vanilla draws the turret top (clear for us)
if (!HasWeapon) return;
Vector3 offset = new Vector3(Ext.weaponDrawOffset.x, 0f, Ext.weaponDrawOffset.y).RotatedBy(Rotation.AsAngle);
Vector3 pivot = drawLoc + offset + Altitudes.AltIncVect * 2f;
float aim = Top.CurRotation;
if (MountSettings.Get.centerWeapons) pivot -= ArtCentroidWorld(aim);
PawnRenderUtility.DrawEquipmentAiming(gun, pivot, aim);
```

- `weaponDrawOffset` (from XML, zero for every current mount) can shift the pivot on the base art, rotated with the building.
- In RimWorld's 3D-on-a-plane rendering, `y` is draw height (altitude), not a map axis. Adding `AltIncVect * 2` draws the weapon just above the base.
- `PawnRenderUtility.DrawEquipmentAiming` is the vanilla method that draws a pawn's held weapon. Using it means the weapon looks exactly as it does in a colonist's hands: same mesh, same `equippedAngleOffset`, same mirroring when aiming left.

### 7.4 Weapon centring: `WeaponArtCentroid` and `ArtCentroidWorld`

This is the least ordinary code in the mod, and it's optional (the "Center weapon art on the mount" setting).

**The problem.** `DrawEquipmentAiming` rotates the weapon around the *centre of its texture*. Many weapon textures don't have the gun in the middle: the minigun's art sits well to one side. On a pawn you don't notice, because the gun is drawn in front of the pawn. On a mount, it rotates around a visible pivot, and an off-centre gun swings around a point outside its own body.

**The fix.** Find where the art actually is inside the texture and move the draw point so that spot lands on the pivot.

`WeaponArtCentroid.Measure(texture)`:
1. Game textures live on the GPU and can't be read from C#. So it copies (`Graphics.Blit`) the texture into a 64x64 `RenderTexture`, a GPU image we're allowed to read back.
2. `ReadPixels` copies that into a temporary `Texture2D` on the CPU.
3. It computes the alpha-weighted average position of all visible pixels (alpha 16 or more): the art's centre of mass.
4. Returns how far that is from the texture's centre, in fractions of the texture's size (-0.5 to 0.5).
5. Restores the previous render target and frees the temporary textures in `finally`, even on error. On error it logs one warning and returns zero (draw uncentred).

The result is cached per texture, so each weapon texture is measured once, the first time it's drawn on a mount. It must run on Unity's main thread, which `DrawAt` always is.

`ArtCentroidWorld(aim)` turns that texture-space offset into a map-space offset for the current aim. It copies the rotation and mirroring logic from vanilla's `DrawEquipmentAiming` (between 200° and 340° the mesh is flipped and the angle shifted by 180°), scales by the weapon's draw size, and rotates. Subtracting the result from the pivot puts the art's centre on the pivot at every angle.

If you'd rather not maintain this, it can be replaced by setting `weaponDrawOffset` per mount and turning centring off. Off-centre weapons like the minigun would then swing around their texture centre again.

### Try it
Mount a minigun and toggle "Center weapon art on the mount" in settings while watching it sweep. With centring off you'll see the barrel swing around a point beside the gun.

---

## 8. The settings window (`Mod.cs`, second half)

RimWorld calls `DoSettingsWindowContents(Rect)` every frame while the window is open. This is Unity's immediate-mode GUI: there are no persistent widget objects; each frame you draw every control and read its new value in the same call.

- **Tabs**: `TabDrawer.DrawTabs(rect, tabs)` (vanilla) draws the strip *above* the rect it's given, so the body rect starts one tab height down. The Automated tab is only added when automated defs exist (Biotech).
- **Scrolling**: `Widgets.BeginScrollView` with a view rect as tall as last frame's content (`listingHeight`). `Listing_Standard` lays controls out top to bottom. `maxOneColumn = true` stops it wrapping into a second column for the one frame after switching to a taller tab, a bug found while building this.
- **`Slider`**: draws label and value, then `Listing_Standard.Slider`. It only rounds (`GenMath.RoundTo`) when the value actually changed, so an off-grid XML value like 0.987 isn't rewritten just by opening the tab.
- **`DoMounts`**: for each mount def, edits the saved `MountTuning` if one exists, otherwise a `Copy()` of the default. A copy that ends up different from the default is saved; one that doesn't is thrown away. Viewing a tab therefore never writes anything.
- **Reset this tab**: General resets the five global values; the mount tabs remove those mounts' saved entries, which returns them to the XML values.
- When the window closes, `WriteSettings()` saves and calls `Apply()` (2.2).

The HTML mockup in `.circinus/mockups/settings-window.html` is the design this was built from, and the source of the Workshop picture.

---

## 9. Bugs that were found and fixed

Knowing what went wrong explains several lines that otherwise look odd. Most of these were caught by code review before they showed up in play; the hellcat rifle and the beep were found by you in testing.

| Problem | Cause | Fix | Where |
|---|---|---|---|
| First C# build failed | Missing `using Verse.AI;` and Unity's IMGUI and TextRendering module references | Added both | `Building_WeaponMount.cs`, `.csproj` |
| Gunner gained XP even when the shot never started | XP was given unconditionally | XP only when `TryStartCastOn` returns true | `Fire()` |
| Legendary weapons hit like normal ones on manned mounts | Vanilla passes the building as projectile equipment | `Projectile.Launch` prefix | 6.2 |
| Install job couldn't start while someone manned the mount | The job reserved the mount, which the gunner already held | Reserve only the weapon | 4.5 |
| An attack order could fire instantly | A finished warmup carried over to a new target | Clear `fireReadyTick` when the order changes the target | `OrderAttack` |
| Opening a settings tab froze XML values into the settings file | The old code inserted an entry for every mount viewed | Edit a copy, save only if different; prune entries equal to defaults at startup | 2.2, 8 |
| Sliders rewrote values just by being drawn | Rounding on every frame | Round only when moved | `Slider` |
| Settings list wrapped into two columns | `Listing_Standard` overflow for one frame | `maxOneColumn` | 8 |
| Hellcat rifle couldn't be mounted | `HasComp(typeof(CompEquippable))` matches the exact class only, and the hellcat uses a subclass | `HasComp<CompEquippable>()` matches subclasses | `MountUtility.cs` |
| Manned mounts beeped on acquiring a target | Vanilla turret default | `<playTargetAcquiredSound>false` | `MW_MountBases.xml` |

---

## 10. Known limits

These are true of the current code. None of them throw errors.

- **Burst shots and the arc**: the arc is checked when a burst starts, not between its shots. A target crossing the edge mid-burst can take the rest of that burst.
- **Packed-up mounts**: a minified mount that is destroyed while packed up, or sold to a trader, takes its weapon and core with it.
- **Weapon abilities**: the hellcat rifle's burner and similar equippable abilities can't be used while mounted, and reloadable charges don't refill on the mount. The weapon keeps whatever charges it had.
- **Exotic weapons**: weapons whose primary verb isn't a projectile or beam verb are refused rather than risked.
- **Combat Extended**: not supported. CE replaces the shooting and projectile code these patches and overrides rely on.
- **Removing the mod mid-save**: every mount disappears, with any weapon or core still on it.
- **Separate settings per install**: the local copy and the Workshop copy keep separate settings files (2.1).
- **`HasComp<T>` is wider now**: any `CompEquippable` subclass from another mod counts as equippable, and any `CompMannable` subclass makes a modded mount count as manned. That's intended, but it's where to look if a strange modded weapon misbehaves on a mount.
- **Hostile mortar targeting**: the arc-aware target search leaves out vanilla's random-building branch (5.6).
- **Art**: every texture is placeholder art.

---

## 11. Where to change what

| You want to change | Edit |
|---|---|
| A mount's range, stability, aim time | `<modExtensions>` in the mount's XML (players' own settings still override) |
| Auto-mount accuracy | `<ShootingAccuracyTurret>` in the auto-mount's `statBases` |
| Costs, hit points, research, cover | The mount's XML (`costList`, `statBases`, `researchPrerequisites`, `fillPercent`) |
| Default arc, core-loss chance, weapon damage | Constants at the top of `MountSettings` |
| Which weapons can be mounted | `MountUtility.ComputeMountable` |
| Install time | `JobDriver_InstallMountedWeapon.InstallTicks` (180 = 3 s) |
| How stability affects accuracy | `Patch_ShotReport_HitFactorFromShooter` |
| XP rate | `GiveGunnerShootingXp` |
| What drops on destruction | `Building_WeaponMount.Destroy` |
| Any text the player reads from C# | `Languages/English/Keyed/MountedWeapons.xml` |
| A new mount tier | Copy a def in the XML, give it a new defName, texture and extension values. No C# needed; it appears in the settings automatically. |

The project file (`Source/MountedWeapons.csproj`) targets .NET Framework 4.7.2, which is what RimWorld's Mono runtime loads. It references the game's `Assembly-CSharp.dll` and Unity modules from your install without copying them (`Private=false`), and references Harmony with `ExcludeAssets=runtime`, so `0Harmony.dll` is never shipped: the Harmony mod, a declared dependency in `About.xml`, provides it. The build writes `Assemblies/MountedWeapons.dll`, which is what the Workshop upload ships; `Source/` stays on your machine.


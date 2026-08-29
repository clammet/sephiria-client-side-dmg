# Client Side Damage — Sephiria mod

*Built by AI.*

A BepInEx 5 plugin for **Sephiria** that makes the *joining* player's own game client the
authority over its combat instead of the host:

| What | Vanilla | With this mod |
|---|---|---|
| **Block / dodge / perfect guard** on hits taken by a joined player | decided by the host from its (lagged) copy of the player's state | decided from the **client's own** guard & dash i-frame state, as of the moment the client *sees* the hit |
| **Bullet / projectile hits** that involve a joined player (their own bullets hitting enemies, or enemy bullets hitting them) | host collision test against lagged positions | **registered by the client** — what you see hit is what hits |
| **Melee swings** performed by a joined player, and enemy swings hitting a joined player | host collision test | **registered by the client** |
| **Area / ground / boss effects** hitting a joined player (stomps, cracks, sweeping lasers, ground fire, traps, explosions, chain lightning, ...) | host overlap test against the lagged position | host still detects, but the **client verifies** the exact shape against its own position at the moment it sees the effect; a miss on the client's screen is dropped |
| Damage numbers, crits, evasion rolls, HP | host | host runs the unchanged vanilla formula, but with the client's guard/dodge verdict and the client's hit registration forced in |

The host player (the one who created the lobby) is the server, so nothing changes for them —
their state is already exact.

Both the **host and the joining player must install the mod**. If either side does not have it,
the mod stays dormant for that pair and the game behaves exactly like vanilla (nobody gets kicked;
the mod only uses Mirror RPCs, which are silently ignored by an un-modded peer).

---

## Installation

You need **BepInEx 5 (x64)** and the plugin DLL. Do this on every PC that will play together.

### 1. Install BepInEx 5

1. Download `BepInEx_win_x64_5.4.23.3.zip` (or newer 5.4.x) from
   <https://github.com/BepInEx/BepInEx/releases>.
2. Extract it **into the game folder** — the folder that contains `Sephiria.exe`
   (Steam: right click the game → *Manage* → *Browse local files*).
   Afterwards you should have `Sephiria\BepInEx\`, `Sephiria\winhttp.dll` and
   `Sephiria\doorstop_config.ini` next to `Sephiria.exe`.
3. Start the game once and close it. BepInEx creates `BepInEx\plugins\`, `BepInEx\config\`
   and `BepInEx\LogOutput.log`.

### 2. Install the plugin

1. Download `ClientSideDamage.dll` from the latest entry on the
   [**Releases** page](https://github.com/clammet/sephiria-client-side-dmg/releases)
   (or build it yourself, see *Building* below - the output is `dist\ClientSideDamage.dll`).
2. Place it at `Sephiria\BepInEx\plugins\ClientSideDamage\ClientSideDamage.dll`
   (create the `ClientSideDamage` folder; any sub-folder of `plugins` works).
3. Start the game. `BepInEx\LogOutput.log` should contain

   ```
   [Info   :Client Side Damage] Client Side Damage 1.4.8 loaded (protocol 10)
   ```

3. Play co-op. When a modded client joins a modded host, the logs show

   ```
   host:   [CSD/host] client 1 enabled with features: DamageTakenAuthority, BulletHits, MeleeHits, AreaHits
   client: [CSD/client] enabled by host with features: DamageTakenAuthority, BulletHits, MeleeHits, AreaHits
   ```

   The joining player's own game also writes local `CSD : ...` lines into their chat log
   (`v1.4.8 loaded on your side, waiting for the host...`, then `host enabled: ...`, or
   `no answer from the host - it does not seem to run the mod`), so each side can tell from its
   own screen whether the mod is loaded there.

   The host also posts one-line status messages in the in-game chat log (sender `CSD`, sent
   through the game's own chat RPC, so un-modded players see them too): its own line when it
   creates a multiplayer lobby (`CSD : v1.4.8 host ON: guard/dodge, bullets, melee, area, fresh-pos`)
   and, for every player joining the lobby, a line to everybody in the session as soon as their
   status is known (a modded client within a round trip, an un-modded one after ~2 s of silence) -
   `<player>: ON: guard/dodge, bullets, melee, area` with the negotiated features, or
   `<player>: OFF - <reason>` (mod not detected on their side, version mismatch, disabled in host /
   their config, init failure).

### 3. Uninstall

Delete `BepInEx\plugins\ClientSideDamage\`. To remove BepInEx entirely, also delete the
`BepInEx` folder, `winhttp.dll` and `doorstop_config.ini`.

---

## Configuration

After the first run a config file appears at `BepInEx\config\com.sephiria.clientsidedamage.cfg`.
Edit it with any text editor (or a BepInEx configuration-manager plugin).

| Section / key | Default | Meaning |
|---|---|---|
| `General.Enabled` | `true` | Master switch. |
| `General.DebugLog` | `false` | Log every damage query and hit report (for troubleshooting). |
| `Host.DamageTakenAuthority` | `true` | (host) resolve damage against a joined player with *their* guard/dodge state. |
| `Host.BulletHitAuthority` | `true` | (host) let the joined player's client register bullet hits involving them. |
| `Host.MeleeHitAuthority` | `true` | (host) let the joined player's client register their own melee hits. |
| `Host.ReplyTimeoutSeconds` | `1.0` | (host) if the client does not answer a damage query in time, resolve with host state (vanilla). |
| `Host.UseLatestClientPosition` | `true` | (host) apply a joined player's newest position update immediately instead of through Mirror's interpolation buffer, so every host-side position test (monster melee, ground/area effects, aiming) sees them 1-3 network ticks fresher. Host sees that player slightly less smoothly. |
| `Host.AreaHitAuthority` | `true` | (host) area / ground / boss hits detected against a joined player are verified by that player's client against its own position; a "no" from the client drops the damage. |
| `Host.AreaHitMargin` | `0` | (host, experimental) widen every area test by this many world units for joined players, so the client can also confirm hits the host *narrowly missed*. `0` = off. See the notes below before turning it on. |
| `Client.DamageTakenAuthority` | `true` | (client) answer the host's damage queries with our local state. |
| `Client.BulletHitDetection` | `true` | (client) run bullet hit tests locally and report them. |
| `Client.MeleeHitDetection` | `true` | (client) run melee hit tests for our own swings locally and report them. |
| `Client.PredictGuardFromInput` | `true` | (client) count the guard as up from the moment the guard button is pressed (once the mod has seen the current weapon guard on that button), instead of waiting for the host to confirm. |
| `Client.ClientOnlyDodge` | `false` | (client) `false`: a dodge counts if *either* our predicted dash i-frames *or* the host's own dodge state say so (never worse than vanilla). `true`: only our prediction counts. |
| `Client.AreaHitVerification` | `true` | (client) verify area / ground / boss hits the host detected against us with our own position at the moment we see the effect. |
| `Client.AreaHitAnchoring` | `true` | (client) before testing, move the shape to where *we* currently see the object it belongs to (a sweeping laser, a charging boss, a moving wave). Off = test the host's world position of the shape. |

A feature is active for a host/client pair only if **both** sides enable it; the handshake
negotiates the intersection.
Config changes made while playing are re-negotiated on the fly (no reconnect needed): the client
re-sends its request, the host re-sends the agreed set.

---

## How it works (short version)

Sephiria's combat is fully host-authoritative on top of Mirror: the host owns hit detection,
`UnitAvatar.ApplyDamage`, HP, guard and i-frame timers. A joined player's inputs travel to the
host as Commands, so from that player's point of view every block, dodge and swing is judged
about one round-trip late. The mod keeps the vanilla code but moves the *decisions* to the client:

* **Damage the host detects against a joined player** (monster melee, AoE, traps, …):
  the host's `ApplyDamage` is intercepted, the damage is parked, and the client is asked
  (`CSD::DamageQuery`) for its guard/dodge state *at the moment it perceives the hit*. The reply
  is a small snapshot (guard up?, seconds since it came up, guard direction, dash i-frames?).
  The host then runs the **unmodified vanilla `ApplyDamage`** with those private fields
  temporarily forced to the client's values (and `GetGuardDirection` returning the client's aim),
  so all vanilla side effects — perfect-guard bonus, guard break, MP cost, evasion/crit rolls,
  charms, thorns, death, FX RPCs — happen exactly as usual. If no answer arrives within
  `ReplyTimeoutSeconds` (or the client disconnects) the hit is resolved with the host's state.
  For `Bullet.AttackOnServer` and `MeleeCollision.Attack` the *whole call* is parked and replayed
  when the reply arrives (the same port flow B uses), so the projectile's own bookkeeping - pierce
  count, on-attack callbacks, hit FX, consumption / continuation on a dodge - happens with the
  real result. Every other caller (boss code, traps, explosions, ground fire...) is parked inside
  `ApplyDamage` and gets `Success` back right away: its own result handling (`CallOnAttack`,
  success sounds, hit counters) runs optimistically and is not replayed, so it also fires for
  hits that later resolve as a block or dodge.

* **Bullets** (`Bullet`): on the client, the same overlap test the host normally runs
  (`attackingCollider.Overlap` / `TopdownSpatialHash`) is executed every frame for bullets that
  are *ours* (against anything) or that could hit *us* (against our own hitbox). Each hit is
  reported (`CSD::BulletHit`) with the hit direction and, when we are the victim, our state
  snapshot. The host re-creates the vanilla `AttackOnServer` behaviour for that (bullet, victim)
  pair — including pierce counts, on-attack callbacks, chain lightning and hit FX broadcasts —
  and applies the damage; the host's own overlap test is disabled for bullets whose owner or
  victim is a modded client. The host answers every report (`CSD::BulletHitResult`) with what its
  bookkeeping did - not applied / added to the attacked list / counted towards pierce - so the
  client's own dedupe and pierce state follow vanilla (`attackedList` only on non-absolute
  results, pierce only on success / block) instead of blacklisting on the first report. Because some bullets arm/disarm their collision in host-only code
  (laser warm-up, shuriken wind-up, arrow-rain delay), the host pushes `isCollisionEnabled` to
  modded clients on spawn and on every toggle. Hitscan arrows (`BulletMoveModule_RaycastArrow`)
  are re-cast by the client from the angle/length the host sends for the sprite (`RpcSetSprite`)
  and reported the same way. Bullets whose hit volume exists only on the host (the growing
  `WaveWithParticle` / `RingWave` colliders) stay host-side on both ends (`CsdUtil.IsBulletExcluded`).
  Two things compensate for the round trip a report needs while the host's copy of the bullet
  keeps flying: **rewind** - the report carries the bullet position the client saw at contact,
  and the host moves its bullet there before applying the hit, so hit FX and above all a
  destroy-on-hit explosion happen where the client saw the contact (centred on the victim, from
  the front, blockable like vanilla's) instead of speed × RTT further along; a bullet that survives
  the hit (pierce, dodge, a destroy module that keeps it alive) is put back in the same frame. The
  reported position must be finite and within the park reach of the host's copy, otherwise the hit
  is applied where the host has the bullet. And **parking** - a bullet that wants to destroy
  itself normally while somebody who might have reported it is within reach (its speed × the round
  trip, measured from its hit volume: an enemy hitbox for a client-owned bullet, the client's own
  player for a hostile one) is frozen where it is instead (its `Update` is skipped and it registers
  no more host hits; clients are told its collision is off and keep testing it for 0.75 s so their
  lagging copy can still reach the target). A report arriving within the grace (2×RTT + 0.9 s,
  0.45–2 s) is applied with the rewind above; without one the bullet is destroyed vanilla-style
  where it was parked, just late. Only *misses* are parked: a destroy issued because the bullet just
  hit somebody (pierce exhausted) is not, and a bullet whose destroy module carries a payload
  (explosion, spread, ground fire...) is only parked when it hit a wall - its timer / arrival /
  landing destroy is the detonation and happens on time. A bullet parked for a host-detected hit
  that is waiting for the client's reply is kept until that reply is in (or timed out) and released
  right after. Bullets driven by a `TopdownRigidbody` (lobbed / physical projectiles), lasers,
  owner-attached bullets and bullets that do not die on contact (bounce, boomerang, hover) are
  never parked; `forceDestroy` (a swing cutting a bullet, owner death, floor change) never is.
  Visible cost: a near-miss bullet rests on the wall for up to the grace before it explodes; a hit
  one visibly jumps back from the wall to the victim to explode there.

  The growth-parry charm's `DaggerGrowthBullet` is also covered even though it is a plain
  `MonoBehaviour`, not a network-spawned `Bullet`. Immediately before vanilla's reliable spawn RPC,
  the host assigns the dagger a temporary CSD id (`CSD::DaggerSpawn`). Each client correlates that
  id with its independently-created visual and runs vanilla's `HorayPhysics2D.OverlapCircle` test
  after the same movement update. Hits involving that client are reported with `CSD::DaggerHit`;
  the host validates the id, owner, faction and reported point against the dagger's fixed path, then
  constructs vanilla's projectile/DirectAttack/Chaos `DamageInstance` from its authoritative spawn
  data. The host's own observation is suppressed only when the owner or victim has negotiated
  `BulletHits`. If both are modded, the victim's report wins so its guard/dodge snapshot is used.
  Straight-moving `UniformVector2` and `Shuriken` bullets have their collider swept between
  consecutive client positions so they cannot pass completely through a target between networked
  transform samples. This covers the dagger weapon's shuriken upgrades and fast enemy projectiles
  such as mole rocks, cat throwing knives / shuriken, and panther daggers.
  Hostile bullets are classified with the `AttackableFactionLayers` stored on the projectile at
  spawn, so monster direct attacks are not discarded by recomputing a different faction mask.

* **Melee** (`MeleeCollision`): swings are network-spawned objects with the same shape data on
  both sides and a `NetworkTransformReliable`. Right after the spawn the host sends modded clients
  the swing parameters (`CSD::MeleeSpawn`: owner, motion data, range bonus, and vanilla's own
  owner-relative attach offset). The client then runs the vanilla shape test itself every frame:
  for its **own swings** it attaches the swing to its local position with that host-computed
  offset (mixing the host's swing origin with the fresher local position would trail the
  character by one latency step) and reports every victim it touches; for **hostile swings** it uses the streamed
  transform (where the swing is drawn) and reports when the swing touches *its own hitbox*, with its
  guard/dodge snapshot attached. The host calls the vanilla `MeleeCollision.Attack` for the reported
  pair (with the snapshot forced in when the client is the victim) and disables its own test for
  swings whose owner or victim is a modded client. Only swing types that use the base update loop
  are handled this way; `MeleeCollision_Circle_Distance` stays host-side (host detects, then asks
  the client for its guard/dodge state through the damage query).
  A swing lives only a few frames (a katana swing ~0.15 s) while the client's report needs a full
  round trip, so the host keeps a modded client's **own** swings alive for an RTT-derived grace
  period (2×RTT + 0.15 s, 0.3–2 s) after vanilla would have destroyed them - invisible, and their
  host-side hit test is off anyway - so the reports still find the swing (`MeleeCollision.DestroySelf`
  prefix). The client stops testing at the swing's nominal `durationTimer`, so the hit window itself
  stays vanilla's; only the moment the damage lands is one round trip late.

* **Client state prediction**: dash i-frames are predicted locally when `CharacterDash.StartDash`
  runs (`dodgeInvincibleTime + DashInvincibleTimeBonus`); the guard is taken from the synced
  `isGuardEnabled` plus, optionally, the guard button being held (learned per weapon the first
  time a guard actually comes up while the button is held). The host still applies its usual
  perfect-guard latency bonus on top of the client-perceived timing.

* **Transport**: everything is sent as ordinary Mirror Commands / TargetRpcs registered at
  runtime on `UnitAvatar` (`CSD::*`). An un-modded peer only logs "no receiver for RPC" and
  keeps playing vanilla — unlike custom `NetworkMessage`s, which would make Mirror disconnect
  the peer. The host initiates the handshake, so a modded client never sends anything to an
  un-modded host.

* **Area / ground / boss effects** (`AreaHits`, see `src\AreaHits.cs`): practically every
  non-projectile hit in the game is a host-side overlap query (`Physics2D.OverlapCircle/Box/
  Capsule`, `Collider2D.Overlap`, `Physics2D.Raycast`, or the game's own `TopdownSpatialHash`
  for "ground" effects) followed by `ApplyDamage` on each result - stomps, cracks, lasers,
  ground fire, traps, explosions, chain lightning, wave bullets... The client cannot run those
  tests itself (the timing and often the shape only exist in host code), so the mod hooks the
  overlap primitives on the host instead. Whenever a query's results contain a joined player's
  hitbox, the exact shape of that query is recorded for the current frame: its world geometry
  (circle / box / capsule / polygon / point / ray), the contact filter, and the transform it was
  attached to (the laser, the boss, the crack...). When the effect then calls `ApplyDamage` on
  that player, the shape is attached to the damage query the mod already sends. The client
  first re-anchors the shape to where *it* currently sees the source object (so a sweeping
  laser or a charging boss is tested where it is drawn on the client, not where the host had it
  a few ticks earlier), then runs the same kind of query against its own hitbox / position and
  answers hit or no-hit together with its guard/dodge snapshot. A "no" drops the damage; a
  "yes" resolves it exactly like any other flow A hit. Anything the client cannot evaluate
  (no shape recorded, unknown source object, our hitbox disabled...) keeps the host's verdict,
  so the generic verifier can only remove hits the client did not see. Pentaxis's explicitly
  validated SpinDash path below is the one client-originated boss-contact exception.
  Shapes are only anchored when they plausibly move with the object that ran the test: a
  `Collider2D.Overlap` source collider always does; a world-space query only when it is centred
  near that object (a stomp around a boss, a laser slab in front of it) and the object has a
  `NetworkTransform` at all. A telegraphed circle at the player's position or a crack the boss
  left behind stays in host world coordinates, so the caller's client-side lag (or a facing flip
  the client has not shown yet) cannot shift or mirror it. Pentaxis's SpinDash is the exception to
  the host-query-only model: its moving damage box is swept over the boss path shown on the joined
  client and reported to the host, because verifying the host's earlier overlap cannot create the
  later contact the client sees. The host validates the dash phase, faction, recent path, and the
  vanilla one-hit-per-second interval before applying it. The client also refuses an anchor that
  resolves to a differently named child or that it sees more than a few units away from where the
  host had it, and falls back to the host position.
  The methods that run these tests are found at runtime: the first time a test involving a
  joined player runs from a method the mod does not know yet, the caller is looked up on the
  stack and patched with a tiny context marker (`[CSD/host] area hits: tracking X.Y` in the
  log). From then on shapes from that method are anchored to the object that ran the test and
  matched to the `ApplyDamage` it makes; until a caller is tracked its shapes are not used at all
  (the hit is still asked about, just without position verification), so a perception query of
  one object can never be attached to the damage of another. Each recorded shape is handed to at
  most one `ApplyDamage`, and among a caller's shapes of the same frame the most recent one from a
  method already seen to deal damage wins over e.g. a sight-radius query it ran in between.
  `Host.AreaHitMargin` (widening) is likewise limited to those damage-dealing methods, honours the
  query's contact filter, and a widened hit the host cannot ask the client about is dropped
  rather than handed to vanilla. Effects that filter their overlap results further
  (e.g. a circle query followed by a diamond-cell test) are verified against the outer shape,
  which can only turn hits into misses when the client left the outer shape.

### Boundary conditions
* Any deferred or client-registered hit still ends in vanilla `ApplyDamage`, whose first checks
  are dead / invulnerable / life-invincible / pit / peace-mode / faction - so a late hit against a
  target that has since died or been revived (revive grants invulnerability) is rejected.
* Late hit reports for projectiles the host already destroyed are dropped (netIds are never
  reused in a session; the host logs `melee report ... dropped: swing netId N not spawned` with
  `DebugLog` on). Client-owned swings linger and near-miss bullets are parked for the grace
  period described above, so this only affects them under extreme lag, for rigidbody-driven
  bullets, or when the reporter was outside the park reach (host: `bullet report ... dropped:
  bullet not spawned`). Late damage replies after the timeout are dropped (no double apply).
* Floor moves (`DungeonManager.LocalMoveFloor`) and scene changes drop parked damage queries for
  the moving player, so nothing from the previous floor lands after the teleport.
* All mod traffic uses Mirror's reliable channel: packet loss only delays, it never loses a hit
  or a reply. Long stalls fall under the query timeout (host-state fallback); a full disconnect
  flushes everything with host state.

* Except for Pentaxis SpinDash, area hit verification only removes hits: the host still has to
  detect the hit first. With
  `Host.AreaHitMargin` > 0 the host's area tests are widened by that many units for joined
  players, so a hit the host missed by less than the margin is still handed to the client to
  decide with the exact shape. This is off by default because a few effects keep per-victim
  state around their test (shared target lists of trap groups / multi-explosions, once-per-victim
  zones): a widened test the client then rejects can make such an effect skip that player once.
  Projectiles and swings are never widened (they consume themselves on a hit).
* Anchoring uses the client's interpolated copy of the source object; for a one-shot attack made
  by a *moving* object the client sees the object slightly behind the host, so the shape is tested
  where the client sees it. That is the intended "what you see is what hits you" behaviour, but it
  is a guess in the rare case of a moving object firing a world-fixed shape (turn
  `Client.AreaHitAnchoring` off to test host world positions instead).

* **Freshest client position on the host**: for a joined player's own transform the host skips
  Mirror's server-side snapshot interpolation and jumps to the newest received snapshot
  (`NetworkTransformReliable.UpdateServer` prefix). Every host-side position test - area/ground
  effects before their client verification, monster melee, aiming - is then evaluated against a
  position that is only the network one-way trip old instead of one-way trip + interpolation buffer.

### What stays on the host on purpose

* Damage math, crit and evasion RNG, HP/MP/shield mutation, buffs, death — vanilla code on the host, deterministic given the (synced) stats.
* Counter / parry windows and post-hit invincibility (host-driven timers that the game already latency-compensates).
* Damage that is not the result of a position test at all (unavoidable boss "wipe" ticks, debuff ticks) - there is nothing to verify.
* Elemental tick damage, fall damage, system damage (never interact with guard/dodge).
* Any projectile type listed above as excluded.

---

## Building from source

Sources are in `src\`. Two options:

* With the .NET SDK (`winget install Microsoft.DotNet.SDK.8`):
  `dotnet build ClientSideDamage.csproj -c Release` (or just run `.\build.ps1`).
* Without an SDK: `.\build.ps1` also works with the portable Roslyn compiler in `..\tools\roslyn`
  (only the .NET 8 runtime is needed).

The build references the game's own `Assembly-CSharp.dll` / `Mirror.dll` from
`Sephiria\Sephiria_Data\Managed` and `BepInEx.dll` / `0Harmony.dll` from `Sephiria\BepInEx\core`.
No publicized assemblies are needed: all non-public members are reached through Harmony
accessors (`src\Access.cs`), because this Unity build's Mono runtime enforces member visibility.

Output: `dist\ClientSideDamage.dll`.

## Compatibility notes

* Built against Sephiria build 8/21/2026 (Unity 6000.3.21, Mirror). Every game member the mod
  touches through accessors is resolved at start-up and every Harmony patch is applied inside
  the same guarded step; if a game update renames something the plugin logs
  `Failed to initialise (game version mismatch?)`, rolls back whatever patches were already
  applied and stays dormant instead of half-working.
* Mirror keeps only 16 bits of an RPC name hash and silently overwrites the registry on a
  clash. The mod checks its twelve hashes are free when it registers them and watches every later
  registration (the game's RPCs register lazily, per type); should a game RPC ever land on one of
  them the mod logs `RPC hash collision` and switches itself off for the session, the game's
  handler wins.
* Host and client must run the same mod **protocol** version (`PROTOCOL_VERSION`, logged at
  start-up); mismatches disable the mod for that pair with a warning (the host's chat line then
  says `OFF - mod version mismatch (host protocol N, theirs M)`). The protocol number is bumped with every release that changes
  behaviour on both sides, so everybody in a session is on the same build.
* Tested to load and patch cleanly in the game; the multiplayer behaviour itself needs a
  two-PC (or two-instance) session to verify — turn on `General.DebugLog` on both sides and
  compare `BepInEx\LogOutput.log` if something looks off. Every mod log line carries a local wall
  clock stamp with its UTC offset (`[17:39:50.223+09:00 t=173.96]`, `t` = seconds since the game started), so a host log
  and a client log from different time zones can be lined up by subtracting the offsets; the first mod line states
  the date and time zone.

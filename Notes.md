Testing notes:
- Since 1.4.7 the Purification core laser (`LibraryChapter4Dorm_CoreLaser`) is verified on a joined
  client against the client's own copy of the laser instead of the host's box. The lasers are local
  objects on every peer driven by a synced group angle, so the client's copy sweeps one network delay
  behind the host's and the host's box could not be anchored; at RTT ~220 ms the client answered
  MISS to 12 of 20 laser queries while standing in the beam. Now the client remembers when its own
  laser last touched its hitbox, answers HIT when that was within 0.35 s, and otherwise holds the
  query up to 0.3 s for the laser to arrive on its screen before answering MISS. Test: stand in a
  sweeping laser as a joined player (damage every 0.2 s as on the host), step through it once
  (one hit), and stand next to it (no hit).
- Since 1.4.6 the hostile-bullet test on a joined client pre-filters with a 2D AABB. Before, it
  used Bounds.Intersects on Collider2D.bounds, which compares z as well; bullets spawn at
  z = height, so every hostile bullet flying above the ground was skipped before the real distance
  test and a joined player was never hit by it (the host and the client's own bullets go through
  Overlap, which ignores z). Test: cat assassin / thief knives and shuriken, mole rocks, archer
  arrows against a joined player standing still and walking through the path; each visible contact
  should damage once, near misses should stay misses.
- Since 1.4.6 hostile attached swings whose prefab has no NetworkTransform follow the owner on the
  joined client (the same attach offset the host sends) instead of staying at the spawn point. In
  the assets that is exactly the Library DemonBook's `CMelee_LibraryDemonBook_Move` (3000 s life),
  `CMelee_LibraryDemonBook_PackmanMove` and `CMelee_DemonBookJumpAttack_Stamp`; every other melee
  prefab carries one. Before, a joined player was never hit by a dashing book (the host stops testing
  that pair, the client's copy of the swing never moved). The host player was never affected: host
  side tests for the host's own avatar are untouched. Test: Library floors, DemonBook dash / pacman
  charge / jump stamp against a joined player (each contact once, guard and dodge honoured).
- 1.4.2 was the last build installed on the dev machine; 1.4.3 was never built or played. In 1.4.2
  a joined player's own bullets could not hit anything (the client filtered them with
  `Bullet.AttackableFactionLayers`, which is 0 on clients), which is the "my projectiles deal no
  damage" symptom. 1.4.3+ use the mask from `CSD::BulletCollision`.
- Since 1.4.4 the host sends `CSD::BulletCollision` from a prefix of `Bullet.OnSpawnFinalized`, so
  it precedes RPCs vanilla sends from inside that method (`RaycastArrow.RpcSetSprite`, which is the
  moment the client runs its hitscan; before, every client-side hitscan was dropped for lack of the
  spawn state). A client that gets a hitscan before the spawn state keeps it pending, and a bullet
  that never receives the state (already in flight when the client was enabled) is treated as
  hostile after 0.5 s; the host's own faction test still rejects a wrong pair. Test: bow / crossbow
  hitscan shots from a joined player against monsters, and an archer's hitscan against a joined
  player; joining a lobby mid-fight with enemy bullets already flying.
- Since 1.4.4 a report the host did not apply (Fail_Absolute) is retried after 0.25 s instead of
  every frame (one round trip per frame per pair before).
- Since 1.4.4 swings that can hit a joined player linger on the host past their duration like the
  joined player's own swings already did, and register nothing more on the host while lingering
  (only late client reports apply). Before, a monster swing was despawned at its 0.15-0.33 s
  duration and the joined player's report, one round trip late, found nothing: monster melee vs. a
  joined player was mostly lost at RTT > ~100 ms. The client stops testing hostile swings at their
  nominal duration, so the hit window does not grow. Test: monster melee against a joined player at
  ~100-200 ms RTT (every visible contact should damage once, no hits after the swing FX ends), and
  that the host player is not hit by swings past their visible end.
- Since 1.4.3 hostile bullets are classified with the projectile's authoritative
  `AttackableFactionLayers`, not a mask recomputed with the wrong damage-source type. Straight
  `UniformVector2` and `Shuriken` bullets also sweep their collider between consecutive client
  positions. Test the mole grenadier's `Stone` / `JJangStone`, the cat assassin's `CatShuriken` /
  `CatAssassinThrowingKnife`, and panther dagger variants against a stationary joined player and a
  player crossing the projectile path. Each contact should damage once; a projectile passing in
  front of or behind the player should remain a miss.
- Since 1.4.3 Pentaxis (`Unit_LibraryGuard`) SpinDash contact is tested over the boss path shown on
  the joined client and reported to the host. Test straight crossings, wall bounces, guard/perfect
  guard, dodge, remaining inside the box for over one second, and visual near-misses.
- Since 1.4.1 player `Shuriken*` bullets sweep their collider between consecutive client positions.
  Test every dagger-to-shuriken upgrade against a stationary target, a moving target, and two lined-up
  targets (pierce variants); each target should be hit once and contacts should follow travel order.
- Since 1.4.0 the growth-parry `DaggerGrowthBullet` uses a CSD spawn id because vanilla creates it
  independently on every peer instead of network-spawning it. Test both directions: a joined
  player's dagger hitting a monster, and another player's dagger hitting the joined player (guard,
  perfect guard, dodge, and a visual miss). With debug logging, the spawn id should be matched on
  the client before its first hit and each accepted pair should appear once on the host.
- A host-detected bullet vs. a modded player is not consumed until the reply arrives (≈RTT). Since 1.3.0 such a
  bullet is parked (frozen) if it wants to destroy itself in that window and the replay rewinds it to where the hit
  was detected, so a wall right behind the player no longer loses the hit (regardless of the bullet-hit feature: it
  is kept until the reply is in). Rigidbody-driven bullets, lasers and owner-attached bullets are the exception.
- Client-registered hits (flow B/C) resolve one round trip after the client saw the contact; the host object is kept
  alive that long (melee: linger past durationTimer; bullets: parked at wall / lifetime end - explosive bullets only at
  a wall, their timer / arrival destroy detonates on time). Watch for zombie bullets resting on walls for up to the
  grace (2×RTT + 0.9 s) in near-miss situations - that is expected. Clients keep testing a parked bullet 0.75 s after
  the collision-off so a target right in front of the wall still gets its report.

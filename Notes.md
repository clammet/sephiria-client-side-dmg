Testing notes:
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

Testing notes:
- A host-detected bullet vs. a modded player is not consumed until the reply arrives (≈RTT). Since 1.3.0 such a
  bullet is parked (frozen) if it wants to destroy itself in that window and the replay rewinds it to where the hit
  was detected, so a wall right behind the player no longer loses the hit (regardless of the bullet-hit feature: it
  is kept until the reply is in). Rigidbody-driven bullets, lasers and owner-attached bullets are the exception.
- Client-registered hits (flow B/C) resolve one round trip after the client saw the contact; the host object is kept
  alive that long (melee: linger past durationTimer; bullets: parked at wall / lifetime end - explosive bullets only at
  a wall, their timer / arrival destroy detonates on time). Watch for zombie bullets resting on walls for up to the
  grace (2×RTT + 0.4 s) in near-miss situations - that is expected. Clients keep testing a parked bullet 0.25 s after
  the collision-off so a target right in front of the wall still gets its report.

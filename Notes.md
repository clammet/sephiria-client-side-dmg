Testing notes:
- A host-detected bullet vs. a modded player is not consumed until the reply arrives (≈RTT). Since 1.3.0 such a
  bullet is parked (frozen) if it wants to destroy itself in that window and the replay rewinds it to where the hit
  was detected, so a wall right behind the player no longer loses the hit. Rigidbody-driven bullets are the exception.
- Client-registered hits (flow B/C) resolve one round trip after the client saw the contact; the host object is kept
  alive that long (melee: linger past durationTimer; bullets: parked at wall / lifetime end). Watch for zombie bullets
  resting on walls for up to the grace (2×RTT + 0.15 s) in near-miss situations - that is expected.

using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace ClientSideDamage
{
    /// <summary>
    /// Everything that runs on the machine hosting the game (the Mirror server).
    ///
    /// Four flows:
    ///  A. Damage the host itself detected against a joined player (monster melee, AoE, traps...):
    ///     instead of resolving it with the host's copy of that player's guard/dodge state, we ask
    ///     the client for its state, and when the answer arrives we run the vanilla damage code
    ///     with that state forced in. For Bullet.AttackOnServer and MeleeCollision.Attack the
    ///     whole caller is parked (and replayed on reply) so its own bookkeeping - pierce counts,
    ///     on-attack callbacks, hit FX, consumption of the projectile - runs with the real result;
    ///     every other caller is parked inside UnitAvatar.ApplyDamage.
    ///  B. Bullet hits involving a joined player: the client detects them and reports them together
    ///     with its state; the host applies them immediately (no extra round trip).
    ///  C. Melee swings performed by a joined player: the client detects the hits and reports them;
    ///     the host applies vanilla damage to the victims.
    ///  D. Area / ground / boss hits the host detected against a joined player (everything that is
    ///     an overlap query followed by ApplyDamage): the shape of the host's test is attached to
    ///     the flow A query and the client answers whether that shape touches it in its own view
    ///     (see AreaHits.cs). A "no" drops the damage.
    /// For B and C the host's own collision test for those projectiles is switched off.
    /// </summary>
    public static class ServerSide
    {
        public class ModdedClient
        {
            public NetworkConnectionToClient conn;
            public PlayerAvatar avatar;
            public CsdFeatures features;          // negotiated: HostFeatures() & requested
            public CsdFeatures requested;         // what the client last asked for
            public bool acked;
            public int helloAttempts;
            public float nextHelloTime;
            public bool announced;                // status chat line sent for the current handshake
            private Collider2D[] _hitboxes;
            private PlayerAvatar _hitboxOwner;
            private float _hitboxesRefresh;

            /// <summary>The avatar's hitbox colliders (cached briefly: they are looked up per bullet destroy).</summary>
            public Collider2D[] Hitboxes()
            {
                if (_hitboxes == null || _hitboxOwner != avatar || Time.time >= _hitboxesRefresh)
                {
                    _hitboxOwner = avatar;
                    _hitboxes = avatar != null ? AreaGeom.HitboxColliders(avatar) : new Collider2D[0];
                    _hitboxesRefresh = Time.time + 0.5f;
                }
                return _hitboxes;
            }
        }

        private enum PendingKind { Damage, Bullet, Melee }

        private sealed class PendingDamage
        {
            public uint id;
            public PendingKind kind;
            public PlayerAvatar victim;
            public ModdedClient client;
            public float createdAt;
            public bool useSnapshot;   // resolve with the client's guard/dodge state (DamageTakenAuthority)
            public bool hasShape;      // the client also verifies the hit position (AreaHits)
            // Damage: parked inside UnitAvatar.ApplyDamage
            public DamageInstance damage;
            // Bullet: parked Bullet.AttackOnServer(hitT)
            public Bullet bullet;
            public Vector2 direction;
            // Melee: parked MeleeCollision.Attack(type, hitPoint, hitT)
            public MeleeCollision melee;
            public int meleeType;
            public Vector3 hitPoint;
            public Transform hitT;
            public long pairKey;       // Bullet / Melee: (projectile netId, victim netId)
            public Vector3 bulletPos;  // Bullet: where the bullet was when the hit was detected (the replay rewinds it there)
            public uint projectileNetId;  // Bullet / Melee: pooled objects may be re-used before the reply
        }

        private static readonly Dictionary<int, ModdedClient> _clients = new Dictionary<int, ModdedClient>();
        private static readonly Dictionary<uint, PendingDamage> _pending = new Dictionary<uint, PendingDamage>();
        private static readonly HashSet<long> _pendingPairs = new HashSet<long>();
        private static readonly Dictionary<long, float> _missCooldown = new Dictionary<long, float>();
        private static readonly List<uint> _tmpIds = new List<uint>();
        private static readonly List<long> _tmpKeys = new List<long>();
        private static uint _nextRequestId;
        private static float _nextScan;
        private static float _nextAreaRefresh;
        private static readonly List<PlayerAvatar> _areaAvatars = new List<PlayerAvatar>();

        private const int MaxPending = 64;
        /// <summary>After the client rejected a bullet's area shape, the same (bullet, victim) pair is not asked again for this long.</summary>
        private const float MissCooldown = 0.15f;

        /// <summary>Set while we call back into vanilla UnitAvatar.ApplyDamage so our own prefix lets it through.</summary>
        [ThreadStatic] public static bool ApplyDamageBypass;
        /// <summary>Set while we call back into vanilla Bullet.AttackOnServer / MeleeCollision.Attack.</summary>
        [ThreadStatic] public static bool ProjectileBypass;
        /// <summary>The bullet whose Bullet.DestroySelf must run vanilla-style right now (being released from the park, or consumed by a reported hit).</summary>
        [ThreadStatic] public static Bullet BulletDestroyBypass;
        /// <summary>The non-networked growth-parry dagger whose vanilla HitCheck is currently applying damage.</summary>
        [ThreadStatic] private static DaggerGrowthBullet _currentDagger;

        private sealed class DaggerTrack
        {
            public uint id;
            public DaggerGrowthBullet projectile;
            public UnitAvatar owner;
            public Vector2 spawnPosition;
            public Vector2 direction;
            public float damage;
            public string damageId;
            public float hitRadius;
            public float maxTravel;
            public float expiresAt;
            public readonly HashSet<CombatBehaviour> attacked = new HashSet<CombatBehaviour>();
        }

        private static readonly Dictionary<uint, DaggerTrack> _daggersById = new Dictionary<uint, DaggerTrack>();
        private static readonly Dictionary<DaggerGrowthBullet, DaggerTrack> _daggersByObject = new Dictionary<DaggerGrowthBullet, DaggerTrack>();
        private static readonly List<DaggerTrack> _pendingDaggers = new List<DaggerTrack>();
        private static readonly List<uint> _tmpDaggerIds = new List<uint>();
        private static uint _nextDaggerId;

        // Optimistic value returned to a caller that parks its damage inside ApplyDamage (flow A
        // callers other than Bullet / MeleeCollision, which are parked and replayed whole). The
        // caller's own result handling runs now, with this value, and is NOT replayed when the
        // real verdict arrives. Success is the statistically best guess (most hits land) and it
        // keeps attacker on-hit callbacks / hit counters / success sounds working; the price is
        // that those fire even when the deferred verdict turns out to be a block or dodge.
        private const EApplyDamageResult PendingResult = EApplyDamageResult.Success;

        public static CsdFeatures HostFeatures()
        {
            CsdFeatures f = CsdFeatures.None;
            if (!Plugin.On) return f;
            if (Plugin.HostDamageTakenAuthority.Value) f |= CsdFeatures.DamageTakenAuthority;
            if (Plugin.HostBulletHitAuthority.Value) f |= CsdFeatures.BulletHits;
            if (Plugin.HostMeleeHitAuthority.Value) f |= CsdFeatures.MeleeHits;
            if (Plugin.HostAreaHitAuthority.Value) f |= CsdFeatures.AreaHits;
            return f;
        }

        // ------------------------------------------------------------------ lifecycle

        public static void Reset()
        {
            _clients.Clear();
            _pending.Clear();
            _pendingPairs.Clear();
            _missCooldown.Clear();
            _lingering.Clear();
            _parked.Clear();
            _parkedDue.Clear();
            _motion.Clear();
            _daggersById.Clear();
            _daggersByObject.Clear();
            _pendingDaggers.Clear();
            _tmpDaggerIds.Clear();
            _nextDaggerId = 0;
            _bulletReporters = 0;
            _justParked = null; _movingBullet = null; _movingHits = 0; _currentDagger = null;
            AreaRecorder.Clear();
        }

        // ------------------------------------------------------------------ non-networked growth-parry daggers

        /// <summary>
        /// Called immediately before Charm_GrowthParry sends vanilla's RpcCreateBullet. The CSD
        /// record therefore reaches each remote client before (or in the same reliable batch as)
        /// the visual spawn and supplies the id that vanilla's plain MonoBehaviour does not have.
        /// </summary>
        public static void OnDaggerSpawn(UnitAvatar owner, DaggerGrowthBullet template, Vector2 position, Vector2 direction, float damage)
        {
            if (!NetworkServer.active || !Plugin.On || !Plugin.HostBulletHitAuthority.Value || owner == null) return;
            uint id;
            do { id = ++_nextDaggerId; } while (id == 0 || _daggersById.ContainsKey(id));

            Vector2 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.right;
            DaggerTrack track = new DaggerTrack
            {
                id = id,
                owner = owner,
                spawnPosition = position,
                direction = dir,
                damage = damage,
                damageId = template != null ? template.damageId : "DaggerGrowthBullet",
                hitRadius = template != null ? template.hitRadius : 0.5f,
                maxTravel = DaggerMaxTravel(template),
                expiresAt = Time.time + DaggerLifetime(template),
            };
            _daggersById.Add(id, track);
            _pendingDaggers.Add(track);

            uint ownerNetId = owner.netId;
            Action<NetworkWriter> payload = w =>
            {
                w.WriteUInt(id);
                w.WriteUInt(ownerNetId);
                w.WriteVector2(position);
                w.WriteVector2(dir);
                w.WriteFloat(damage);
            };
            int sent = 0;
            foreach (ModdedClient mc in _clients.Values)
            {
                if (!mc.acked || (mc.features & CsdFeatures.BulletHits) == 0 || mc.avatar == null || mc.conn == null || !mc.conn.isReady) continue;
                CsdRpc.SendToClient(mc.avatar, mc.conn, CsdRpc.DaggerSpawn, payload);
                sent++;
            }
            if (Plugin.DebugOn) Plugin.Debug("[CSD/host] growth dagger spawn id " + id + " owner=" + owner.name + " -> " + sent + " client(s)");
        }

        public static void OnDaggerInitialized(DaggerGrowthBullet projectile, UnitAvatar owner, Vector2 position, Vector2 direction, float damage, bool isServerObject)
        {
            if (projectile == null || !isServerObject || !NetworkServer.active) return;
            DaggerTrack match = null;
            for (int i = 0; i < _pendingDaggers.Count; i++)
            {
                DaggerTrack t = _pendingDaggers[i];
                if (t.projectile == null && t.owner == owner && DaggerSpawnMatches(t, position, direction, damage))
                {
                    match = t;
                    break;
                }
            }
            if (match == null)
            {
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] growth dagger visual had no pending spawn record");
                return;
            }
            _pendingDaggers.Remove(match);
            match.projectile = projectile;
            match.damageId = projectile.damageId;
            match.hitRadius = projectile.hitRadius;
            match.maxTravel = DaggerMaxTravel(projectile);
            match.expiresAt = Mathf.Max(match.expiresAt, Time.time + DaggerLifetime(projectile));
            _daggersByObject[projectile] = match;
        }

        private static bool DaggerSpawnMatches(DaggerTrack t, Vector2 position, Vector2 direction, float damage)
        {
            if ((t.spawnPosition - position).sqrMagnitude > 0.01f) return false;
            Vector2 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.right;
            if (Vector2.Dot(t.direction, dir) < 0.999f) return false;
            return Mathf.Abs(t.damage - damage) <= Mathf.Max(0.01f, Mathf.Abs(damage) * 0.001f);
        }

        private static float DaggerMaxTravel(DaggerGrowthBullet d)
        {
            if (d == null) return 12f;
            float coast = Mathf.Max(0f, d.travelDistance);
            float stop = d.deceleration > 0.001f ? d.moveSpeed * d.moveSpeed / (2f * d.deceleration) : 6f;
            return Mathf.Clamp(coast + stop, 1f, 30f);
        }

        private static float DaggerLifetime(DaggerGrowthBullet d)
        {
            if (d == null) return 8f;
            float coast = d.moveSpeed > 0.001f ? d.travelDistance / d.moveSpeed : 0f;
            float slow = d.deceleration > 0.001f ? d.moveSpeed / d.deceleration : 1f;
            return Mathf.Clamp(coast + slow + d.despawnDelay + d.destroyFallbackTime + 3f, 5f, 15f);
        }

        private static void SweepDaggers(float now)
        {
            if (_daggersById.Count == 0) return;
            _tmpDaggerIds.Clear();
            foreach (KeyValuePair<uint, DaggerTrack> kv in _daggersById)
                if (kv.Value.expiresAt <= now) _tmpDaggerIds.Add(kv.Key);
            for (int i = 0; i < _tmpDaggerIds.Count; i++)
            {
                DaggerTrack t;
                if (!_daggersById.TryGetValue(_tmpDaggerIds[i], out t)) continue;
                _daggersById.Remove(t.id);
                _pendingDaggers.Remove(t);
                if (t.projectile != null) _daggersByObject.Remove(t.projectile);
            }
        }

        public static void OnDaggerGone(DaggerGrowthBullet projectile)
        {
            if (projectile == null) return;
            DaggerTrack track;
            if (!_daggersByObject.TryGetValue(projectile, out track)) return;
            _daggersByObject.Remove(projectile);
            track.projectile = null;   // keep the authoritative spawn data for late reliable reports
        }

        public static DaggerGrowthBullet PushDaggerHitCheck(DaggerGrowthBullet projectile)
        {
            DaggerGrowthBullet previous = _currentDagger;
            _currentDagger = projectile;
            return previous;
        }

        public static void PopDaggerHitCheck(DaggerGrowthBullet previous)
        {
            _currentDagger = previous;
        }

        /// <summary>
        /// The vanilla dagger inserts the victim into its permanent hitTargets set immediately
        /// before ApplyDamage. Returning Fail_Absolute here consumes that host observation without
        /// mutating health; a matching client report replays the same DamageInstance construction.
        /// </summary>
        public static bool TrySuppressDaggerServerDamage(UnitAvatar victim, DamageInstance damage, out EApplyDamageResult result)
        {
            result = EApplyDamageResult.Fail_Absolute;
            if (ApplyDamageBypass || _currentDagger == null || damage == null || !NetworkServer.active || !Plugin.On || !Plugin.HostBulletHitAuthority.Value) return false;
            DaggerTrack track;
            if (!_daggersByObject.TryGetValue(_currentDagger, out track)) return false;
            if (damage.origin != track.owner || damage.id != track.damageId || damage.damageType != EDamageType.Projectile || damage.fromType != EDamageFromType.DirectAttack || damage.elementalType != EDamageElementalType.Chaos) return false;
            if (!IsClientAuthoritative(track.owner, CsdFeatures.BulletHits) && !IsClientAuthoritative(victim, CsdFeatures.BulletHits)) return false;
            if (Plugin.DebugOn) Plugin.Debug("[CSD/host] suppressed host growth dagger " + track.id + " -> " + victim.name);
            return true;
        }

        /// <summary>Tells the area hit recorder which players' hits are client-verified, and counts the clients that report bullet hits.</summary>
        private static void RefreshAreaTargets()
        {
            _areaAvatars.Clear();
            int reporters = 0;
            foreach (ModdedClient mc in _clients.Values)
            {
                if (!mc.acked || mc.avatar == null) continue;
                if ((mc.features & CsdFeatures.BulletHits) != 0) reporters++;
                if ((mc.features & CsdFeatures.AreaHits) != 0) _areaAvatars.Add(mc.avatar);
            }
            _bulletReporters = reporters;
            AreaRecorder.Refresh(_areaAvatars);
        }

        public static void OnPlayerAvatarStartServer(PlayerAvatar pa)
        {
            if (!NetworkServer.active || pa == null) return;
            Track(pa.connectionToClient, pa);
        }

        private static ModdedClient Track(NetworkConnectionToClient conn, PlayerAvatar pa)
        {
            if (conn == null || conn == NetworkServer.localConnection) return null;
            ModdedClient mc;
            if (!_clients.TryGetValue(conn.connectionId, out mc))
            {
                mc = new ModdedClient { conn = conn, avatar = pa, nextHelloTime = Time.time };
                _clients[conn.connectionId] = mc;
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] tracking connection " + conn.connectionId);
                AnnounceIfHostOff(mc);
            }
            else if (pa != null && mc.avatar != pa)
            {
                // avatar was re-created (rejoin / new run): re-do the handshake on the new object
                mc.avatar = pa;
                mc.acked = false;
                mc.helloAttempts = 0;
                mc.nextHelloTime = Time.time;
                mc.announced = false;
                AnnounceIfHostOff(mc);
                RefreshAreaTargets();
            }
            return mc;
        }

        public static void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            if (conn == null) return;
            ModdedClient mc;
            if (_clients.TryGetValue(conn.connectionId, out mc))
            {
                _clients.Remove(conn.connectionId);
                RefreshAreaTargets();
                // resolve whatever was waiting on this client with the host's state
                DrainPending(p => p.client == mc, true, "client " + conn.connectionId + " disconnected");
            }
        }

        public static void Tick()
        {
            if (!NetworkServer.active)
            {
                if (_clients.Count > 0 || _pending.Count > 0 || _daggersById.Count > 0) Reset();
                _lobbyPhase = -1;
                _hostLinePending = false;
                _chat.Clear();
                return;
            }
            float now = Time.time;

            PollLobbyCreated();
            FlushHostLine();
            FlushChat();

            if (now >= _nextScan)
            {
                _nextScan = now + 1f;
                foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
                {
                    if (conn == null || conn == NetworkServer.localConnection || conn.identity == null) continue;
                    PlayerAvatar pa = conn.identity.GetComponent<PlayerAvatar>();
                    if (pa != null) Track(conn, pa);
                }
                // a client that never answered our hellos does not run the mod (or a very old one)
                foreach (ModdedClient mc in _clients.Values)
                {
                    if (mc.acked || mc.announced || mc.helloAttempts < UnansweredHellosBeforeAnnounce) continue;
                    mc.announced = true;
                    AnnouncePlayer(mc, "OFF - mod not detected on their side (host runs v" + Plugin.VERSION + ")");
                }
                if (_missCooldown.Count > 0)
                {
                    _tmpKeys.Clear();
                    foreach (KeyValuePair<long, float> kv in _missCooldown) if (kv.Value <= now) _tmpKeys.Add(kv.Key);
                    for (int i = 0; i < _tmpKeys.Count; i++) _missCooldown.Remove(_tmpKeys[i]);
                }
                SweepMotion();
                SweepDaggers(now);
            }
            if (!Plugin.Ready) return;   // status only; nothing else may run un-initialised

            if (now >= _nextAreaRefresh)
            {
                _nextAreaRefresh = now + 0.5f;
                RefreshAreaTargets();
            }
            AreaRecorder.Tick();

            foreach (ModdedClient mc in _clients.Values)
            {
                if (!Plugin.On || mc.acked || mc.avatar == null || mc.helloAttempts >= 8 || now < mc.nextHelloTime) continue;
                if (mc.conn == null || !mc.conn.isReady) { mc.nextHelloTime = now + 1f; continue; }
                mc.helloAttempts++;
                mc.nextHelloTime = now + (mc.helloAttempts < UnansweredHellosBeforeAnnounce ? 1f : 2f);
                CsdFeatures hf = HostFeatures();
                CsdRpc.SendToClient(mc.avatar, mc.conn, CsdRpc.Hello, w => { w.WriteInt(Plugin.PROTOCOL_VERSION); w.WriteByte((byte)hf); });
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] hello #" + mc.helloAttempts + " -> conn " + mc.conn.connectionId);
            }

            if (_pending.Count > 0)
            {
                float timeout = Plugin.HostReplyTimeout.Value;
                DrainPending(p => now - p.createdAt > timeout, true, "timed out");
            }
            if (_lingering.Count > 0) TickLingeringMelee(now);
            if (_parked.Count > 0) TickParkedBullets(now);
        }

        // ------------------------------------------------------------------ flow C: client-owned swings outlive their duration until the reports are in

        /// <summary>
        /// A swing performed by a modded client is hit-tested by that client and reported back;
        /// the report arrives one round trip after the swing was spawned. Vanilla destroys the
        /// swing after durationTimer (a katana swing lives ~0.15 s), so with a round trip longer
        /// than that every report would find the swing already gone and be dropped - i.e. the
        /// player would not hit anything at all. So a client-owned swing is kept alive (invisible,
        /// and its own host-side hit test is already suppressed) for an RTT-derived grace period
        /// after vanilla wanted to destroy it. The client stops testing at the swing's nominal
        /// duration (see ClientSide.OnMeleeUpdate), so the hit window itself does not grow.
        /// </summary>
        private sealed class LingeringMelee
        {
            public MeleeCollision melee;
            public float destroyAt;
        }
        private static readonly List<LingeringMelee> _lingering = new List<LingeringMelee>();

        private static float ReportGrace(ModdedClient mc)
        {
            double rtt = Rtt(mc);
            // two round trips (reports of the swing's last frames leave one duration after the first) plus jitter
            return Mathf.Clamp((float)rtt * 2f + 0.15f, 0.3f, 2f);
        }

        /// <summary>MeleeCollision.DestroySelf prefix: true = keep the swing for now (destroyed from Tick later).</summary>
        public static bool TryDeferMeleeDestroy(MeleeCollision m, bool forceDestroy)
        {
            if (forceDestroy || m == null || !NetworkServer.active || !Plugin.On || !Plugin.HostMeleeHitAuthority.Value) return false;
            if (!m.isServer || m.netId == 0 || !CsdUtil.IsBaseMeleeUpdate(m)) return false;
            ModdedClient mc = GetModdedClient(m.owner);
            if (mc == null || (mc.features & CsdFeatures.MeleeHits) == 0) return false;
            for (int i = 0; i < _lingering.Count; i++)
            {
                if (!ReferenceEquals(_lingering[i].melee, m)) continue;
                return Time.time < _lingering[i].destroyAt;   // durationTimer fired again while lingering
            }
            float grace = ReportGrace(mc);
            _lingering.Add(new LingeringMelee { melee = m, destroyAt = Time.time + grace });
            if (Plugin.DebugOn) Plugin.Debug("[CSD/host] melee " + m.name + " (netId " + m.netId + ") of conn " + mc.conn.connectionId + " lingers " + grace.ToString("0.00") + "s for late reports (rtt " + (mc.conn.rtt * 1000.0).ToString("0") + " ms)");
            return true;
        }

        private static readonly List<MeleeCollision> _lingeringDue = new List<MeleeCollision>();

        private static void TickLingeringMelee(float now)
        {
            // collect first, destroy after: destroying one swing may remove others from the list re-entrantly
            _lingeringDue.Clear();
            for (int i = _lingering.Count - 1; i >= 0; i--)
            {
                LingeringMelee l = _lingering[i];
                if (l.melee == null) { _lingering.RemoveAt(i); continue; }   // destroyed by other means (floor move, owner gone)
                if (now < l.destroyAt) continue;
                _lingering.RemoveAt(i);
                _lingeringDue.Add(l.melee);
            }
            for (int i = 0; i < _lingeringDue.Count; i++)
            {
                if (_lingeringDue[i] == null) continue;
                try { _lingeringDue[i].DestroySelf(true); }
                catch (Exception e) { Plugin.Log.LogError("[CSD/host] destroying lingering swing failed: " + e); }
            }
            _lingeringDue.Clear();
        }

        // ------------------------------------------------------------------ flow B: bullets parked at their point of destruction while a report may be in flight

        /// <summary>
        /// The bullet twin of the melee linger. A bullet a modded client hit-tests (its own bullet
        /// against enemies, or a hostile bullet against itself) keeps flying on the host while the
        /// client's report travels back; when the host destroys it in that window - a wall right
        /// behind the target, the lifetime timer running out - the report finds nothing and the
        /// hit is lost (and a fast fireball would explode at the wall *behind* a player who blocked
        /// it in front). So when such a bullet asks to destroy itself normally while somebody who
        /// might have reported it is within reach (its speed times the round trip), it is parked:
        /// frozen where it is (its Update is skipped, so no movement, collision or timers), clients
        /// are told its collision is off (they keep testing it for a short interpolation grace,
        /// see ClientSide.OnBulletCollisionSync), and it is destroyed vanilla-style once the grace
        /// period passed without a report. A report that arrives meanwhile is applied with the
        /// bullet rewound to the reported contact point (see OnBulletHit), so a hit that consumes
        /// it explodes there, from the front, exactly like a vanilla hit.
        ///
        /// Only destroys that are a *miss* are parked. A destroy that vanilla issues because the
        /// bullet just hit somebody (pierce exhausted) can never be redeemed by a report, and a
        /// destroy that *is* the attack (an explosive reaching its arrival point / timer, a meteor
        /// landing) must detonate now: bullets whose destroy module carries a payload are only
        /// parked when the destroy came from tile contact (see the BulletMoveModule.Move hooks) or
        /// a host-detected hit is waiting for the client's reply. Bullets driven by a
        /// TopdownRigidbody (lobbed / physical projectiles), lasers and owner-attached bullets are
        /// never parked.
        /// </summary>
        private sealed class ParkedBullet
        {
            public Bullet bullet;
            public float releaseAt;
            public Vector3 pos;          // where it was frozen (restored after the Update that parked it finished moving it)
            public bool pendingOnly;     // parked only for a pending host-detected hit: released as soon as that resolves
        }
        private sealed class BulletMotion
        {
            public Vector3 lastPos;
            public float speed;    // units per second, smoothed
            public bool parked;
        }
        private enum ParkReason : byte { None, Pending, OwnBullet, HostileBullet }

        private static readonly List<ParkedBullet> _parked = new List<ParkedBullet>();
        private static readonly List<ParkedBullet> _parkedDue = new List<ParkedBullet>();
        private static readonly Dictionary<Bullet, BulletMotion> _motion = new Dictionary<Bullet, BulletMotion>();
        private static readonly Collider2D[] _parkScratch = new Collider2D[64];
        private static readonly List<Bullet> _motionTmp = new List<Bullet>();
        private static Bullet _justParked;          // parked during the Bullet.Update running right now: its position is restored in the postfix
        private static Bullet _movingBullet;        // bullet whose BulletMoveModule.Move is running / just returned this Update
        private static int _movingHits;             // Move's tile contact count for that bullet (0 while Move is still running)
        private static int _bulletReporters;        // acked clients with BulletHits (refreshed with the area targets)
        /// <summary>How long a modded client keeps testing a bullet after we told it collision is off (its interpolated copy lags behind ours).</summary>
        public const float ClientTestGrace = 0.25f;

        private static double Rtt(ModdedClient mc) { return mc != null && mc.conn != null ? mc.conn.rtt : 0.15; }

        /// <summary>Grace for a parked bullet: the client's report of a contact it sees up to ClientTestGrace after our collision-off, plus jitter.</summary>
        private static float BulletParkGrace(ModdedClient mc)
        {
            return Mathf.Clamp((float)Rtt(mc) * 2f + ClientTestGrace + 0.15f, 0.45f, 2f);
        }

        public static bool IsParked(Bullet b)
        {
            BulletMotion m;
            return b != null && _motion.TryGetValue(b, out m) && m.parked;
        }

        private static ParkedBullet FindParked(Bullet b)
        {
            for (int i = 0; i < _parked.Count; i++) if (ReferenceEquals(_parked[i].bullet, b)) return _parked[i];
            return null;
        }

        /// <summary>Bullet.Update prefix on the host. Returns true when the vanilla update must be skipped (parked bullet).</summary>
        public static bool OnBulletUpdateHost(Bullet b)
        {
            if (b == null || !Plugin.On) return false;
            BulletMotion m;
            bool known = _motion.TryGetValue(b, out m);
            if (known && m.parked)
            {
                if (ReferenceEquals(_justParked, b)) _justParked = null;
                return true;
            }
            // speed estimate for the park radius: only while a client that reports bullet hits is connected
            if (_bulletReporters == 0 || !Plugin.HostBulletHitAuthority.Value || !b.isServer) return false;
            Vector3 p = b.transform.position;
            if (!known)
            {
                _motion[b] = new BulletMotion { lastPos = p };
                return false;
            }
            float dt = Time.deltaTime;
            if (dt > 0f)
            {
                float v = (p - m.lastPos).magnitude / dt;
                m.speed = m.speed <= 0f ? v : m.speed * 0.7f + v * 0.3f;
            }
            m.lastPos = p;
            return false;
        }

        /// <summary>
        /// Bullet.Update postfix on the host. A bullet parked from inside its own Update (Move's
        /// lifetime timer, an arrival) was still moved by the rest of that Update: put it back where
        /// it was parked so it freezes there.
        /// </summary>
        public static void OnBulletUpdatedHost(Bullet b)
        {
            ClearMoving();
            if (b == null || !ReferenceEquals(_justParked, b)) return;
            _justParked = null;
            ParkedBullet pb = FindParked(b);
            if (pb != null && b.DestroyModule != null && !b.DestroyModule.IsDestroyed) b.transform.position = pb.pos;
        }

        /// <summary>Bullet.Update finalizer: the moving-bullet state never outlives the Update (even one that threw).</summary>
        public static void ClearMoving() { _movingBullet = null; _movingHits = 0; }

        /// <summary>BulletMoveModule.Move prefix/postfix on the host: remembers whether the Move that just ran had tile contact.</summary>
        public static void OnBulletMoveBegin(BulletMoveModule m) { _movingBullet = m != null ? m.Bullet : null; _movingHits = 0; }
        public static void OnBulletMoveEnd(BulletMoveModule m, int hits) { if (m != null && ReferenceEquals(_movingBullet, m.Bullet)) _movingHits = hits; }

        public static void OnBulletGoneHost(Bullet b)
        {
            if (b == null) return;
            _motion.Remove(b);
            if (ReferenceEquals(_justParked, b)) _justParked = null;
            for (int i = _parked.Count - 1; i >= 0; i--) if (ReferenceEquals(_parked[i].bullet, b)) _parked.RemoveAt(i);
        }

        /// <summary>Bullet.DestroySelf prefix: true = keep the bullet parked for now (destroyed from Tick unless a report consumes it).</summary>
        public static bool TryParkBulletDestroy(Bullet b, bool forceDestroy)
        {
            if (forceDestroy || b == null || ReferenceEquals(BulletDestroyBypass, b) || !NetworkServer.active || !Plugin.On) return false;
            if (!b.isServer || b.netId == 0) return false;
            if (_bulletReporters == 0 && _pending.Count == 0) return false;
            if (b.DestroyModule == null || b.DestroyModule.IsDestroyed) return false;
            if (IsParked(b)) return true;
            if (CsdUtil.IsBulletExcluded(b) || IsNeverParked(b)) return false;
            if (b.MoveModule != null && b.MoveModule.TopdownRigidbody != null) return false;   // rigidbody driven: cannot be frozen cleanly
            // the destroy of a bullet that just hit somebody (pierce exhausted): vanilla rejects any later report, nothing to wait for
            if (b.pierceCreatureCount > 0 && b.pierceCreatureCount <= R.BulletCurrentPierceCount(b)) return false;
            float grace;
            ParkReason reason = ShouldParkBullet(b, out grace);
            if (reason == ParkReason.None) return false;
            BulletMotion m;
            if (!_motion.TryGetValue(b, out m)) { m = new BulletMotion { lastPos = b.transform.position }; _motion[b] = m; }
            m.parked = true;
            ParkedBullet pb = new ParkedBullet { bullet = b, releaseAt = Time.time + grace, pos = b.transform.position, pendingOnly = reason == ParkReason.Pending };
            _parked.Add(pb);
            if (ReferenceEquals(_movingBullet, b)) _justParked = b;   // parked from inside its own Update: the postfix puts it back
            // clients stop testing the frozen bullet (after a short grace for their lagging copy; reports already on the wire still count)
            SyncBulletCollisionState(b, false);
            if (Plugin.DebugOn) Plugin.Debug("[CSD/host] bullet " + b.name + " (netId " + b.netId + ") parked " + grace.ToString("0.00") + "s at " + (Vector2)pb.pos + ": " + reason);
            return true;
        }

        /// <summary>
        /// Lasers (the emitter never moves, the beam is the hit volume, boss code polls IsDestroyed),
        /// owner-attached bullets and bullets that do not die on contact (bounce, boomerang): nothing
        /// flies past them / their destroy is not a miss.
        /// </summary>
        private static bool IsNeverParked(Bullet b)
        {
            if (b.tileCollision == Bullet.ETileCollision.Bounce) return true;
            BulletMoveModule m = b.MoveModule;
            if (m == null) return false;
            return m is BulletMoveModule_Laser || m is BulletMoveModule_Laser_CustomBody || m is BulletMoveModule_AskardLaser
                || m is BulletMoveModule_StickToOwner || m is BulletMoveModule_RevolutionAroundOwner || m is BulletMoveModule_Revolution
                || m is BulletMoveModule_Boomerang || m is BulletMoveModule_LightningBoomerang;
        }

        /// <summary>A destroy module whose non-forced destroy is the attack itself (explosion, spread, ground fire...).</summary>
        private static bool HasDestroyPayload(Bullet b)
        {
            BulletDestroyModule d = b.DestroyModule;
            if (d == null) return false;
            return d.GetType() != typeof(BulletDestroyModule) && !(d is BulletDestroyModule_DestroyImmediate)
                && !(d is BulletDestroyModule_DelayDespawn) && !(d is BulletDestroyModule_PhysicalProjectile);
        }

        private static ParkReason ShouldParkBullet(Bullet b, out float grace)
        {
            grace = 0f;
            ParkReason reason = ParkReason.None;
            float now = Time.time;
            // a host-detected hit against a modded player is parked and waiting for the reply: keep the bullet for the replay
            // (whatever the bullet-hit feature says: those queries come from the damage-taken / area features)
            foreach (PendingDamage p in _pending.Values)
            {
                if (p.kind != PendingKind.Bullet || !ReferenceEquals(p.bullet, b)) continue;
                float left = Plugin.HostReplyTimeout.Value - (now - p.createdAt) + 0.1f;
                grace = Mathf.Max(grace, Mathf.Max(left, ReportGrace(p.client)));
                reason = ParkReason.Pending;
            }
            if (!Plugin.HostBulletHitAuthority.Value || _bulletReporters == 0) return reason;
            bool isRay = b.MoveModule is BulletMoveModule_RaycastArrow;
            if (!isRay && (!b.isCollisionEnabled || b.collosionType == Bullet.ECollisionTiming.None)) return reason;
            // a payload bullet is only parked when it missed into a wall; its timer / arrival destroy is the detonation
            bool tileContact = ReferenceEquals(_movingBullet, b) && _movingHits > 0;
            if (!isRay && !tileContact && HasDestroyPayload(b)) return reason;
            BulletMotion m;
            _motion.TryGetValue(b, out m);
            float speed = m != null ? m.speed : 0f;
            Vector2 pos = b.transform.position;
            ModdedClient owner = GetModdedClient(b.NetworkOwner);
            if (owner != null && (owner.features & CsdFeatures.BulletHits) != 0)
            {
                // the client's own bullet: is there anything it could have hit within reach?
                float reach = ParkReach(speed, owner, isRay ? RayReach(b) : 0f);
                ContactFilter2D f = R.BulletContactFilter(b);
                int n;
                AreaRecorder.Suppress = true;
                try { n = Physics2D.OverlapCircle(pos, reach, f, _parkScratch); }
                finally { AreaRecorder.Suppress = false; }
                if (Plugin.DebugOn && n == _parkScratch.Length) Plugin.Debug("[CSD/host] park query for " + b.name + " filled the scratch buffer (" + n + "), targets may be missed");
                for (int i = 0; i < n; i++)
                {
                    Collider2D c = _parkScratch[i];
                    if (c == null) continue;
                    if (b.MoveModule != null && !b.MoveModule.ValidateCollision(c)) continue;
                    CombatBehaviour cb = CsdUtil.CombatBehaviourFromCollider(c);
                    if (cb == null || (cb == b.NetworkOwner && !b.canAttackOwner) || !CsdUtil.BulletCanHurt(b, cb)) continue;
                    if (CsdUtil.BulletAlreadyAttacked(b, cb)) continue;
                    grace = Mathf.Max(grace, BulletParkGrace(owner));
                    return ParkReason.OwnBullet;
                }
            }
            else
            {
                // somebody else's bullet: is a modded player it could hurt within reach of its hit volume?
                foreach (ModdedClient mc in _clients.Values)
                {
                    if (!mc.acked || (mc.features & CsdFeatures.BulletHits) == 0 || mc.avatar == null) continue;
                    if (mc.avatar.IsDead || !CsdUtil.BulletCanHurt(b, mc.avatar)) continue;
                    float reach = ParkReach(speed, mc, isRay ? RayReach(b) : 0f);
                    if (!WithinReach(b, mc, pos, reach)) continue;
                    grace = Mathf.Max(grace, BulletParkGrace(mc));
                    return ParkReason.HostileBullet;
                }
            }
            return reason;
        }

        /// <summary>Distance from the bullet's hit volume (not just its origin: long bullets) to the player's hitboxes.</summary>
        private static bool WithinReach(Bullet b, ModdedClient mc, Vector2 pos, float reach)
        {
            Collider2D[] boxes = mc.Hitboxes();
            Collider2D ac = b.attackingCollider;
            bool byOrigin = ((Vector2)mc.avatar.transform.position - pos).sqrMagnitude <= reach * reach;
            if (ac == null || !ac.enabled || !ac.gameObject.activeInHierarchy || boxes.Length == 0) return byOrigin;
            for (int i = 0; i < boxes.Length; i++)
            {
                Collider2D hb = boxes[i];
                if (hb == null || !hb.enabled) continue;
                ColliderDistance2D d = ac.Distance(hb);
                if (!d.isValid) return byOrigin;   // no shapes to measure: fall back to the origin test
                if (d.distance <= reach) return true;
            }
            return false;
        }

        /// <summary>How far the bullet may have travelled since a client saw a contact with it: speed x (round trip + margin), plus hitbox sizes.</summary>
        private static float ParkReach(float speed, ModdedClient mc, float extra)
        {
            float t = (float)Rtt(mc) * 1.5f + 0.1f;
            return Mathf.Clamp(speed * t, 0.5f, 12f) + 1f + extra;
        }

        /// <summary>Largest rewind a client report may ask for (see OnBulletHit): the park reach plus the client's interpolation lag.</summary>
        private static float MaxRewind(Bullet b, ModdedClient mc)
        {
            BulletMotion m;
            _motion.TryGetValue(b, out m);
            float speed = m != null ? m.speed : 0f;
            return ParkReach(speed, mc, 0f) + speed * 0.15f;   // + the client's interpolation buffer
        }

        private static bool IsFinite(Vector2 v) { return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y); }

        private static float RayReach(Bullet b)
        {
            BulletMoveModule_RaycastArrow ra = b.MoveModule as BulletMoveModule_RaycastArrow;
            if (ra == null || ra.Bullet == null) return 0f;
            Vector2 span = (Vector2)ra.EndPosition - (Vector2)ra.BeginPosition;
            return Mathf.Min(span.magnitude, 30f);
        }

        /// <summary>A pending host-detected hit on this bullet was resolved: a bullet parked only for it is released next tick.</summary>
        private static void OnPendingBulletResolved(Bullet b)
        {
            if (b == null) return;
            ParkedBullet pb = FindParked(b);
            if (pb == null || !pb.pendingOnly) return;
            foreach (PendingDamage p in _pending.Values) if (p.kind == PendingKind.Bullet && ReferenceEquals(p.bullet, b)) return;   // another query still waits
            pb.releaseAt = Time.time;
        }

        private static void TickParkedBullets(float now)
        {
            // collect first, destroy after: a released bullet's payload can despawn other parked bullets (OnBulletGoneHost edits _parked)
            _parkedDue.Clear();
            for (int i = _parked.Count - 1; i >= 0; i--)
            {
                ParkedBullet pb = _parked[i];
                Bullet b = pb.bullet;
                if (b == null) { _parked.RemoveAt(i); continue; }
                if (b.DestroyModule != null && b.DestroyModule.IsDestroyed)   // consumed by a report meanwhile
                {
                    _parked.RemoveAt(i);
                    BulletMotion m; if (_motion.TryGetValue(b, out m)) m.parked = false;
                    continue;
                }
                if (now < pb.releaseAt) continue;
                _parked.RemoveAt(i);
                _parkedDue.Add(pb);
            }
            for (int i = 0; i < _parkedDue.Count; i++)
            {
                Bullet b = _parkedDue[i].bullet;
                if (b == null || b.DestroyModule == null || b.DestroyModule.IsDestroyed) continue;
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] bullet " + b.name + " (netId " + b.netId + ") released: no report, destroyed where it was parked");
                Bullet prev = BulletDestroyBypass;
                BulletDestroyBypass = b;
                try { b.DestroySelf(false); }
                catch (Exception e) { Plugin.Log.LogError("[CSD/host] destroying parked bullet failed: " + e); }
                finally { BulletDestroyBypass = prev; }
                BulletMotion m;
                if (_motion.TryGetValue(b, out m)) m.parked = false;   // a DelayDespawn-type module keeps the object alive: let it move again
            }
            _parkedDue.Clear();
        }

        /// <summary>Forget motion records of bullets that are gone (OnDespawn handles the normal case; this catches scene unloads).</summary>
        private static void SweepMotion()
        {
            if (_motion.Count == 0) return;
            _motionTmp.Clear();
            foreach (Bullet k in _motion.Keys) if (k == null) _motionTmp.Add(k);
            for (int i = 0; i < _motionTmp.Count; i++) _motion.Remove(_motionTmp[i]);
            _motionTmp.Clear();
        }

        // ------------------------------------------------------------------ handshake

        public static void OnHelloAck(UnitAvatar avatar, NetworkConnectionToClient sender, int protocol, CsdFeatures clientFeatures)
        {
            if (sender == null || sender == NetworkServer.localConnection) return;
            PlayerAvatar pa = avatar as PlayerAvatar;
            if (protocol != Plugin.PROTOCOL_VERSION)
            {
                Plugin.Log.LogWarning("[CSD/host] client " + sender.connectionId + " runs protocol " + protocol + " but we run " + Plugin.PROTOCOL_VERSION + " - mod stays off for that player.");
                ModdedClient mm; _clients.TryGetValue(sender.connectionId, out mm);
                AnnouncePlayer(mm, pa, sender, "OFF - mod version mismatch (host protocol " + Plugin.PROTOCOL_VERSION + ", theirs " + protocol + ") - update both");
                return;
            }
            ModdedClient mc = Track(sender, pa);
            if (mc == null) return;
            mc.acked = true;
            mc.requested = clientFeatures;
            SendEnable(mc);
        }

        private static void SendEnable(ModdedClient mc)
        {
            mc.features = HostFeatures() & mc.requested;
            CsdFeatures f = mc.features;
            CsdRpc.SendToClient(mc.avatar, mc.conn, CsdRpc.Enable, w => w.WriteByte((byte)f));
            Plugin.Log.LogInfo("[CSD/host] client " + mc.conn.connectionId + " enabled with features: " + f);
            RefreshAreaTargets();
            mc.announced = true;
            if (f != CsdFeatures.None) AnnouncePlayer(mc, "ON: " + FeatureList(f));
            else if (!Plugin.On || HostFeatures() == CsdFeatures.None) AnnouncePlayer(mc, "OFF - " + HostOffReason());
            else if (mc.requested == CsdFeatures.None) AnnouncePlayer(mc, "OFF - disabled in their config");
            else AnnouncePlayer(mc, "OFF - no feature enabled on both sides (host: " + FeatureList(HostFeatures()) + ")");
        }

        // ------------------------------------------------------------------ status chat

        // Status lines ride on the game's own chat RPC (DungeonManager.RpcChat: "name : message"
        // in the game log, 120 chars max), so un-modded clients see them too. A joining player's
        // status is broadcast to everybody in the session ("<player>: ON: ..."); the host's own
        // line goes into its local log at lobby creation. (SendChat can still address a single
        // connection - kept for the fallback path.)
        private const string ChatName = "CSD";
        private const string RpcChatFullName = "System.Void DungeonManager::RpcChat(PlayerAvatar,System.String,System.String)";
        private const int UnansweredHellosBeforeAnnounce = 3;   // hellos at 0 / 1 / 2 s: an un-modded client is announced ~2 s after joining

        private struct ChatItem
        {
            public NetworkConnectionToClient conn;   // null: everybody (host included)
            public string msg;
        }
        private static readonly List<ChatItem> _chat = new List<ChatItem>();
        private static int _lobbyPhase = -1;

        /// <summary>
        /// The host created a multiplayer lobby (the moment the game shows "You've created a lobby.
        /// You can now invite people...", see LobbyCreatedHooks): post the host's own status line.
        /// Creating the lobby also moves the host to the assembly area behind a fade / loading
        /// screen, and HUD log lines fade out within seconds - so the line is written into the local
        /// game log (the same call the chat RPC makes on arrival) once the screen has faded back in.
        /// (Nobody else is in the session yet; joining players get their own line.)
        /// </summary>
        public static void OnLobbyCreated()
        {
            // several triggers can fire for one creation (creation handler, system message, phase poll): one line
            if (_hostLinePending || Time.unscaledTime - _hostLineArmedAt < 5f) return;
            _hostLineArmedAt = Time.unscaledTime;
            Plugin.Log.LogInfo("[CSD/host] lobby created: " + HostStatusLine());
            _hostLinePending = true;
            _hostLineNotBefore = Time.unscaledTime + HostLineSettle;
        }
        private static float _hostLineArmedAt = -100f;

        private const float HostLineSettle = 0.5f;   // after the fade-in, so the line is not lost under the loading screen
        private static bool _hostLinePending;
        private static float _hostLineNotBefore;

        private static void FlushHostLine()
        {
            if (!_hostLinePending || Time.unscaledTime < _hostLineNotBefore) return;
            ScreenFader fader = ScreenFader.Instance;
            if (fader != null && fader.IsFading) { _hostLineNotBefore = Time.unscaledTime + HostLineSettle; return; }
            GameLogWriter log = GameLogWriter.Instance;
            if (log == null) return;   // no HUD log yet: keep waiting
            _hostLinePending = false;
            string line = HostStatusLine();
            log.WriteLog(ChatName + " : " + line, Color.cyan);
            Plugin.Log.LogInfo("[CSD/host] posted host status line: " + line + " (fader " + (fader != null ? fader.FadingState.ToString() : "none") + ")");
        }

        /// <summary>
        /// Patch-free trigger (also the only one when the mod is not Ready): the Steam panel flips
        /// DungeonManager.lobbyCreatedPhase to 1 on creation. OnLobbyCreated dedupes.
        /// </summary>
        private static void PollLobbyCreated()
        {
            DungeonManager dm = DungeonManager.Instance;
            if (dm == null) return;
            int phase = dm.lobbyCreatedPhase;
            if (phase == 1 && _lobbyPhase != -1 && _lobbyPhase != 1)
            {
                Plugin.Log.LogInfo("[CSD/host] DungeonManager.lobbyCreatedPhase -> 1");
                OnLobbyCreated();
            }
            _lobbyPhase = phase;
        }

        private static string FeatureList(CsdFeatures f)
        {
            string s = "";
            if ((f & CsdFeatures.DamageTakenAuthority) != 0) s += "guard/dodge, ";
            if ((f & CsdFeatures.BulletHits) != 0) s += "bullets, ";
            if ((f & CsdFeatures.MeleeHits) != 0) s += "melee, ";
            if ((f & CsdFeatures.AreaHits) != 0) s += "area, ";
            return s.Length > 0 ? s.Substring(0, s.Length - 2) : "none";
        }

        /// <summary>Why the host side is not offering anything (only meaningful when it is not).</summary>
        private static string HostOffReason()
        {
            if (!Plugin.Ready) return Plugin.DisabledReason.Length > 0 ? Plugin.DisabledReason : "not initialised";
            if (!Plugin.Enabled.Value) return "disabled in host config";
            return "all host features off in config";
        }

        /// <summary>The host's own one-liner, e.g. "v1.3.0 host ON: guard/dodge, bullets, melee, area, fresh-pos".</summary>
        public static string HostStatusLine()
        {
            CsdFeatures f = HostFeatures();
            if (f == CsdFeatures.None) return "v" + Plugin.VERSION + " host OFF: " + HostOffReason();
            string line = "v" + Plugin.VERSION + " host ON: " + FeatureList(f);
            if (Plugin.HostUseLatestClientPosition.Value) line += ", fresh-pos";
            if (Plugin.HostAreaHitMargin.Value > 0f) line += ", margin " + Plugin.HostAreaHitMargin.Value.ToString("0.#");
            return line;
        }

        /// <summary>A joining player gets told right away when the host side cannot do anything for them.</summary>
        private static void AnnounceIfHostOff(ModdedClient mc)
        {
            if (Plugin.On && HostFeatures() != CsdFeatures.None) return;   // handshake will announce
            mc.announced = true;
            AnnouncePlayer(mc, "OFF - " + HostOffReason());
        }

        /// <summary>Broadcasts a joining player's mod status to everyone: "<name>: ON: ..." / "<name>: OFF - reason".</summary>
        private static void AnnouncePlayer(ModdedClient mc, string status)
        {
            AnnouncePlayer(mc, mc != null ? mc.avatar : null, mc != null ? mc.conn : null, status);
        }

        private static void AnnouncePlayer(ModdedClient mc, PlayerAvatar pa, NetworkConnectionToClient conn, string status)
        {
            if (pa == null && mc != null) pa = mc.avatar;
            string name = null;
            try { if (pa != null) name = pa.Name; } catch { }
            if (string.IsNullOrEmpty(name)) name = conn != null ? "player " + conn.connectionId : "player";
            QueueChat(null, name + ": " + status);
        }

        private static void QueueChat(NetworkConnectionToClient conn, string msg)
        {
            if (msg.Length > DungeonManager.ChatMessageMaxLength) msg = msg.Substring(0, DungeonManager.ChatMessageMaxLength);
            _chat.Add(new ChatItem { conn = conn, msg = msg });
        }

        /// <summary>Sends queued lines once the DungeonManager exists and the addressee is ready to receive RPCs.</summary>
        private static void FlushChat()
        {
            if (_chat.Count == 0) return;
            DungeonManager dm = DungeonManager.Instance;
            for (int i = _chat.Count - 1; i >= 0; i--)
            {
                ChatItem c = _chat[i];
                if (c.conn != null && !NetworkServer.connections.ContainsKey(c.conn.connectionId)) { _chat.RemoveAt(i); continue; }   // addressee left
                if (dm == null || !dm.isServer) continue;
                if (c.conn == null) { if (!NetworkClient.ready) continue; }
                else if (!c.conn.isReady) continue;
                _chat.RemoveAt(i);
                try { SendChat(dm, c.conn, c.msg); }
                catch (Exception e) { Plugin.Log.LogWarning("[CSD/host] status chat failed: " + e.Message); }
            }
        }

        private static void SendChat(DungeonManager dm, NetworkConnectionToClient conn, string msg)
        {
            if (conn == null || conn == NetworkServer.localConnection || !Plugin.Ready)
            {
                // broadcast (host creation line; also the fallback while accessors are unavailable)
                dm.Chat(null, ChatName, msg);
                return;
            }
            // private line: the game's RpcChat payload, delivered as a TargetRpc to this connection only
            NetworkWriterPooled w = NetworkWriterPool.Get();
            try
            {
                w.WriteNetworkBehaviour((NetworkBehaviour)null);
                w.WriteString(ChatName);
                w.WriteString(msg);
                R.SendTargetRPCInternal(dm, conn, RpcChatFullName, RpcChatFullName.GetStableHashCode(), w, Channels.Reliable);
            }
            finally { NetworkWriterPool.Return(w); }
        }

        /// <summary>Host changed a config value at runtime: re-negotiate with every connected modded client.</summary>
        public static void OnHostConfigChanged()
        {
            if (!NetworkServer.active) return;
            foreach (ModdedClient mc in _clients.Values)
            {
                if (!mc.acked || mc.avatar == null || mc.conn == null) continue;
                SendEnable(mc);
            }
            if (!Plugin.On) FlushPending("mod disabled", null, true);
        }

        /// <summary>
        /// Drops (or resolves) every parked damage query. Used on floor / scene transitions so a
        /// hit from the previous floor cannot land after the teleport.
        /// </summary>
        public static void FlushPending(string reason, PlayerAvatar onlyVictim = null, bool applyWithHostState = false)
        {
            if (_pending.Count == 0) return;
            DrainPending(p => onlyVictim == null || p.victim == onlyVictim, applyWithHostState, reason);
        }

        /// <summary>
        /// Removes every parked query that <paramref name="match"/> selects and either resolves it
        /// with the host's own state (vanilla behaviour) or drops it.
        /// </summary>
        private static void DrainPending(Predicate<PendingDamage> match, bool applyWithHostState, string reason)
        {
            if (_pending.Count == 0) return;
            _tmpIds.Clear();
            foreach (KeyValuePair<uint, PendingDamage> kv in _pending)
                if (match(kv.Value)) _tmpIds.Add(kv.Key);
            for (int i = 0; i < _tmpIds.Count; i++)
            {
                PendingDamage p;
                if (!_pending.TryGetValue(_tmpIds[i], out p)) continue;
                Unpark(p);
                if (applyWithHostState) Resolve(p, true, default(CombatSnapshot), true);
            }
            if (_tmpIds.Count > 0 && Plugin.DebugOn)
                Plugin.Debug("[CSD/host] " + (applyWithHostState ? "resolved with host state " : "dropped ") + _tmpIds.Count + " pending damage queries (" + reason + ")");
        }

        public static ModdedClient GetModdedClient(UnitAvatar unit)
        {
            if (!Plugin.On || unit == null || !NetworkServer.active) return null;
            PlayerAvatar pa = unit as PlayerAvatar;
            if (pa == null) return null;
            NetworkConnectionToClient conn = pa.connectionToClient;
            if (conn == null || conn == NetworkServer.localConnection) return null;
            ModdedClient mc;
            if (!_clients.TryGetValue(conn.connectionId, out mc) || !mc.acked || mc.features == CsdFeatures.None) return null;
            return mc;
        }

        public static bool IsClientAuthoritative(UnitAvatar unit, CsdFeatures feature)
        {
            ModdedClient mc = GetModdedClient(unit);
            return mc != null && (mc.features & feature) != 0;
        }

        // ------------------------------------------------------------------ flow A: damage detected by the host

        /// <summary>
        /// Everything vanilla UnitAvatar.ApplyDamage decides purely from host-side state, before it
        /// ever looks at guard/dodge, is left to vanilla (it returns without client-dependent side
        /// effects). Returns true when the client's state can still matter for this hit.
        /// </summary>
        private static bool ShouldAskClient(PlayerAvatar pa, UnitAvatar attacker, long targetFactionLayers, bool hasShape)
        {
            if (pa.IsDead || pa.IsInvulnerable || pa.IsLifeInvincibleApplied) return false;
            if (pa.monsterType != EMonsterType.Dummy && CombatManager.Instance != null && CombatManager.Instance.PeaceMode) return false;
            if (pa.TopdownRigidbody != null && pa.TopdownRigidbody.IsPitFalling) return false;
            if (attacker != null)
            {
                if (pa.NetworkLeader == attacker) return false;
                if (pa.followers.Contains(attacker)) return false;
                if (pa.monsterType != EMonsterType.Dummy && !CombatManager.ContainsAttackableFaction(targetFactionLayers, pa.faction)) return false;
            }
            // A protection point is a consumable shield: vanilla would spend it on this hit. When
            // the client still has to confirm the hit position, that must wait for its answer.
            if (!hasShape && pa.protectionPoint > 0) return false;
            if (pa.IsProtected) return false;
            // counter / parry windows are host-driven (and already latency compensated by the game),
            // hit-invincibility after a hit too: nothing to ask the client about.
            if (pa.isCounterInvincibleApplied > 0 || pa.parryInvincibleApplied > 0 || R.IsHitInvincibleEnabled(pa)) return false;
            return true;
        }

        private static bool IsExcludedDamage(EDamageType type, bool isSystemDamage)
        {
            // Damage kinds that never interact with guard/dodge.
            return isSystemDamage || type == EDamageType.Fall || type == EDamageType.ElementalEffectDamage;
        }

        /// <summary>Which client-side decisions apply to a hit on this player. False when none does.</summary>
        private static bool Wanted(ModdedClient mc, out bool useSnapshot, out bool areaHits)
        {
            useSnapshot = (mc.features & CsdFeatures.DamageTakenAuthority) != 0;
            areaHits = (mc.features & CsdFeatures.AreaHits) != 0 && Plugin.HostAreaHitAuthority.Value;
            return useSnapshot || areaHits;
        }

        private static PendingDamage NewPending(PendingKind kind, PlayerAvatar victim, ModdedClient mc, bool useSnapshot, AreaShape shape)
        {
            uint id = ++_nextRequestId;
            if (id == 0) id = ++_nextRequestId;
            PendingDamage p = new PendingDamage
            {
                id = id,
                kind = kind,
                victim = victim,
                client = mc,
                createdAt = Time.time,
                useSnapshot = useSnapshot,
                hasShape = shape != null,
            };
            _pending[id] = p;
            CsdRpc.SendToClient(victim, mc.conn, CsdRpc.DamageQuery, w =>
            {
                w.WriteUInt(id);
                w.WriteBool(shape != null);
                if (shape != null) shape.Write(w);
            });
            return p;
        }

        private static long PairKey(uint projectileNetId, uint victimNetId) { return ((long)projectileNetId << 32) | victimNetId; }

        private static void Unpark(PendingDamage p)
        {
            _pending.Remove(p.id);
            if (p.kind != PendingKind.Damage) _pendingPairs.Remove(p.pairKey);
            if (p.kind == PendingKind.Bullet) OnPendingBulletResolved(p.bullet);
        }

        /// <summary>
        /// Called from the UnitAvatar.ApplyDamage prefix. Returns true when the call was taken over
        /// (result is then valid), false to let vanilla run.
        /// </summary>
        public static bool TryHandleApplyDamage(UnitAvatar unit, DamageInstance damage, out EApplyDamageResult result)
        {
            result = EApplyDamageResult.Fail_Absolute;
            if (ApplyDamageBypass || damage == null) return false;
            PlayerAvatar pa = unit as PlayerAvatar;
            if (pa == null) return false;
            ModdedClient mc = GetModdedClient(pa);
            if (mc == null) return false;
            bool useSnapshot, areaHits;
            if (!Wanted(mc, out useSnapshot, out areaHits)) return false;

            // Flow D: was this damage produced by an area / ground / boss hit test we recorded this
            // frame? Then the client gets the shape and decides whether it touches it at all.
            bool widened = false;
            AreaShape shape = areaHits ? AreaRecorder.FindShape(pa, CallerContext.Current, out widened) : null;

            // A widened test injected this player into the effect's results although the host's
            // real test missed: that hit only exists if the client confirms it. Whenever we cannot
            // ask, it must be dropped, never handed to vanilla.
            if (IsExcludedDamage(damage.damageType, damage.isSystemDamage))
            {
                if (widened) { result = EApplyDamageResult.Fail_Absolute; return true; }
                return false;
            }
            if (!useSnapshot && shape == null) return false;
            if (!ShouldAskClient(pa, damage.origin as UnitAvatar, damage.targetFactionLayers, shape != null))
            {
                if (widened) { result = EApplyDamageResult.Fail_Absolute; return true; }
                return false;
            }
            if (_pending.Count >= MaxPending)
            {
                Plugin.Log.LogWarning("[CSD/host] too many pending damage queries, resolving with host state");
                if (widened) { result = EApplyDamageResult.Fail_Absolute; return true; }
                return false;
            }

            PendingDamage p = NewPending(PendingKind.Damage, pa, mc, useSnapshot, shape);
            p.damage = CsdUtil.CloneDamage(damage);
            if (Plugin.DebugOn)
                Plugin.Debug("[CSD/host] query " + p.id + " -> conn " + mc.conn.connectionId + " (" + damage.id + " dmg=" + damage.damage + " type=" + damage.damageType + ")"
                    + (shape != null ? " shape: " + shape : " (no shape)"));
            result = PendingResult;
            return true;
        }

        /// <summary>
        /// Called from the Bullet.AttackOnServer prefix for hits the host itself detected. When the
        /// victim is a modded player whose state we would ask for anyway, the whole attack is
        /// parked and replayed on reply (through the same port flow B uses), so the bullet's own
        /// bookkeeping - pierce counter, on-attack callbacks, hit FX, consumption - happens with the
        /// real result instead of an optimistic one. Returns true when the call was swallowed.
        /// </summary>
        public static bool TryParkBulletHit(Bullet b, Transform hit)
        {
            if (ProjectileBypass || b == null || hit == null) return false;
            PlayerAvatar pa = CsdUtil.CombatBehaviourFromCollider(hit) as PlayerAvatar;
            if (pa == null) return false;
            ModdedClient mc = GetModdedClient(pa);
            if (mc == null) return false;
            bool useSnapshot, areaHits;
            if (!Wanted(mc, out useSnapshot, out areaHits)) return false;

            // vanilla AttackOnServer rejects these before it does anything: let it
            if (b.NetworkOwner == null && !b.canAttackEvenIfOwnerIsNull) return false;
            if (b.pierceCreatureCount > 0 && b.pierceCreatureCount <= R.BulletCurrentPierceCount(b)) return false;
            if (b.DestroyModule == null || b.DestroyModule.IsDestroyed) return false;
            if (b.ignored.Contains(pa)) return false;
            if (CsdUtil.BulletAlreadyAttacked(b, pa)) return false;
            if (b.SharedTarget != null && b.SharedTarget.Contains(pa)) return false;

            long key = PairKey(b.netId, pa.netId);
            if (_pendingPairs.Contains(key)) return true;   // already asked for this pair
            float until;
            if (_missCooldown.TryGetValue(key, out until) && Time.time < until) return true;

            bool widened = false;
            AreaShape shape = areaHits ? AreaRecorder.FindShape(pa, CallerContext.Current, out widened) : null;
            EDamageType type = b.IsOverrideDamageType ? b.DamageType : EDamageType.Projectile;
            if (IsExcludedDamage(type, false)) return widened;   // widened + cannot ask: swallow (drop)
            if (!useSnapshot && shape == null) return false;
            if (!ShouldAskClient(pa, b.NetworkOwner, b.AttackableFactionLayers, shape != null)) return widened;
            if (_pending.Count >= MaxPending) return widened;

            // the hit direction as vanilla computes it now (the bullet may have flown past the victim by the time the reply is in)
            Vector2 dir = CsdUtil.VanillaHitDirection(b, b.transform.position, hit.position, b.MoveModule != null ? b.MoveModule.CurMovingDirection : Vector2.zero, false);

            PendingDamage p = NewPending(PendingKind.Bullet, pa, mc, useSnapshot, shape);
            p.bullet = b; p.hitT = hit; p.direction = dir; p.pairKey = key; p.projectileNetId = b.netId; p.bulletPos = b.transform.position;
            _pendingPairs.Add(key);
            if (Plugin.DebugOn)
                Plugin.Debug("[CSD/host] bullet query " + p.id + " " + b.name + " -> " + pa.name + (shape != null ? " shape: " + shape : " (no shape)"));
            return true;
        }

        /// <summary>Same as <see cref="TryParkBulletHit"/> for MeleeCollision.Attack (swing types the host still tests itself).</summary>
        public static bool TryParkMeleeHit(MeleeCollision m, int type, Vector3 hitPoint, Transform hitT)
        {
            if (ProjectileBypass || m == null || hitT == null) return false;
            PlayerAvatar pa = CsdUtil.CombatBehaviourFromCollider(hitT) as PlayerAvatar;
            if (pa == null) return false;
            ModdedClient mc = GetModdedClient(pa);
            if (mc == null) return false;
            bool useSnapshot, areaHits;
            if (!Wanted(mc, out useSnapshot, out areaHits)) return false;

            // vanilla Attack ignores these without side effects: let it
            List<CombatBehaviour> inSwing = R.MeleeAttackedInSwing(m);
            if (inSwing != null && inSwing.Contains(pa)) return false;
            List<CombatBehaviour> shared = R.MeleeSharedTarget(m);
            if (shared != null && shared.Contains(pa)) return false;

            long key = PairKey(m.netId, pa.netId);
            if (_pendingPairs.Contains(key)) return true;

            bool widened = false;
            AreaShape shape = areaHits ? AreaRecorder.FindShape(pa, CallerContext.Current, out widened) : null;
            if (!useSnapshot && shape == null) return false;
            if (!ShouldAskClient(pa, m.owner, m.targetTeam, shape != null)) return widened;
            if (_pending.Count >= MaxPending) return widened;

            PendingDamage p = NewPending(PendingKind.Melee, pa, mc, useSnapshot, shape);
            p.melee = m; p.meleeType = type; p.hitPoint = hitPoint; p.hitT = hitT; p.pairKey = key; p.projectileNetId = m.netId;
            _pendingPairs.Add(key);
            if (Plugin.DebugOn)
                Plugin.Debug("[CSD/host] melee query " + p.id + " " + m.name + " -> " + pa.name + (shape != null ? " shape: " + shape : " (no shape)"));
            return true;
        }

        public static void OnDamageReply(UnitAvatar avatar, NetworkConnectionToClient sender, uint requestId, bool hit, CombatSnapshot snap)
        {
            PendingDamage p;
            if (!_pending.TryGetValue(requestId, out p)) return;
            if (p.client == null || p.client.conn != sender) return;
            Unpark(p);
            Resolve(p, hit, snap, false);
        }

        /// <summary>Applies a parked hit now that we know the outcome (or gave up waiting for it).</summary>
        private static void Resolve(PendingDamage p, bool hit, CombatSnapshot snap, bool hostState)
        {
            if (p.victim == null) return;
            if (!hit && p.hasShape)
            {
                if (p.kind == PendingKind.Bullet) _missCooldown[p.pairKey] = Time.time + MissCooldown;
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] reply " + p.id + ": client not touched by the area, hit dropped");
                return;
            }
            bool force = p.useSnapshot && !hostState;
            if (Plugin.DebugOn) Plugin.Debug("[CSD/host] reply " + p.id + (force ? " " + snap : " (host state)"));
            switch (p.kind)
            {
                case PendingKind.Damage:
                    if (force) ApplyWithSnapshot(p.victim, p.damage, snap);
                    else ApplyVanilla(p.victim, p.damage);
                    break;
                case PendingKind.Bullet:
                {
                    Bullet b = p.bullet;
                    if (b == null || !b.isServer || b.netId == 0 || b.netId != p.projectileNetId || p.hitT == null) return;
                    if (b.DestroyModule == null || b.DestroyModule.IsDestroyed) return;
                    Bullet bb = b; Transform ht = p.hitT; Vector2 dir = p.direction; PlayerAvatar victim = p.victim;
                    byte code;
                    // rewind to where the bullet was when the host detected the hit (see OnBulletHit)
                    bool wasHit = RunBulletAt(b, p.bulletPos, victim, force, snap, () => BulletAttack(bb, victim, ht, dir, out code));
                    ApplyBulletPhase(b, wasHit);
                    break;
                }
                case PendingKind.Melee:
                {
                    MeleeCollision m = p.melee;
                    if (m == null || !m.isServer || m.netId == 0 || m.netId != p.projectileNetId || p.hitT == null) return;
                    MeleeCollision mm = m; Transform ht = p.hitT; int type = p.meleeType; Vector3 hp = p.hitPoint;
                    RunProjectile(p.victim, force, snap, null, () => R.MeleeAttack(mm, type, hp, ht));
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ applying

        public static EApplyDamageResult ApplyVanilla(UnitAvatar victim, DamageInstance damage)
        {
            if (victim == null || damage == null) return EApplyDamageResult.Fail_Absolute;
            bool prev = ApplyDamageBypass;
            ApplyDamageBypass = true;
            try { return victim.ApplyDamage(damage); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] ApplyDamage threw: " + e); return EApplyDamageResult.Fail_Absolute; }
            finally { ApplyDamageBypass = prev; }
        }

        public static EApplyDamageResult ApplyWithSnapshot(PlayerAvatar victim, DamageInstance damage, CombatSnapshot snap)
        {
            if (victim == null || damage == null) return EApplyDamageResult.Fail_Absolute;
            using (new ForcedCombatState(victim, snap))
            {
                EApplyDamageResult r = ApplyVanilla(victim, damage);
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] applied " + damage.id + " to " + victim.name + " with " + snap + " -> " + r);
                return r;
            }
        }

        /// <summary>
        /// Runs a vanilla projectile attack (Bullet.AttackOnServer port / MeleeCollision.Attack)
        /// with our own hooks bypassed. When <paramref name="force"/> is set the victim's guard/dodge
        /// fields are forced to the client's snapshot for the duration, scoped to that victim only.
        /// </summary>
        private static bool RunProjectile(PlayerAvatar victim, bool force, CombatSnapshot snap, Bullet bullet, Func<bool> body)
        {
            bool prevProj = ProjectileBypass;
            Bullet prevDestroy = BulletDestroyBypass;
            ProjectileBypass = true;
            BulletDestroyBypass = bullet;   // a hit consuming this bullet destroys it vanilla-style, it is never parked again (other bullets are unaffected)
            try
            {
                if (!force) return body();
                using (new ForcedCombatState(victim, snap))
                {
                    bool prevApply = ApplyDamageBypass;
                    ApplyDamageBypass = true;
                    try { return body(); }
                    finally { ApplyDamageBypass = prevApply; }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] projectile hit failed: " + e); return false; }
            finally { ProjectileBypass = prevProj; BulletDestroyBypass = prevDestroy; }
        }

        /// <summary>
        /// Runs a bullet attack with the bullet moved to <paramref name="at"/> for the duration, so
        /// everything vanilla derives from the bullet position - hit FX placement, and above all a
        /// destroy-on-hit explosion (centred on the victim, from the front, blockable like vanilla's)
        /// - lands there. If the bullet is still spawned afterwards (pierce, dodge, pass, or a
        /// destroy module that keeps the object alive) it goes back where it was in the same frame.
        /// </summary>
        private static bool RunBulletAt(Bullet b, Vector2 at, PlayerAvatar victim, bool force, CombatSnapshot snap, Func<bool> body)
        {
            Vector3 savedPos = b.transform.position;
            b.transform.position = new Vector3(at.x, at.y, savedPos.z);
            try { return RunProjectile(victim, force, snap, b, body); }
            finally
            {
                if (b != null && b.IsSpawned)
                {
                    bool consumed = b.DestroyModule != null && b.DestroyModule.IsDestroyed;
                    // a consumed bullet whose module still has its payload to run over the next frames
                    // (Linebomb, ProgressiveExplode) stays at the contact point; one that merely lingers
                    // (DelayDespawn, PhysicalProjectile) or survived goes back and keeps flying
                    if (!consumed || !HasDestroyPayload(b)) b.transform.position = savedPos;
                    if (consumed) { BulletMotion m; if (_motion.TryGetValue(b, out m)) m.parked = false; }
                }
            }
        }

        /// <summary>
        /// Temporarily overwrites the private guard/dodge fields of the victim so that the vanilla
        /// ApplyDamage takes the branches the client's state dictates, and restores whatever
        /// vanilla did not itself change afterwards.
        /// </summary>
        private sealed class ForcedCombatState : IDisposable
        {
            private readonly UnitAvatar _u;
            private readonly int _dodgeDelta;
            private readonly bool _origGuard, _forcedGuard;
            private readonly float _origAngle, _forcedAngle;
            private readonly bool _origPerfect, _forcedPerfect;
            private readonly float _origStart, _forcedStart;
            private readonly float _origLatency;

            public ForcedCombatState(UnitAvatar u, CombatSnapshot snap)
            {
                _u = u;

                // ---- dodge (dash i-frames). isDodgeInvincibleApplied is a nesting counter that
                // vanilla may push/pop during ApplyDamage (invincibility buffs, dash callbacks), so
                // we shift it by a delta and shift it back instead of replacing it.
                NestedBoolean origDodge = R.IsDodgeInvincibleApplied(u);
                int counter = R.NestedCounter(origDodge);
                bool dodge = snap.dodgeInvincible || (!snap.clientOnlyDodge && origDodge.IsTrue());
                int forcedCounter = dodge ? (counter > 0 ? counter : 1) : (counter > 0 ? 0 : counter);
                _dodgeDelta = forcedCounter - counter;
                if (_dodgeDelta != 0) R.IsDodgeInvincibleApplied(u) = new NestedBoolean(forcedCounter);

                // ---- guard
                _origGuard = u.isGuardEnabled;
                _origAngle = u.guardAngle;
                _origPerfect = R.IsPerfectGuardAvailable(u);
                _origStart = R.GuardStartTimer(u);
                _origLatency = R.PerfectGuardLatencyBonus(u);

                bool canGuard = R.IsAvailableGuard(u) && !u.IsDead && (u.GetCustomStatUnsafe("INFINITYMP") > 0 || u.mp > 0);
                _forcedGuard = snap.guardActive && canGuard;
                _forcedAngle = _origAngle;
                _forcedPerfect = _origPerfect;
                _forcedStart = _origStart;
                float latency = _origLatency;
                if (_forcedGuard)
                {
                    if (!_origGuard)
                    {
                        // client raised the guard before the host got around to it: use the default guard
                        if (_forcedAngle <= 0f)
                            _forcedAngle = 190f + 190f * (float)u.GetCustomStat(ECustomStat.GuardAngleBonus) / 100f;
                        _forcedPerfect = true;
                        latency = HorayNetworkManager.CalculateLatency(u.connectionToClient);
                    }
                    float elapsed = snap.guardElapsed;
                    if (elapsed < 0f) elapsed = 0f;
                    // a negative host timer is the bonus window vanilla grants right after a perfect guard - keep it
                    if (_origGuard && _origStart < 0f && _origStart < elapsed) elapsed = _origStart;
                    _forcedStart = elapsed;
                    GuardDirectionOverride.Set(u, snap.guardDir);
                }
                u.isGuardEnabled = _forcedGuard;          // plain field write: no SyncVar dirty bit, nothing sent
                u.guardAngle = _forcedAngle;
                R.IsPerfectGuardAvailable(u) = _forcedPerfect;
                R.GuardStartTimer(u) = _forcedStart;         // seconds since the client saw its guard come up
                R.PerfectGuardLatencyBonus(u) = latency;     // vanilla's own perfect-guard latency bonus stays in effect
            }

            public void Dispose()
            {
                GuardDirectionOverride.Clear();
                if (_u == null) return;
                if (_dodgeDelta != 0)
                {
                    // undo our shift, keeping whatever vanilla pushed/popped in the meantime
                    int now = R.NestedCounter(R.IsDodgeInvincibleApplied(_u));
                    R.IsDodgeInvincibleApplied(_u) = new NestedBoolean(now - _dodgeDelta);
                }
                if (_u.isGuardEnabled == _forcedGuard) _u.isGuardEnabled = _origGuard;
                if (_u.guardAngle == _forcedAngle) _u.guardAngle = _origAngle;
                if (R.IsPerfectGuardAvailable(_u) == _forcedPerfect) R.IsPerfectGuardAvailable(_u) = _origPerfect;
                if (R.GuardStartTimer(_u) == _forcedStart) R.GuardStartTimer(_u) = _origStart;
                R.PerfectGuardLatencyBonus(_u) = _origLatency;
            }
        }

        /// <summary>Makes PlayerAvatar.GetGuardDirection return the client's aim while a forced apply runs.</summary>
        public static class GuardDirectionOverride
        {
            public static UnitAvatar Target;
            public static Vector2 Direction;
            public static bool Active;
            public static void Set(UnitAvatar u, Vector2 dir) { Target = u; Direction = dir; Active = true; }
            public static void Clear() { Active = false; Target = null; }
        }

        // ------------------------------------------------------------------ freshest client position

        /// <summary>
        /// Called from the NetworkTransformReliable.UpdateServer prefix. For a joined (modded)
        /// player's own transform the host normally replays received snapshots through an
        /// interpolation buffer (2+ send intervals behind, more on jittery links). We instead jump
        /// to the newest snapshot as soon as it is in, so every host-side position test - monster
        /// melee, ground/area effects, aiming, damage-query fallbacks - sees the player where their
        /// client most recently said they were. Returns true when handled (skip vanilla).
        /// </summary>
        public static bool TryApplyLatestClientPosition(NetworkTransformReliable nt)
        {
            if (nt == null || !Plugin.On || !Plugin.HostUseLatestClientPosition.Value) return false;
            if (nt.syncDirection != SyncDirection.ClientToServer || nt.isOwned) return false;
            NetworkConnectionToClient conn = nt.connectionToClient;
            if (conn == null || conn == NetworkServer.localConnection) return false;
            ModdedClient mc;
            if (!_clients.TryGetValue(conn.connectionId, out mc) || !mc.acked || mc.features == CsdFeatures.None) return false;
            if (mc.avatar == null || nt.netIdentity != mc.avatar.netIdentity) return false;   // only the player object itself

            var snaps = nt.serverSnapshots;
            if (snaps.Count == 0) return true;   // nothing new: keep the last applied pose, skip vanilla too
            TransformSnapshot latest = snaps.Values[snaps.Count - 1];
            R.NetworkTransformApply(nt, latest, latest);
            snaps.Clear();
            return true;
        }

        // ------------------------------------------------------------------ flow B/C: hits detected by the client

        /// <summary>
        /// Bullet.isCollisionEnabled is toggled by host-only code (laser warm-up, shuriken wind-up,
        /// arrow rain delay...). Clients that register hits for us need the real value, so push it
        /// to them on spawn and on every toggle. Un-modded clients are never sent anything.
        /// </summary>
        public static void SyncBulletCollisionState(Bullet b)
        {
            if (b == null) return;
            // a parked bullet stays "collision off" for the clients whatever the host toggles
            SyncBulletCollisionState(b, b.isCollisionEnabled && !IsParked(b));
        }

        public static void SyncBulletCollisionState(Bullet b, bool enabled)
        {
            if (b == null || !NetworkServer.active || !Plugin.On || !Plugin.HostBulletHitAuthority.Value) return;
            if (!b.isServer || b.netId == 0) return;
            if (_clients.Count == 0) return;
            Action<NetworkWriter> payload = w => w.WriteBool(enabled);
            foreach (ModdedClient mc in _clients.Values)
            {
                if (!mc.acked || (mc.features & CsdFeatures.BulletHits) == 0 || mc.conn == null || !mc.conn.isReady) continue;
                CsdRpc.SendToClient(b, mc.conn, CsdRpc.BulletCollision, payload);
            }
        }

        public static bool ShouldSuppressBulletServerHit(Bullet b, Transform hit)
        {
            if (ProjectileBypass || b == null || !Plugin.On || !Plugin.HostBulletHitAuthority.Value) return false;
            if (CsdUtil.IsBulletExcluded(b)) return false;   // hit volume only simulated host side
            if (IsClientAuthoritative(b.NetworkOwner, CsdFeatures.BulletHits)) return true;
            CombatBehaviour victim = CsdUtil.CombatBehaviourFromCollider(hit);
            if (victim != null && IsClientAuthoritative(victim as UnitAvatar, CsdFeatures.BulletHits)) return true;
            return false;
        }

        public static bool ShouldSuppressMeleeServerHit(MeleeCollision m, Transform hitTransform)
        {
            if (ProjectileBypass || m == null || !Plugin.On || !Plugin.HostMeleeHitAuthority.Value) return false;
            if (!CsdUtil.IsBaseMeleeUpdate(m)) return false;
            if (IsClientAuthoritative(m.owner, CsdFeatures.MeleeHits)) return true;
            CombatBehaviour victim = CsdUtil.CombatBehaviourFromCollider(hitTransform);
            if (victim != null && IsClientAuthoritative(victim as UnitAvatar, CsdFeatures.MeleeHits)) return true;
            return false;
        }

        /// <summary>
        /// Send the swing parameters the client needs for its own shape test (vanilla only sends
        /// them via RpcSpawn, and only for swings that have swing FX). Modded clients only.
        /// </summary>
        public static void SendMeleeSpawn(MeleeCollision m)
        {
            if (m == null || !NetworkServer.active || !Plugin.On || !Plugin.HostMeleeHitAuthority.Value) return;
            if (!m.isServer || m.netId == 0 || _clients.Count == 0) return;
            if (!CsdUtil.IsBaseMeleeUpdate(m)) return;
            uint ownerNetId = m.owner != null ? m.owner.netId : 0u;
            Vector2 begin = m.motionDataBegin, end = m.motionDataEnd;
            float height = m.height, dir = R.MeleeAttachedDirection(m), rangeBonus = m.rangeBonus;
            // vanilla's own owner-relative offset (computed from begin and the owner position of the
            // same host frame) - the client attaches its own swings with exactly this
            Vector3 offset = R.MeleeOffsetFromAvatar(m);
            Action<NetworkWriter> payload = w =>
            {
                w.WriteUInt(ownerNetId); w.WriteVector2(begin); w.WriteVector2(end);
                w.WriteFloat(height); w.WriteFloat(dir); w.WriteFloat(rangeBonus);
                w.WriteVector3(offset);
            };
            int sent = 0;
            foreach (ModdedClient mc in _clients.Values)
            {
                if (!mc.acked || (mc.features & CsdFeatures.MeleeHits) == 0 || mc.conn == null || !mc.conn.isReady) continue;
                CsdRpc.SendToClient(m, mc.conn, CsdRpc.MeleeSpawn, payload);
                sent++;
            }
            if (Plugin.DebugOn && sent > 0)
                Plugin.Debug("[CSD/host] melee spawn " + m.name + " (" + m.GetType().Name + ", netId " + m.netId + ") owner=" + (m.owner != null ? m.owner.name + " netId " + ownerNetId : "null")
                    + " client-owned=" + IsClientAuthoritative(m.owner, CsdFeatures.MeleeHits) + " -> " + sent + " client(s)");
        }

        public static void OnBulletHit(UnitAvatar avatar, NetworkConnectionToClient sender, uint bulletNetId, uint victimNetId, byte victimComponent, byte kind, Vector2 direction, Vector2 bulletPos, bool hasSnapshot, CombatSnapshot snap)
        {
            PlayerAvatar me = avatar as PlayerAvatar;
            ModdedClient mc = GetModdedClient(me);
            if (mc == null || mc.conn != sender || (mc.features & CsdFeatures.BulletHits) == 0) return;
            if (!Plugin.HostBulletHitAuthority.Value) return;

            byte code = CsdRpc.HitResult_NotApplied;
            try
            {
                Bullet b = CsdUtil.FindComponent<Bullet>(NetworkServer.spawned, bulletNetId);
                if (b == null || !b.isServer)
                {
                    if (Plugin.DebugOn) Plugin.Debug("[CSD/host] bullet report from conn " + sender.connectionId + " (bullet " + bulletNetId + " -> " + victimNetId + "/" + victimComponent + ") dropped: bullet not spawned (already gone)");
                    return;
                }
                if (CsdUtil.IsBulletExcluded(b)) return;
                CombatBehaviour victim = CsdUtil.FindBehaviour(NetworkServer.spawned, victimNetId, victimComponent) as CombatBehaviour;
                if (victim == null) return;

                bool ownBullet = b.NetworkOwner != null && b.NetworkOwner == me;
                bool victimIsMe = victim == me;
                if (!ownBullet && !victimIsMe) return;   // a client may only register hits that involve itself
                bool isRay = kind == CsdUtil.BulletHitKind_Ray;
                if (isRay)
                {
                    // vanilla fires the hitscan from SetMotionData regardless of the overlap settings
                    if (!(b.MoveModule is BulletMoveModule_RaycastArrow)) return;
                }
                else if (!b.isCollisionEnabled || b.collosionType == Bullet.ECollisionTiming.None) return;

                Transform hitT = CsdUtil.FindHitboxTransform(victim);
                if (hitT == null) return;

                // Rewind: the host's copy of the bullet has travelled one round trip past the point
                // where the client saw the contact. Apply the hit with the bullet at that point (see
                // RunBulletAt). The client only gets to say where within the reach a report can
                // honestly be about - a position that is not finite or further away than the bullet
                // can have flown in that time is ignored (the hit is applied where the host has it).
                Vector2 hostPos = b.transform.position;
                float rewind = 0f;
                bool rewound = false;
                if (!isRay && IsFinite(bulletPos))
                {
                    rewind = (hostPos - bulletPos).magnitude;
                    rewound = rewind <= MaxRewind(b, mc);
                    if (!rewound && Plugin.DebugOn) Plugin.Debug("[CSD/host] bullet " + b.name + " report from conn " + sender.connectionId + ": position " + rewind.ToString("0.0") + "u away from ours ignored");
                }
                bool wasParked = IsParked(b);
                bool force = victimIsMe && hasSnapshot && (mc.features & CsdFeatures.DamageTakenAuthority) != 0;
                byte c = CsdRpc.HitResult_NotApplied;
                bool hit = RunBulletAt(b, rewound ? bulletPos : hostPos, me, force, snap, () => BulletAttack(b, victim, hitT, direction, out c));
                code = c;
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] bullet " + b.name + " -> " + victim.name + " reported by conn " + sender.connectionId + " hit=" + hit + " code=" + code
                    + (rewound ? " rewound " + rewind.ToString("0.00") + "u" : "") + (wasParked ? " (parked)" : ""));
                if (!isRay) ApplyBulletPhase(b, hit);
            }
            finally
            {
                // tell the client how the report went so its own dedupe / pierce bookkeeping matches vanilla
                CsdRpc.SendToClient(me, mc.conn, CsdRpc.BulletHitResult, w =>
                {
                    w.WriteUInt(bulletNetId); w.WriteUInt(victimNetId); w.WriteByte(victimComponent); w.WriteByte(code);
                });
            }
        }

        public static void OnDaggerHit(UnitAvatar avatar, NetworkConnectionToClient sender, uint daggerId, uint victimNetId, byte victimComponent, Vector2 daggerPosition, Vector2 hitPoint, bool hasSnapshot, CombatSnapshot snap)
        {
            PlayerAvatar me = avatar as PlayerAvatar;
            ModdedClient mc = GetModdedClient(me);
            string drop = null;
            if (mc == null || mc.conn != sender || (mc.features & CsdFeatures.BulletHits) == 0) drop = "sender not a bullet-authoritative client";
            else if (!Plugin.HostBulletHitAuthority.Value) drop = "Host.BulletHitAuthority off";

            DaggerTrack track = null;
            CombatBehaviour victim = null;
            UnitAvatar unit = null;
            bool ownDagger = false, victimIsMe = false;
            if (drop == null && (!_daggersById.TryGetValue(daggerId, out track) || track == null)) drop = "dagger id not found (already expired?)";
            if (drop == null && track.expiresAt <= Time.time) drop = "dagger expired";
            if (drop == null)
            {
                victim = CsdUtil.FindBehaviour(NetworkServer.spawned, victimNetId, victimComponent) as CombatBehaviour;
                unit = victim as UnitAvatar;
                ownDagger = track.owner != null && track.owner == me;
                victimIsMe = unit != null && unit == me;
                if (track.owner == null) drop = "dagger owner no longer exists";
                else if (unit == null) drop = "victim not found";
                else if (!ownDagger && !victimIsMe) drop = "neither our dagger nor us as victim";
                else if (unit == track.owner) drop = "dagger owner reported as victim";
                else if (unit.IsDead || !unit.gameObject.activeSelf) drop = "victim is dead or inactive";
                else if (!CombatManager.ContainsAttackableFaction(track.owner.GetHostileFactionLayers(EDamageFromType.DirectAttack), unit.faction)) drop = "victim faction is not attackable";
            }

            // When both endpoints are modded, let the victim's own view win so its report carries
            // the guard/dodge snapshot. The owner's copy will report the same pair as well.
            if (drop == null && ownDagger && !victimIsMe)
            {
                ModdedClient victimClient = GetModdedClient(unit);
                if (victimClient != null && (victimClient.features & CsdFeatures.BulletHits) != 0) drop = "waiting for victim's own report";
            }

            if (drop == null)
            {
                if (!IsFinite(daggerPosition) || !IsFinite(hitPoint)) drop = "non-finite position";
                else
                {
                    Vector2 rel = daggerPosition - track.spawnPosition;
                    float along = Vector2.Dot(rel, track.direction);
                    float perpendicular = Mathf.Abs(rel.x * track.direction.y - rel.y * track.direction.x);
                    float allowance = Mathf.Max(1f, track.hitRadius + 0.75f);
                    if (along < -allowance || along > track.maxTravel + allowance || perpendicular > allowance)
                        drop = "reported position is outside the dagger path";
                }
            }
            if (drop == null && track.attacked.Contains(victim)) drop = "victim already attacked";

            if (drop != null)
            {
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] growth dagger report from conn " + (sender != null ? sender.connectionId.ToString() : "?") + " (dagger " + daggerId + " -> " + victimNetId + "/" + victimComponent + ") dropped: " + drop);
                return;
            }

            // Vanilla adds to hitTargets before ApplyDamage and never retries this pair, including
            // Fail_Absolute. Mirror that ordering before invoking the authoritative damage code.
            track.attacked.Add(victim);
            DamageInstance damage = DamageInstance.GetDamage(track.owner, track.damageId, hitPoint,
                track.owner.GetHostileFactionLayers(EDamageFromType.DirectAttack), track.damage,
                EDamageType.Projectile, EDamageFromType.DirectAttack, track.direction, 0, 0f);
            damage.elementalType = EDamageElementalType.Chaos;

            bool force = victimIsMe && hasSnapshot && (mc.features & CsdFeatures.DamageTakenAuthority) != 0;
            EApplyDamageResult result = force ? ApplyWithSnapshot(me, damage, snap) : ApplyVanilla(unit, damage);
            if (Plugin.DebugOn) Plugin.Debug("[CSD/host] growth dagger " + daggerId + " -> " + victim.name + " reported by conn " + sender.connectionId + " -> " + result);
        }

        /// <summary>Bullet.Update's phase bookkeeping after an AttackOnServer that registered a hit.</summary>
        private static void ApplyBulletPhase(Bullet b, bool hit)
        {
            if (!hit || b == null) return;
            if (b.collosionType == Bullet.ECollisionTiming.Enter)
            {
                if (b.pierceCreatureCount > 0 && b.pierceCreatureCount <= R.BulletCurrentPierceCount(b))
                    R.BulletCollisionPhase(b) = 2;
            }
            else
            {
                R.BulletCollisionPhase(b) = 1;
            }
        }

        /// <summary>
        /// Port of Bullet.AttackOnServer with the hit direction supplied by the caller (the host's
        /// bullet may already have flown past the victim when the report arrives, which would flip
        /// the direction used for the guard-angle test). <paramref name="hitCode"/> tells what the
        /// bookkeeping did with the pair (see CsdRpc.HitResult_*).
        /// </summary>
        private static bool BulletAttack(Bullet b, CombatBehaviour combatBehaviour, Transform hit, Vector2 clientDirection, out byte hitCode)
        {
            hitCode = CsdRpc.HitResult_NotApplied;
            if (b.NetworkOwner == null && !b.canAttackEvenIfOwnerIsNull) return false;
            if (b.pierceCreatureCount > 0 && b.pierceCreatureCount <= R.BulletCurrentPierceCount(b)) return false;
            if (b.DestroyModule.IsDestroyed) return false;

            bool flag = false, flag2 = false, flag3 = false;
            Vector2 vector;
            if (clientDirection.sqrMagnitude > 0.0001f)
            {
                vector = clientDirection;
            }
            else
            {
                vector = CsdUtil.VanillaHitDirection(b, b.transform.position, hit.position, b.MoveModule != null ? b.MoveModule.CurMovingDirection : Vector2.zero, false);
            }

            if (b.ignored.Contains(combatBehaviour)) return false;
            if (CsdUtil.BulletAlreadyAttacked(b, combatBehaviour)) { hitCode = CsdRpc.HitResult_Attacked; return false; }

            if (b.SharedTarget == null || !b.SharedTarget.Contains(combatBehaviour))
            {
                if (b.SharedTarget != null) b.SharedTarget.Add(combatBehaviour);
                EDamageType damageType = EDamageType.Projectile;
                if (b.IsOverrideDamageType) damageType = b.DamageType;
                float damage; int staggeringLevel; float externalForcePower;
                if (b.damageDealtType == Bullet.EDamageDealtType.Normal)
                {
                    damage = b.Damage * b.DamageMultiplier * b.defaultDamageRatio;
                    staggeringLevel = b.StaggeringLevel;
                    externalForcePower = b.ExternalForcePower;
                }
                else
                {
                    damage = 0f; staggeringLevel = 0; externalForcePower = 0f;
                }
                DamageInstance d = DamageInstance.GetDamage(b.NetworkOwner, b.damageId, combatBehaviour.transform.position, b.AttackableFactionLayers, damage, damageType, b.FromType, vector, staggeringLevel, externalForcePower);
                d.criticalChancePercent += b.additionalCriticalChancePercent;
                d.criticalDamageRate += b.additionalCriticalDamageRate;
                d.criticalDamageMultiplier *= b.additionalCriticalDamageMultiplier;
                d.damage += d.damage * b.additionalDamagePercent / 100f;
                d.ignoreDefense += b.ignoreDefense;
                d.elementalType = R.BulletElementalType(b);
                d.flag = b.swingId;
                if (b.useCustomColorType == Bullet.ECustomColorType.Chaos) d.SetCustomColor(true, b.customColor);
                else if (b.useCustomColorType == Bullet.ECustomColorType.Normal) d.SetCustomColor(false, b.customColor);
                d.damageObject = b.gameObject;

                EApplyDamageResult r = combatBehaviour.ApplyDamage(d);
                if (r != EApplyDamageResult.Fail_Absolute)
                {
                    b.attackedList.Add(new Bullet.Attacked { combatBehaviour = combatBehaviour, timer = b.collisionStayDamageIntervalTimer.time });
                    hitCode = CsdRpc.HitResult_Attacked;
                }
                switch (r)
                {
                    case EApplyDamageResult.Success:
                    case EApplyDamageResult.Fail_Block: flag = true; flag3 = true; hitCode = CsdRpc.HitResult_Counted; break;
                    case EApplyDamageResult.Fail_Pass: flag3 = true; break;
                    case EApplyDamageResult.Fail_Dodge: flag2 = true; break;
                }
                if (r == EApplyDamageResult.Success)
                {
                    b.CallOnAttack(combatBehaviour, d, b);
                    if (b.turnOnChainLightning && DungeonManager.Instance != null)
                        DungeonManager.Instance.CreateChainLightning(1, b.damageId, d.damage, b.NetworkOwner, combatBehaviour as UnitAvatar, b.FromType);
                }
            }
            if (flag3)
            {
                Vector3 position = (b.transform.position + hit.transform.position) * 0.5f + (Vector3)UnityEngine.Random.insideUnitCircle * 0.3f;
                float angle = HorayUtility.GetAngleFromVector(vector);
                if (DungeonManager.Instance != null)
                {
                    uint ownerNetId = 0u;
                    if (b.NetworkOwner != null) ownerNetId = b.NetworkOwner.netId;
                    DungeonManager.Instance.BroadcastAttackBullet(ownerNetId, b.canBeTransparentOnMultiplayer, b.spawnedPrefabName, position, angle, (int)combatBehaviour.BodyMaterial);
                }
            }
            if (flag)
            {
                R.BulletHitCount(b)++;
                R.BulletCurrentPierceCount(b)++;
                if (b.pierceCreatureCount > 0 && b.pierceCreatureCount <= R.BulletCurrentPierceCount(b) && b.afterPierceOperation == Bullet.EAfterPierceOperation.Destroy)
                    b.DestroySelf(false);
            }
            return flag || flag2;
        }

        public static void OnMeleeHit(UnitAvatar avatar, NetworkConnectionToClient sender, uint meleeNetId, uint victimNetId, byte victimComponent, Vector2 hitPoint, bool hasSnapshot, CombatSnapshot snap)
        {
            PlayerAvatar me = avatar as PlayerAvatar;
            ModdedClient mc = GetModdedClient(me);
            string drop = null;
            if (mc == null || mc.conn != sender || (mc.features & CsdFeatures.MeleeHits) == 0) drop = "sender not a melee-authoritative client";
            else if (!Plugin.HostMeleeHitAuthority.Value) drop = "Host.MeleeHitAuthority off";
            if (drop != null) { if (Plugin.DebugOn) Plugin.Debug("[CSD/host] melee report from conn " + sender.connectionId + " dropped: " + drop); return; }

            MeleeCollision m = CsdUtil.FindComponent<MeleeCollision>(NetworkServer.spawned, meleeNetId);
            CombatBehaviour victim = CsdUtil.FindBehaviour(NetworkServer.spawned, victimNetId, victimComponent) as CombatBehaviour;
            bool ownSwing = m != null && m.owner != null && m.owner == me;
            bool victimIsMe = victim != null && victim == me;
            Transform hitT = victim != null ? CsdUtil.FindHitboxTransform(victim) : null;
            if (m == null) drop = "swing netId " + meleeNetId + " not spawned (already gone?)";
            else if (!m.isServer) drop = "swing not server side";
            else if (!CsdUtil.IsBaseMeleeUpdate(m)) drop = m.GetType().Name + " runs its own update loop";
            else if (victim == null) drop = "victim netId " + victimNetId + "/" + victimComponent + " not found";
            else if (!ownSwing && !victimIsMe) drop = "neither our swing nor us as victim (owner=" + (m.owner != null ? m.owner.name : "null") + ")";   // a client may only register hits that involve itself
            else if (ownSwing && victimIsMe) drop = "own swing against ourselves";
            else if (hitT == null) drop = "victim " + victim.name + " has no hitbox";
            if (drop != null)
            {
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] melee report from conn " + sender.connectionId + " (swing " + meleeNetId + " -> " + victimNetId + "/" + victimComponent + ") dropped: " + drop);
                return;
            }

            bool force = victimIsMe && hasSnapshot && (mc.features & CsdFeatures.DamageTakenAuthority) != 0;
            bool hit = RunProjectile(me, force, snap, null, () => R.MeleeAttack(m, 0, hitPoint, hitT));
            if (Plugin.DebugOn) Plugin.Debug("[CSD/host] melee " + m.name + " -> " + victim.name + " reported by conn " + sender.connectionId + " hit=" + hit);
        }
    }
}

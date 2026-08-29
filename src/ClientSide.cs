using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace ClientSideDamage
{
    /// <summary>
    /// Everything that runs on a machine that joined somebody else's game (pure Mirror client).
    /// Keeps a locally-predicted guard/dodge state for our own character, answers the host's
    /// damage queries with it, and runs the hit tests for projectiles that involve our character.
    /// </summary>
    public static class ClientSide
    {
        private static bool _hostModded;
        private static bool _helloReceived;
        private static CsdFeatures _hostFeatures;
        private static CsdFeatures _features;
        private static PlayerAvatar _localAvatar;

        // guard prediction
        private static bool _subAttackHeld;
        private static float _subAttackDownTime = -1f;
        private static bool _guardLearned;
        private static WeaponSimple _learnedWeapon;
        private static float _guardStartTime = -1f;

        // dodge prediction
        private static float _dashInvincibleUntil = -1f;

        // damage queries from the host, answered in Tick (after this frame's network messages, incl.
        // the effect's own spawn / RPC, have been processed)
        private sealed class PendingQuery
        {
            public PlayerAvatar avatar;
            public uint requestId;
            public AreaShape shape;
            public float arrivedAt;
        }
        private static readonly List<PendingQuery> _queries = new List<PendingQuery>();

        // ------------------------------------------------------------------ Purification core laser (LibraryChapter4Dorm_CoreLaser)
        //
        // The lasers are plain MonoBehaviours every peer instantiates for itself (RpcCreateLaser);
        // only the group angle is synced (a 16-bit SyncVar), so our copy sweeps one network delay
        // behind the host's. The host tests its box in its own Update and asks us; by the time the
        // query is here the host's box is where the laser was, not where we see it, and the
        // shape cannot be anchored (no NetworkIdentity above the laser). So we track our own
        // copies every frame and answer a laser query from when our laser last touched us,
        // holding the query briefly for a laser that is about to reach us on our screen.
        private static float _laserTouchedAt = -100f;
        private static float _laserSeenAt = -100f;
        private const float LaserTouchWindow = 0.35f;   // a touch this recent confirms the host's hit
        private const float LaserQueryHold = 0.3f;      // how long a laser query waits for our copy to arrive

        public static void OnCoreLaserUpdate(LibraryChapter4Dorm_CoreLaser laser)
        {
            if (laser == null || !Active || !Has(CsdFeatures.AreaHits) || !Plugin.ClientAreaHitVerification.Value) return;
            if (!laser.IsActive() || laser.damageCheckCollider == null) return;
            PlayerAvatar me = LocalAvatar;
            if (me == null || me.IsDead) return;
            _laserSeenAt = Time.time;
            AreaShape shape = new AreaShape();
            shape.kind = AreaShapeKind.Box;
            shape.victim = AreaVictimTest.HitboxCollider;
            shape.center = laser.damageCheckCollider.transform.position;
            shape.size = laser.damageCheckCollider.size;
            shape.angle = laser.laserAngle;
            shape.useTriggers = true;
            shape.useLayerMask = true;
            shape.layerMask = CombatManager.Topdown1FLayerMask;
            if (AreaGeom.HitboxOverlaps(shape, MyHitboxes(me))) _laserTouchedAt = Time.time;
        }

        /// <summary>A thin, long, unanchored box while our own laser copies are running: the core laser's damage box.</summary>
        private static bool IsLaserQuery(AreaShape s)
        {
            return s != null && s.kind == AreaShapeKind.Box && !s.hasAnchor
                && Mathf.Min(s.size.x, s.size.y) <= 0.6f && Mathf.Max(s.size.x, s.size.y) >= 2f
                && Time.time - _laserSeenAt < 1f;
        }

        public static bool Active
        {
            get
            {
                return _hostModded && Plugin.On && NetworkClient.active && !NetworkServer.active;
            }
        }

        public static bool Has(CsdFeatures f) { return (_features & f) != 0; }

        public static PlayerAvatar LocalAvatar
        {
            get
            {
                if (_localAvatar == null || !_localAvatar.isOwned)
                {
                    _localAvatar = null;
                    NetworkIdentity id = NetworkClient.localPlayer;
                    if (id != null) _localAvatar = id.GetComponent<PlayerAvatar>();
                    if (_localAvatar == null && CombatManager.Instance != null) _localAvatar = CombatManager.Instance.CurrentPlayer;
                }
                return _localAvatar;
            }
        }

        public static CsdFeatures ClientFeatures()
        {
            CsdFeatures f = CsdFeatures.None;
            if (!Plugin.On) return f;
            if (Plugin.ClientDamageTakenAuthority.Value) f |= CsdFeatures.DamageTakenAuthority;
            if (Plugin.ClientBulletHitDetection.Value) f |= CsdFeatures.BulletHits;
            if (Plugin.ClientMeleeHitDetection.Value) f |= CsdFeatures.MeleeHits;
            if (Plugin.ClientAreaHitVerification.Value) f |= CsdFeatures.AreaHits;
            return f;
        }

        public static void Reset()
        {
            _hostModded = false;
            _helloReceived = false;
            _hostFeatures = CsdFeatures.None;
            _features = CsdFeatures.None;
            _localAvatar = null;
            _subAttackHeld = false;
            _subAttackDownTime = -1f;
            _guardLearned = false;
            _learnedWeapon = null;
            _guardStartTime = -1f;
            _dashInvincibleUntil = -1f;
            _bullets.Clear();
            _pendingRays = 0;
            _daggers.Clear();
            _melees.Clear();
            _pentaxis.Clear();
            _queries.Clear();
            _laserTouchedAt = -100f;
            _laserSeenAt = -100f;
            _myHitboxes = null;
            _myHitboxOwner = null;
        }

        public static void Tick()
        {
            if (!NetworkClient.active && (_hostModded || _bullets.Count > 0 || _daggers.Count > 0 || _melees.Count > 0 || _queries.Count > 0)) Reset();
            SelfLineTick();
            if (!Plugin.Ready) return;
            if (_queries.Count > 0) AnswerQueries();
            if (_daggers.Count > 0) CleanupDaggerTracks();
            if (_pendingRays > 0) TickPendingRays();
        }

        // ------------------------------------------------------------------ self status lines (local HUD log only)

        // Written into our own game log (never sent anywhere) so a joining player can tell, from
        // their own screen, that the mod is loaded on their side and what the host answered.
        // Posted once the HUD is up and the screen has faded in (a joined player is moved into
        // the assembly area behind a loading screen right after spawning).
        private const string ChatName = "CSD";
        private const float SelfLineSettle = 0.5f;
        private const float HostSilenceSeconds = 8f;
        private static readonly List<string> _selfLines = new List<string>();
        private static float _selfLineNotBefore;
        private static bool _joinAnnounced;
        private static float _joinedAt = -1f;
        private static bool _hostSilenceAnnounced;
        private static bool _wasClient;

        private static void SelfLine(string msg)
        {
            _selfLines.Add(msg);
            if (_selfLineNotBefore < Time.unscaledTime + SelfLineSettle) _selfLineNotBefore = Time.unscaledTime + SelfLineSettle;
        }

        private static void SelfLineTick()
        {
            bool client = NetworkClient.active && !NetworkServer.active;
            if (client != _wasClient)
            {
                _wasClient = client;
                _joinAnnounced = false; _hostSilenceAnnounced = false; _joinedAt = -1f; _selfLines.Clear();
            }
            if (!client) return;
            if (!_joinAnnounced && NetworkClient.ready && NetworkClient.localPlayer != null)
            {
                _joinAnnounced = true;
                _joinedAt = Time.unscaledTime;
                SelfLine("v" + Plugin.VERSION + " loaded on your side" + (Plugin.On ? ", waiting for the host..." : " but disabled: " + (Plugin.Ready ? "General.Enabled = false" : Plugin.DisabledReason)));
                Plugin.Log.LogInfo("[CSD/client] joined a session (mod " + (Plugin.On ? "on" : "off") + ")");
            }
            if (_joinAnnounced && !_hostSilenceAnnounced && !_helloReceived && Plugin.On && Time.unscaledTime - _joinedAt > HostSilenceSeconds)
            {
                _hostSilenceAnnounced = true;
                SelfLine("no answer from the host - it does not seem to run the mod (vanilla combat)");
                Plugin.Log.LogInfo("[CSD/client] no hello from the host after " + HostSilenceSeconds + " s");
            }
            if (_selfLines.Count == 0 || Time.unscaledTime < _selfLineNotBefore) return;
            ScreenFader fader = ScreenFader.Instance;
            if (fader != null && fader.IsFading) { _selfLineNotBefore = Time.unscaledTime + SelfLineSettle; return; }
            GameLogWriter log = GameLogWriter.Instance;
            if (log == null) return;
            for (int i = 0; i < _selfLines.Count; i++) log.WriteLog(ChatName + " : " + _selfLines[i], Color.cyan);
            _selfLines.Clear();
        }

        // ------------------------------------------------------------------ handshake

        public static void OnHello(UnitAvatar avatar, int protocol, CsdFeatures hostFeatures)
        {
            if (NetworkServer.active) return; // host talking to itself, never happens but be safe
            if (avatar == null || !avatar.isOwned) return;
            if (protocol != Plugin.PROTOCOL_VERSION)
            {
                if (!_helloReceived)
                {
                    Plugin.Log.LogWarning("[CSD/client] host runs protocol " + protocol + " but we run " + Plugin.PROTOCOL_VERSION + " - mod stays off for this session.");
                    _hostSilenceAnnounced = true;
                    SelfLine("mod version mismatch (host protocol " + protocol + ", yours " + Plugin.PROTOCOL_VERSION + ") - update both");
                }
                _helloReceived = true;
                // still answer (the hello / ack header has not changed across protocols): the host
                // then announces the mismatch instead of "mod not detected" and stops hailing us
                CsdRpc.SendToServer(avatar, CsdRpc.HelloAck, w => { w.WriteInt(Plugin.PROTOCOL_VERSION); w.WriteByte((byte)CsdFeatures.None); });
                return;
            }
            _helloReceived = true;
            _hostFeatures = hostFeatures;
            PlayerAvatar pa = avatar as PlayerAvatar;
            if (pa != null) _localAvatar = pa;
            SendHelloAck(avatar);
        }

        private static void SendHelloAck(UnitAvatar avatar)
        {
            // With the mod switched off we still answer, asking for no features at all, so the host
            // stops treating us as authoritative (otherwise its own hit tests would stay disabled).
            CsdFeatures want = _hostFeatures & ClientFeatures();
            CsdRpc.SendToServer(avatar, CsdRpc.HelloAck, w => { w.WriteInt(Plugin.PROTOCOL_VERSION); w.WriteByte((byte)want); });
            if (Plugin.DebugOn) Plugin.Debug("[CSD/client] hello ack -> host (host offers " + _hostFeatures + "), requesting " + want);
        }

        /// <summary>A client-side config value changed at runtime: re-negotiate with the host.</summary>
        public static void OnClientConfigChanged()
        {
            if (!_helloReceived || !NetworkClient.active || NetworkServer.active) return;
            PlayerAvatar pa = LocalAvatar;
            if (pa == null) return;
            SendHelloAck(pa);
        }

        public static void OnEnable(UnitAvatar avatar, CsdFeatures features)
        {
            _hostModded = true;
            _features = features;
            PlayerAvatar pa = avatar as PlayerAvatar;
            if (pa != null && pa.isOwned) _localAvatar = pa;
            Plugin.Log.LogInfo("[CSD/client] enabled by host with features: " + features);
            _hostSilenceAnnounced = true;   // the host answered
            SelfLine(features != CsdFeatures.None ? "host enabled: " + FeatureList(features) : "host enabled nothing (check both configs)");
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

        public static void OnDamageQuery(UnitAvatar avatar, uint requestId, AreaShape shape)
        {
            PlayerAvatar pa = avatar as PlayerAvatar;
            if (pa == null || !pa.isOwned) return;
            if (!NetworkClient.active || NetworkServer.active) return;
            _queries.Add(new PendingQuery { avatar = pa, requestId = requestId, shape = shape, arrivedAt = Time.time });
        }

        private static void AnswerQueries()
        {
            for (int i = 0; i < _queries.Count; i++)
            {
                PendingQuery q = _queries[i];
                bool done = true;
                try { done = Answer(q); }
                catch (Exception e) { Plugin.Log.LogError("[CSD/client] damage query " + q.requestId + " failed: " + e); }
                if (done) { _queries.RemoveAt(i); i--; }
            }
        }

        /// <summary>Returns false when the query is kept for a later frame.</summary>
        private static bool Answer(PendingQuery q)
        {
            PlayerAvatar pa = q.avatar;
            if (pa == null) return true;
            bool hit = true;
            string detail = "";
            if (q.shape != null && Plugin.ClientAreaHitVerification.Value && IsLaserQuery(q.shape))
            {
                float since = Time.time - _laserTouchedAt;
                if (since <= LaserTouchWindow) { hit = true; detail = "laser touched us " + since.ToString("0.00") + "s ago"; }
                else if (Time.time - q.arrivedAt < LaserQueryHold) return false;   // our copy of the laser may still be on its way
                else { hit = false; detail = "laser not touching us within " + LaserQueryHold + "s"; }
                if (Plugin.DebugOn) detail = q.shape + " " + detail;
            }
            else if (q.shape != null && Plugin.ClientAreaHitVerification.Value)
            {
                try { hit = AreaVerifier.Evaluate(q.shape, pa, out detail); }
                catch (Exception e)
                {
                    hit = true;
                    Plugin.Log.LogError("[CSD/client] area verification failed, accepting host verdict: " + e);
                }
            }
            CombatSnapshot snap = Capture(pa);
            uint requestId = q.requestId;
            CsdRpc.SendToServer(pa, CsdRpc.DamageReply, w => { w.WriteUInt(requestId); w.WriteBool(hit); snap.Write(w); });
            if (Plugin.DebugOn) Plugin.Debug("[CSD/client] query " + requestId + " -> " + (q.shape != null ? (hit ? "HIT " : "MISS ") + detail + " " : "") + snap);
            return true;
        }

        // ------------------------------------------------------------------ local combat state

        public static void OnSubAttackDown(PlayerAvatar pa)
        {
            if (pa == null || !pa.isOwned || pa.isServer) return;
            _subAttackHeld = true;
            _subAttackDownTime = Time.time;
        }

        public static void OnSubAttackUp(PlayerAvatar pa)
        {
            if (pa == null || !pa.isOwned || pa.isServer) return;
            _subAttackHeld = false;
        }

        /// <summary>SyncVar hook: the host raised / lowered our guard.</summary>
        public static void OnGuardSync(UnitAvatar u, bool enabled)
        {
            if (u == null || u.isServer || !u.isOwned) return;
            if (enabled)
            {
                // The moment we *see* the guard come up is our reference for the perfect-guard
                // window (the host still adds its usual latency bonus on top of it).
                _guardStartTime = Time.time;
                if (_subAttackHeld && _subAttackDownTime >= 0f && Time.time - _subAttackDownTime < 1.5f)
                {
                    // guard came up while the sub-attack button was held right after being pressed:
                    // remember that this weapon guards on that button so we can predict the next one
                    _guardLearned = true;
                    _learnedWeapon = CurrentWeapon(u as PlayerAvatar);
                }
            }
            else
            {
                _guardStartTime = -1f;
            }
        }

        public static void OnDashStarted(CharacterDash dash)
        {
            if (dash == null || dash.UnitAvatar == null) return;
            UnitAvatar u = dash.UnitAvatar;
            if (u.isServer || !u.isOwned) return;
            if (u.GetCustomStatUnsafe("NODASHINVINCIBLE") > 0) return;
            float duration = dash.dodgeInvincibleTime + (float)u.GetCustomStat(ECustomStat.DashInvincibleTimeBonus) / 1000f;
            if (duration > 0f) _dashInvincibleUntil = Time.time + duration;
        }

        private static WeaponSimple CurrentWeapon(PlayerAvatar pa)
        {
            if (pa == null) return null;
            WeaponControllerSimple wc = R.WeaponController(pa);
            return wc == null ? null : wc.currentWeapon;
        }

        /// <summary>Reported when we do not know when the guard came up: far outside any perfect-guard window.</summary>
        private const float UnknownGuardElapsed = 999f;

        public static CombatSnapshot Capture(PlayerAvatar pa)
        {
            CombatSnapshot s = default(CombatSnapshot);
            if (pa == null) return s;
            bool synced = pa.isGuardEnabled;
            bool intent = false;
            if (Plugin.ClientPredictGuardFromInput.Value && _subAttackHeld && _guardLearned && _learnedWeapon != null)
            {
                intent = CurrentWeapon(pa) == _learnedWeapon;
            }
            s.guardActive = synced || intent;
            // The perfect-guard window is decided from this value, so it must fail closed: a guard
            // whose start we never saw (held through a floor change / respawn, hook missed) is
            // reported as an old guard, not a fresh one.
            float elapsed;
            if (_guardStartTime >= 0f) elapsed = Time.time - _guardStartTime;
            else if (intent && _subAttackDownTime >= 0f) elapsed = Time.time - _subAttackDownTime;
            else elapsed = UnknownGuardElapsed;
            s.guardElapsed = Mathf.Max(0f, elapsed);
            s.guardDir = pa.GetGuardDirection();
            s.dodgeInvincible = Time.time < _dashInvincibleUntil;
            s.clientOnlyDodge = Plugin.ClientOnlyDodge.Value;
            return s;
        }

        // ------------------------------------------------------------------ our own hitbox colliders (hostile projectiles are only tested against these)

        private static Collider2D[] _myHitboxes;
        private static PlayerAvatar _myHitboxOwner;
        private static float _myHitboxesRefresh;

        private static Collider2D[] MyHitboxes(PlayerAvatar me)
        {
            if (_myHitboxes == null || _myHitboxOwner != me || Time.time >= _myHitboxesRefresh)
            {
                _myHitboxes = AreaGeom.HitboxColliders(me);
                _myHitboxOwner = me;
                _myHitboxesRefresh = Time.time + 0.5f;
            }
            return _myHitboxes;
        }

        // ------------------------------------------------------------------ bullet hit detection

        private enum BulletRelevance : byte { Unknown = 0, Own = 1, Hostile = 2, Ignore = 3 }

        private struct Attacked
        {
            public CombatBehaviour cb;
            public float timer;
            public float retryAt;          // > 0: the host did not apply the report (Fail_Absolute); the pair may be reported again from then on
        }

        private class BulletTrack
        {
            public BulletRelevance relevance;
            public bool factionFallback;   // classified without the host's faction mask (see Classify)
            public float firstSeen;
            public int phase;
            public float phaseTimer;
            public int reported;           // reports the host has (optimistically) counted towards pierce
            public bool hasLast;
            public Vector3 lastPos;
            public float testUntil;        // keep testing until then after the host switched collision off (our copy lags behind the host's)
            public bool hasCollisionState;
            public bool hasTargetFactions;
            public long targetFactionLayers;   // host-only Bullet field supplied by CSD::BulletCollision
            public bool hasPendingRay;     // a RaycastArrow sprite RPC arrived before the spawn state: run the ray once it is in
            public float pendingRayDistance;
            public readonly List<Attacked> attacked = new List<Attacked>();

            public int IndexOf(CombatBehaviour cb)
            {
                for (int i = 0; i < attacked.Count; i++) if (attacked[i].cb == cb) return i;
                return -1;
            }
        }

        private static readonly Dictionary<Bullet, BulletTrack> _bullets = new Dictionary<Bullet, BulletTrack>();
        private static readonly Collider2D[] _colliders = new Collider2D[16];
        private static readonly TopdownRigidbody[] _rigidbodies = new TopdownRigidbody[16];
        private static readonly RaycastHit2D[] _bulletSweepHits = new RaycastHit2D[32];
        private static readonly List<Bullet> _pendingRayTmp = new List<Bullet>();
        private static int _pendingRays;

        /// <summary>
        /// How long a bullet waits for the host's spawn-state RPC (CSD::BulletCollision) before it
        /// is classified without it. The RPC normally arrives in the same batch as the spawn; it is
        /// missing for bullets that were already in flight when this client was enabled, and it
        /// arrives late for RPCs vanilla sends from inside OnSpawnFinalized on older hosts.
        /// </summary>
        private const float TargetFactionWait = 0.5f;

        /// <summary>
        /// After the host answered a report with "not applied" (vanilla would test the pair again
        /// next frame), the same pair is not reported again for this long. Vanilla's retest is a
        /// local overlap; ours is a round trip per frame.
        /// </summary>
        private const float NotAppliedRetry = 0.25f;

        private static BulletTrack GetTrack(Bullet b)
        {
            BulletTrack track;
            if (!_bullets.TryGetValue(b, out track))
            {
                track = new BulletTrack { firstSeen = Time.time };
                _bullets[b] = track;
            }
            return track;
        }

        public static void OnBulletGone(Bullet b)
        {
            if (b == null) return;
            BulletTrack track;
            if (_bullets.TryGetValue(b, out track) && track.hasPendingRay) _pendingRays--;
            _bullets.Remove(b);
        }

        /// <summary>Hitscan arrows whose spawn state never came: run them with the fallback classification.</summary>
        private static void TickPendingRays()
        {
            _pendingRayTmp.Clear();
            foreach (KeyValuePair<Bullet, BulletTrack> kv in _bullets)
            {
                if (kv.Value.hasPendingRay && (kv.Value.hasTargetFactions || Time.time - kv.Value.firstSeen >= TargetFactionWait)) _pendingRayTmp.Add(kv.Key);
            }
            for (int i = 0; i < _pendingRayTmp.Count; i++)
            {
                Bullet b = _pendingRayTmp[i];
                BulletTrack track;
                if (b == null || !_bullets.TryGetValue(b, out track)) continue;
                RunPendingRay(b, track);
            }
            _pendingRayTmp.Clear();
        }

        private static void RunPendingRay(Bullet b, BulletTrack track)
        {
            if (!track.hasPendingRay) return;
            track.hasPendingRay = false;
            _pendingRays--;
            BulletMoveModule_RaycastArrow arrow = b.MoveModule as BulletMoveModule_RaycastArrow;
            if (arrow == null) return;
            try { OnRaycastArrowSprite(arrow, track.pendingRayDistance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/client] deferred hitscan detection failed: " + e); }
        }

        /// <summary>
        /// Host pushed the authoritative Bullet.isCollisionEnabled value. Collision-off also comes
        /// with a bullet the host parked at a wall (see ServerSide.TryParkBulletDestroy): our
        /// interpolated copy is still one buffer behind the host's and may not have reached the
        /// target yet, so a bullet we were testing keeps being tested for a short grace.
        /// </summary>
        public static void OnBulletCollisionSync(Bullet b, bool enabled, long targetFactionLayers)
        {
            if (b == null) return;
            BulletTrack track = GetTrack(b);
            track.hasTargetFactions = true;
            track.targetFactionLayers = targetFactionLayers;
            // classified without the mask while waiting for it: redo that with the real one
            if (track.factionFallback) { track.factionFallback = false; track.relevance = BulletRelevance.Unknown; }
            // Only grant interpolation grace on a real enabled -> disabled transition. Some
            // projectiles (notably CatShuriken) spawn collision-off while winding up; their first
            // state sync must not create a premature damage window.
            if (track.hasCollisionState && !enabled && b.isCollisionEnabled && b.isClient && !b.isServer)
            {
                track.testUntil = Time.time + ServerSide.ClientTestGrace;
            }
            track.hasCollisionState = true;
            b.isCollisionEnabled = enabled;
            if (track.hasPendingRay) RunPendingRay(b, track);
        }

        /// <summary>
        /// The host told us what it did with a hit we reported. Vanilla only blacklists a victim
        /// (attackedList) when the result was not Fail_Absolute and only counts it towards pierce
        /// on Success / Block; mirror that so the bullet keeps trying targets vanilla would retry
        /// (invulnerable, wrong faction...) and does not stop early because of dodged reports.
        /// </summary>
        public static void OnBulletHitResult(UnitAvatar avatar, uint bulletNetId, uint victimNetId, byte victimComponent, byte code)
        {
            if (code == CsdRpc.HitResult_Counted) return;
            Bullet b = CsdUtil.FindComponent<Bullet>(NetworkClient.spawned, bulletNetId);
            if (b == null) return;
            BulletTrack track;
            if (!_bullets.TryGetValue(b, out track)) return;
            CombatBehaviour cb = CsdUtil.FindBehaviour(NetworkClient.spawned, victimNetId, victimComponent) as CombatBehaviour;
            if (cb == null) return;
            if (code == CsdRpc.HitResult_NotApplied)
            {
                // vanilla would retest the pair next frame; we retry after a short cooldown
                int i = track.IndexOf(cb);
                if (i >= 0)
                {
                    Attacked a = track.attacked[i];
                    a.retryAt = Time.time + NotAppliedRetry;
                    track.attacked[i] = a;
                }
            }
            if (track.reported > 0) track.reported--;
            if (track.phase == 2 && !(b.pierceCreatureCount > 0 && b.pierceCreatureCount <= track.reported)) track.phase = 0;
        }

        private static BulletRelevance Classify(Bullet b, BulletTrack track, PlayerAvatar me)
        {
            if (CsdUtil.IsBulletExcluded(b)) return BulletRelevance.Ignore;
            UnitAvatar owner = b.NetworkOwner;
            if (owner != null && owner == me) return BulletRelevance.Own;
            // AttackableFactionLayers is runtime-only in vanilla (not a SyncVar), so wait for the
            // host's CSD spawn-state RPC instead of guessing from the owner and the wrong damage
            // source type. That guess discarded valid cat knives / shuriken.
            if (!track.hasTargetFactions)
            {
                if (Time.time - track.firstSeen < TargetFactionWait) return BulletRelevance.Unknown;
                // No spawn state after the wait (bullet already in flight when we were enabled, or
                // an older host): assume it can hurt us. The host still applies its own faction
                // test to every report, so a wrong guess costs a rejected report, never a wrong hit.
                track.factionFallback = true;
                if (Plugin.DebugOn) Plugin.Debug("[CSD/client] bullet " + b.name + " (netId " + b.netId + ") has no spawn state from the host after " + TargetFactionWait + "s, treating it as hostile");
                return BulletRelevance.Hostile;
            }
            if (me.monsterType != EMonsterType.Dummy &&
                !CombatManager.ContainsAttackableFaction(track.targetFactionLayers, me.faction)) return BulletRelevance.Ignore;
            return BulletRelevance.Hostile;
        }

        public static void OnBulletUpdate(Bullet b)
        {
            if (b == null || !Active || !Has(CsdFeatures.BulletHits) || !Plugin.ClientBulletHitDetection.Value) return;
            if (!b.isClient || b.isServer) return;
            // cheapest gates first: most bullets on screen cannot collide right now
            if (b.collosionType == Bullet.ECollisionTiming.None) return;

            BulletTrack track = GetTrack(b);
            float dt = Time.deltaTime;
            // vanilla ticks the phase-1 cooldown whenever it is not testing, collision enabled or not
            if (track.phase == 1)
            {
                track.phaseTimer += dt;
                if (track.phaseTimer >= R.BulletCollisionCheckTimer(b).time) { track.phase = 0; track.phaseTimer = 0f; }
            }
            if (b.collosionType == Bullet.ECollisionTiming.Stay)
            {
                for (int i = track.attacked.Count - 1; i >= 0; i--)
                {
                    Attacked a = track.attacked[i];
                    a.timer -= dt;
                    if (a.timer <= 0f) track.attacked.RemoveAt(i);
                    else track.attacked[i] = a;
                }
            }
            Vector3 pos = b.transform.position;
            bool hasPreviousPos = track.hasLast;
            Vector2 moveDir = hasPreviousPos ? (Vector2)(pos - track.lastPos) : Vector2.zero;
            track.lastPos = pos;
            track.hasLast = true;
            bool testing = b.isCollisionEnabled || Time.time < track.testUntil;
            if (!testing || track.phase != 0 || b.attackingCollider == null) return;

            PlayerAvatar me = LocalAvatar;
            if (me == null) return;
            if (track.relevance == BulletRelevance.Unknown) track.relevance = Classify(b, track, me);
            if (track.relevance == BulletRelevance.Unknown) return;   // authoritative spawn metadata has not arrived yet
            if (track.relevance == BulletRelevance.Ignore) return;
            bool own = track.relevance == BulletRelevance.Own;

            bool anyHit = false;
            if (b.collisionDemension == Bullet.ECollisionDemension.Normal && hasPreviousPos && ShouldSweepBullet(b))
            {
                anyHit |= SweepBulletHits(b, track, me, own, pos, moveDir);
            }
            if (b.collisionDemension == Bullet.ECollisionDemension.Ground)
            {
                int n = TopdownSpatialHash.ColliderCastNonAlloc(b.attackingCollider, _rigidbodies);
                for (int i = 0; i < n; i++)
                {
                    if (_rigidbodies[i] == null) continue;
                    anyHit |= ConsiderBulletHit(b, track, me, own, _rigidbodies[i].transform, pos, moveDir);
                }
            }
            else if (own)
            {
                int n = b.attackingCollider.Overlap(R.BulletContactFilter(b), _colliders);
                for (int i = 0; i < n; i++)
                {
                    Collider2D c = _colliders[i];
                    if (c == null) continue;
                    if (b.MoveModule != null && !b.MoveModule.ValidateCollision(c)) continue;
                    anyHit |= ConsiderBulletHit(b, track, me, own, c.transform, pos, moveDir);
                }
            }
            else
            {
                // hostile bullet: only a hit on our own hitbox matters, so instead of a full overlap
                // query we test the bullet's collider against our few hitbox colliders directly
                Collider2D[] mine = MyHitboxes(me);
                if (mine.Length == 0) return;
                // Cheap AABB pre-test in 2D only: Collider2D.bounds carries the transform's z with
                // zero extent, and Bounds.Intersects compares z too. Bullets spawn at z = height,
                // so the 3D test never passed for a bullet flying above the ground and no hostile
                // bullet was ever reported (vanilla's Overlap, and our own-bullet path, ignore z).
                Bounds bb = b.attackingCollider.bounds;
                ContactFilter2D f = R.BulletContactFilter(b);
                for (int i = 0; i < mine.Length; i++)
                {
                    Collider2D hb = mine[i];
                    if (hb == null || !hb.enabled || !Intersects2D(bb, hb.bounds)) continue;
                    if (f.IsFilteringLayerMask(hb.gameObject) || f.IsFilteringTrigger(hb) || f.IsFilteringDepth(hb.gameObject)) continue;
                    if (b.MoveModule != null && !b.MoveModule.ValidateCollision(hb)) continue;
                    ColliderDistance2D d = b.attackingCollider.Distance(hb);
                    if (!d.isValid || d.distance > 0f) continue;
                    anyHit |= ConsiderBulletHit(b, track, me, own, hb.transform, pos, moveDir);
                }
            }
            if (anyHit)
            {
                if (b.collosionType == Bullet.ECollisionTiming.Enter)
                {
                    if (b.pierceCreatureCount > 0 && b.pierceCreatureCount <= track.reported) track.phase = 2;
                }
                else
                {
                    track.phase = 1;
                    track.phaseTimer = 0f;
                }
            }
        }

        private static bool Intersects2D(Bounds a, Bounds b)
        {
            return a.min.x <= b.max.x && a.max.x >= b.min.x && a.min.y <= b.max.y && a.max.y >= b.min.y;
        }

        /// <summary>
        /// Straight-moving bullets can cross an entire target between remote NetworkTransform
        /// samples. This includes player shuriken as well as enemy rocks, throwing knives and
        /// shuriken. Sweep the movement modules whose observed path between samples is a straight
        /// segment; curved / orbiting / returning projectiles keep their ordinary overlap test.
        /// </summary>
        private static bool ShouldSweepBullet(Bullet b)
        {
            return b.MoveModule is BulletMoveModule_UniformVector2 ||
                   b.MoveModule is BulletMoveModule_Shuriken;
        }

        private static bool SweepBulletHits(Bullet b, BulletTrack track, PlayerAvatar me, bool own, Vector2 currentPos, Vector2 moveDir)
        {
            float distance = moveDir.magnitude;
            if (distance <= 0.001f) return false;

            // Cast backwards because the collider is already at currentPos. Results are sorted
            // farthest-first so piercing targets are reported in the projectile's travel order.
            Vector2 backwards = -moveDir / distance;
            int n = b.attackingCollider.Cast(backwards, R.BulletContactFilter(b), _bulletSweepHits, distance);
            for (int i = 1; i < n; i++)
            {
                RaycastHit2D hit = _bulletSweepHits[i];
                int j = i - 1;
                while (j >= 0 && _bulletSweepHits[j].distance < hit.distance)
                {
                    _bulletSweepHits[j + 1] = _bulletSweepHits[j];
                    j--;
                }
                _bulletSweepHits[j + 1] = hit;
            }

            bool anyHit = false;
            for (int i = 0; i < n; i++)
            {
                Collider2D c = _bulletSweepHits[i].collider;
                if (c == null) continue;
                if (b.MoveModule != null && !b.MoveModule.ValidateCollision(c)) continue;
                Vector2 contactPos = currentPos + backwards * _bulletSweepHits[i].distance;
                anyHit |= ConsiderBulletHit(b, track, me, own, c.transform, contactPos, moveDir);
            }
            return anyHit;
        }

        // ------------------------------------------------------------------ Pentaxis spin dash

        private sealed class PentaxisTrack
        {
            public bool hasCenter;
            public Vector2 lastCenter;
            public float lastReportAt = -100f;
        }

        private static readonly Dictionary<Unit_LibraryGuard, PentaxisTrack> _pentaxis = new Dictionary<Unit_LibraryGuard, PentaxisTrack>();
        private const float PentaxisHitInterval = 1f;   // Unit_LibraryGuard.attackedList uses the same interval

        /// <summary>
        /// Pentaxis performs its spin-dash overlap only on the host. A joined client sees the boss
        /// one interpolation buffer later, so merely verifying the host's early overlap can reject
        /// it without ever creating the contact the client sees later. Test the exact dash box over
        /// the boss's observed movement and report that local contact back to the host.
        /// </summary>
        public static void OnPentaxisUpdate(Unit_LibraryGuard boss)
        {
            if (boss == null) return;
            if (!Active || !Has(CsdFeatures.AreaHits) || !Plugin.ClientAreaHitVerification.Value)
            {
                _pentaxis.Remove(boss);
                return;
            }
            if (!boss.isClient || boss.isServer) return;

            PentaxisTrack track;
            if (!_pentaxis.TryGetValue(boss, out track))
            {
                track = new PentaxisTrack();
                _pentaxis[boss] = track;
            }

            Vector2 center = (Vector2)boss.transform.position + boss.spinDashCollisionOffset;
            bool attacking = boss.currentState == Unit_LibraryGuard.EState.SpinDash && boss.spinDashSpeed > 6f;
            if (!attacking)
            {
                track.hasCenter = false;
                track.lastCenter = center;
                return;
            }

            PlayerAvatar me = LocalAvatar;
            if (me == null || me.IsDead ||
                !CombatManager.ContainsAttackableFaction(boss.GetHostileFactionLayers(EDamageFromType.DirectAttack), me.faction))
            {
                track.lastCenter = center;
                track.hasCenter = true;
                return;
            }

            Vector2 from = track.hasCenter ? track.lastCenter : center;
            track.lastCenter = center;
            track.hasCenter = true;
            if (Time.time - track.lastReportAt < PentaxisHitInterval) return;

            Collider2D[] mine = MyHitboxes(me);
            if (!PentaxisSweepTouches(from, center, boss.spinDashCollisionSize, mine)) return;

            track.lastReportAt = Time.time;
            Vector2 direction = boss.spinDashDirection.sqrMagnitude > 0.0001f ? boss.spinDashDirection.normalized : (center - from).normalized;
            CombatSnapshot snap = Capture(me);
            uint bossNetId = boss.netId;
            CsdRpc.SendToServer(me, CsdRpc.PentaxisHit, w =>
            {
                w.WriteUInt(bossNetId);
                w.WriteVector2(center);
                w.WriteVector2(direction);
                snap.Write(w);
            });
            if (Plugin.DebugOn) Plugin.Debug("[CSD/client] Pentaxis spin dash -> " + me.name + " at " + center + " " + snap);
        }

        private static bool PentaxisSweepTouches(Vector2 from, Vector2 to, Vector2 size, Collider2D[] mine)
        {
            if (mine == null || mine.Length == 0) return false;
            AreaShape shape = new AreaShape();
            shape.kind = AreaShapeKind.Box;
            shape.victim = AreaVictimTest.HitboxCollider;
            shape.size = new Vector2(Mathf.Abs(size.x), Mathf.Abs(size.y));
            shape.angle = 0f;
            shape.useTriggers = true;
            shape.useLayerMask = true;
            shape.layerMask = CombatManager.Topdown1FLayerMask;

            float distance = (to - from).magnitude;
            float stride = Mathf.Max(0.1f, Mathf.Min(shape.size.x, shape.size.y) * 0.35f);
            int steps = Mathf.Clamp(Mathf.CeilToInt(distance / stride), 1, 64);
            for (int i = 0; i <= steps; i++)
            {
                shape.center = Vector2.Lerp(from, to, (float)i / steps);
                if (AreaGeom.HitboxOverlaps(shape, mine)) return true;
            }
            return false;
        }

        private static bool ConsiderBulletHit(Bullet b, BulletTrack track, PlayerAvatar me, bool own, Transform t, Vector3 bulletPos, Vector2 moveDir)
        {
            Vector2 dir = CsdUtil.VanillaHitDirection(b, bulletPos, t.position, moveDir.normalized, true);
            return ReportBulletHit(b, track, me, own, t, CsdUtil.BulletHitKind_Overlap, dir, bulletPos);
        }

        /// <summary>
        /// Shared tail of every client-side bullet hit: victim filter (own bullet vs. anything the
        /// host would let it hurt, hostile bullet vs. us only), per-bullet dedupe, snapshot when we
        /// are the victim, report. Returns true when a report went out.
        /// </summary>
        private static bool ReportBulletHit(Bullet b, BulletTrack track, PlayerAvatar me, bool own, Transform t, byte kind, Vector2 dir, Vector2 bulletPos)
        {
            CombatBehaviour cb = CsdUtil.CombatBehaviourFromCollider(t);
            if (cb == null) return false;
            bool victimIsMe = cb == me;
            if (own)
            {
                if (victimIsMe && !b.canAttackOwner) return false;
                // vanilla rejects these before any bookkeeping; without the host's faction mask
                // (see Classify) the faction part is left to the host
                if (!CsdUtil.BulletCanHurt(b, cb, track.targetFactionLayers, track.hasTargetFactions)) return false;
            }
            else if (!victimIsMe)
            {
                return false;
            }
            int existing = track.IndexOf(cb);
            if (existing >= 0)
            {
                Attacked prev = track.attacked[existing];
                if (prev.retryAt <= 0f || Time.time < prev.retryAt) return false;   // reported and pending / applied, or in the retry cooldown
                prev.retryAt = 0f;
                prev.timer = b.collisionStayDamageIntervalTimer.time;
                track.attacked[existing] = prev;
            }
            else
            {
                track.attacked.Add(new Attacked { cb = cb, timer = b.collisionStayDamageIntervalTimer.time });
            }
            track.reported++;

            uint bulletNetId = b.netId;
            uint victimNetId = cb.netId;
            byte victimComponent = (byte)cb.ComponentIndex;
            // where we see the bullet at contact (including an interpolated point from a sweep):
            // the host rewinds its copy there before applying
            CombatSnapshot snap = victimIsMe ? Capture(me) : default(CombatSnapshot);
            CsdRpc.SendToServer(me, CsdRpc.BulletHit, w =>
            {
                w.WriteUInt(bulletNetId);
                w.WriteUInt(victimNetId);
                w.WriteByte(victimComponent);
                w.WriteByte(kind);
                w.WriteVector2(dir);
                w.WriteVector2(bulletPos);
                w.WriteBool(victimIsMe);
                if (victimIsMe) snap.Write(w);
            });
            if (Plugin.DebugOn) Plugin.Debug("[CSD/client] " + (kind == CsdUtil.BulletHitKind_Ray ? "hitscan " : "bullet ") + b.name + " -> " + cb.name + (victimIsMe ? " " + snap : ""));
            return true;
        }

        // ------------------------------------------------------------------ hitscan (RaycastArrow)

        private static readonly RaycastHit2D[] _rayHits = new RaycastHit2D[30];

        /// <summary>
        /// The host resolves a RaycastArrow in SetMotionData, before the arrow is even spawned;
        /// the only thing the client ever learns is the angle (SyncVar) and the tile-clipped
        /// length (RpcSetSprite). That RPC is therefore the moment we run the same ray ourselves.
        /// </summary>
        public static void OnRaycastArrowSprite(BulletMoveModule_RaycastArrow arrow, float distance)
        {
            if (arrow == null || !Active || !Has(CsdFeatures.BulletHits) || !Plugin.ClientBulletHitDetection.Value) return;
            Bullet b = arrow.Bullet;
            if (b == null || !b.isClient || b.isServer) return;
            PlayerAvatar me = LocalAvatar;
            if (me == null) return;

            BulletTrack track = GetTrack(b);
            if (track.relevance == BulletRelevance.Unknown) track.relevance = Classify(b, track, me);
            if (track.relevance == BulletRelevance.Unknown)
            {
                // Vanilla sends RpcSetSprite from OnSpawnFinalized, i.e. before a host that syncs
                // the spawn state from a postfix gets to send it: keep the ray until the state is in
                // (OnBulletCollisionSync) or the wait runs out (TickPendingRays).
                if (!track.hasPendingRay) { track.hasPendingRay = true; _pendingRays++; }
                track.pendingRayDistance = distance;
                return;
            }
            if (track.relevance == BulletRelevance.Ignore) return;
            bool own = track.relevance == BulletRelevance.Own;
            if (distance <= 0f) distance = 15f;

            Vector2 origin = arrow.transform.position;
            Vector2 dir = HorayUtility.GetVector2FromAngle(arrow.rayAngle);
            int n = Physics2D.RaycastNonAlloc(origin, dir, _rayHits, distance, CombatManager.Topdown1FLayerMask);
            if (n <= 0) return;

            int reported = 0;
            for (int i = 0; i < n && reported < 10; i++)
            {
                Transform t = _rayHits[i].transform;
                if (t == null) continue;
                if (ReportBulletHit(b, track, me, own, t, CsdUtil.BulletHitKind_Ray, dir, origin)) reported++;
            }
        }

        // ------------------------------------------------------------------ DaggerGrowthBullet

        /// <summary>
        /// Growth-parry daggers are ordinary MonoBehaviours, not Mirror-spawned Bullets. Vanilla's
        /// reliable RpcCreateBullet creates one independent copy on every peer. The host sends a
        /// small CSD spawn record immediately before that RPC so the copies can be correlated by a
        /// mod-local id; either message may still be observed first, so matching works both ways.
        /// </summary>
        private sealed class DaggerTrack
        {
            public uint id;
            public uint ownerNetId;
            public DaggerGrowthBullet projectile;
            public UnitAvatar owner;
            public Vector2 spawnPosition;
            public Vector2 direction;
            public float damage;
            public float createdAt;
            public BulletRelevance relevance;
            public float traveled;
            public float currentSpeed;
            public bool decelerating;
            public bool collisionFinished;
            public readonly HashSet<CombatBehaviour> attacked = new HashSet<CombatBehaviour>();
        }

        private static readonly List<DaggerTrack> _daggers = new List<DaggerTrack>();
        private static readonly Collider2D[] _daggerHits = new Collider2D[20];
        private const float DaggerMatchLifetime = 3f;

        public static void OnDaggerSpawn(UnitAvatar avatar, uint id, uint ownerNetId, Vector2 position, Vector2 direction, float damage)
        {
            if (id == 0 || avatar == null || !avatar.isOwned || !Active || !Has(CsdFeatures.BulletHits)) return;
            for (int i = 0; i < _daggers.Count; i++) if (_daggers[i].id == id) return;

            DaggerTrack match = null;
            for (int i = 0; i < _daggers.Count; i++)
            {
                DaggerTrack t = _daggers[i];
                if (t.id == 0 && t.projectile != null && DaggerMatches(t, ownerNetId, position, direction, damage))
                {
                    match = t;
                    break;
                }
            }
            if (match == null)
            {
                match = new DaggerTrack { createdAt = Time.time };
                _daggers.Add(match);
            }
            match.id = id;
            match.ownerNetId = ownerNetId;
            match.spawnPosition = position;
            match.direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.right;
            match.damage = damage;
            if (Plugin.DebugOn) Plugin.Debug("[CSD/client] dagger spawn id " + id + " owner " + ownerNetId + (match.projectile != null ? " matched" : " queued"));
        }

        public static void OnDaggerInitialized(DaggerGrowthBullet projectile, UnitAvatar owner, Vector2 position, Vector2 direction, float damage, bool isServerObject)
        {
            if (projectile == null || isServerObject || !NetworkClient.active || NetworkServer.active) return;
            uint ownerNetId = owner != null ? owner.netId : 0u;
            DaggerTrack match = null;
            for (int i = 0; i < _daggers.Count; i++)
            {
                DaggerTrack t = _daggers[i];
                if (t.id != 0 && t.projectile == null && DaggerMatches(t, ownerNetId, position, direction, damage))
                {
                    match = t;
                    break;
                }
            }
            if (match == null)
            {
                match = new DaggerTrack
                {
                    ownerNetId = ownerNetId,
                    spawnPosition = position,
                    direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.right,
                    damage = damage,
                    createdAt = Time.time,
                };
                _daggers.Add(match);
            }
            match.projectile = projectile;
            match.owner = owner;
            match.currentSpeed = projectile.moveSpeed;
            if (Plugin.DebugOn) Plugin.Debug("[CSD/client] dagger visual " + projectile.name + (match.id != 0 ? " matched to id " + match.id : " awaiting spawn id"));
        }

        private static bool DaggerMatches(DaggerTrack t, uint ownerNetId, Vector2 position, Vector2 direction, float damage)
        {
            if (t.ownerNetId != ownerNetId) return false;
            if ((t.spawnPosition - position).sqrMagnitude > 0.01f) return false;
            Vector2 d = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.right;
            if (Vector2.Dot(t.direction, d) < 0.999f) return false;
            return Mathf.Abs(t.damage - damage) <= Mathf.Max(0.01f, Mathf.Abs(damage) * 0.001f);
        }

        private static void CleanupDaggerTracks()
        {
            float staleBefore = Time.time - DaggerMatchLifetime;
            for (int i = _daggers.Count - 1; i >= 0; i--)
            {
                DaggerTrack t = _daggers[i];
                if (t.projectile == null && t.createdAt < staleBefore) _daggers.RemoveAt(i);
            }
        }

        public static void OnDaggerGone(DaggerGrowthBullet projectile)
        {
            if (projectile == null) return;
            for (int i = _daggers.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_daggers[i].projectile, projectile)) _daggers.RemoveAt(i);
        }

        public static void OnDaggerUpdate(DaggerGrowthBullet projectile)
        {
            if (projectile == null) return;
            DaggerTrack track = null;
            for (int i = 0; i < _daggers.Count; i++)
            {
                if (ReferenceEquals(_daggers[i].projectile, projectile)) { track = _daggers[i]; break; }
            }
            if (track == null) return;
            try
            {
                if (track.collisionFinished || !Active || !Has(CsdFeatures.BulletHits) || !Plugin.ClientBulletHitDetection.Value || track.id == 0 || track.owner == null) return;

                PlayerAvatar me = LocalAvatar;
                if (me == null) return;
                if (track.relevance == BulletRelevance.Unknown)
                {
                    if (track.owner == me) track.relevance = BulletRelevance.Own;
                    else if (!CombatManager.ContainsAttackableFaction(track.owner.GetHostileFactionLayers(EDamageFromType.DirectAttack), me.faction)) track.relevance = BulletRelevance.Ignore;
                    else track.relevance = BulletRelevance.Hostile;
                }
                if (track.relevance == BulletRelevance.Ignore) return;
                bool own = track.relevance == BulletRelevance.Own;

                Vector2 position = projectile.transform.position;
                int n = HorayPhysics2D.OverlapCircle(position, projectile.hitRadius, _daggerHits, CombatManager.Topdown1FLayerMask);
                for (int i = 0; i < n; i++)
                {
                    Collider2D c = _daggerHits[i];
                    if (c == null || !c.TryGetComponent<Hitbox>(out Hitbox hitbox)) continue;
                    CombatBehaviour cb = hitbox.GetCombatBehaviour(0);
                    UnitAvatar victim = cb as UnitAvatar;
                    if (victim == null || victim.IsDead || victim == track.owner) continue;
                    if (!CombatManager.ContainsAttackableFaction(track.owner.GetHostileFactionLayers(EDamageFromType.DirectAttack), victim.faction)) continue;
                    bool victimIsMe = victim == me;
                    if (!own && !victimIsMe) continue;
                    if (!track.attacked.Add(cb)) continue;   // vanilla DaggerGrowthBullet.hitTargets is permanent, even on Fail_Absolute

                    uint victimNetId = cb.netId;
                    byte victimComponent = (byte)cb.ComponentIndex;
                    Vector2 hitPoint = c.ClosestPoint(position);
                    CombatSnapshot snap = victimIsMe ? Capture(me) : default(CombatSnapshot);
                    CsdRpc.SendToServer(me, CsdRpc.DaggerHit, w =>
                    {
                        w.WriteUInt(track.id);
                        w.WriteUInt(victimNetId);
                        w.WriteByte(victimComponent);
                        w.WriteVector2(position);
                        w.WriteVector2(hitPoint);
                        w.WriteBool(victimIsMe);
                        if (victimIsMe) snap.Write(w);
                    });
                    if (Plugin.DebugOn) Plugin.Debug("[CSD/client] growth dagger " + track.id + " -> " + cb.name + (victimIsMe ? " " + snap : ""));
                }
            }
            finally
            {
                // Mirror vanilla's private Fly -> Decelerate phase closely enough to stop testing
                // on the same zero-speed frame; WaitDespawn / Despawn are visual-only.
                float dt = Time.deltaTime;
                if (!track.decelerating)
                {
                    track.traveled += track.currentSpeed * dt;
                    if (track.traveled >= projectile.travelDistance) track.decelerating = true;
                }
                else
                {
                    track.currentSpeed = Mathf.MoveTowards(track.currentSpeed, 0f, projectile.deceleration * dt);
                    if (track.currentSpeed <= 0f) track.collisionFinished = true;
                }
            }
        }

        // ------------------------------------------------------------------ melee hit detection

        private class MeleeTrack
        {
            public UnitAvatar owner;
            public bool ownSwing;          // our swing (attach to our local position) vs. an enemy swing against us (use synced transform)
            public bool anchorToOwner;     // hostile attached swing without a NetworkTransform: follow the owner we see (see OnMeleeSpawned)
            public Vector3 offset;         // vanilla's offsetFromAvatarPosition, as computed by the host
            public float attachedDirection;
            public float multiHitTimer;
            public int hitCount = 1;
            public readonly List<CombatBehaviour> attacked = new List<CombatBehaviour>();
            public int frames;             // update frames seen (debug summary on the first ones)
            public int found;              // colliders the shape test returned over the swing's life (debug)
            public float age;              // seconds since we first saw the swing; testing stops at the swing's nominal duration
            public bool expired;
        }

        private static readonly Dictionary<MeleeCollision, MeleeTrack> _melees = new Dictionary<MeleeCollision, MeleeTrack>();
        private static readonly Collider2D[] _meleeHits = new Collider2D[30];

        public static void OnMeleeGone(MeleeCollision m)
        {
            if (m != null) _melees.Remove(m);
        }

        /// <summary>
        /// The host told us the swing parameters (CSD::MeleeSpawn, sent right after the spawn
        /// message). Our own swings are attached to our local position with the host's own
        /// owner-relative offset; hostile swings keep the transform the host streams (that is
        /// where the swing is drawn) and are tested against us.
        /// </summary>
        public static void OnMeleeSpawned(MeleeCollision m, UnitAvatar owner, Vector2 begin, Vector2 end, float height, float attachedDirection, float rangeBonus, Vector3 offset)
        {
            if (m == null) return;
            if (!Active || !Has(CsdFeatures.MeleeHits) || !Plugin.ClientMeleeHitDetection.Value)
            {
                if (Plugin.DebugOn) Plugin.Debug("[CSD/client] melee spawn " + m.name + " ignored: active=" + Active + " feature=" + Has(CsdFeatures.MeleeHits) + " cfg=" + Plugin.ClientMeleeHitDetection.Value);
                return;
            }
            if (!m.isClient || m.isServer) return;
            PlayerAvatar me = LocalAvatar;
            if (me == null) { if (Plugin.DebugOn) Plugin.Debug("[CSD/client] melee spawn " + m.name + " ignored: no local avatar"); return; }
            if (!CsdUtil.IsBaseMeleeUpdate(m)) { if (Plugin.DebugOn) Plugin.Debug("[CSD/client] melee spawn " + m.name + " ignored: " + m.GetType().Name + " runs its own update loop (host side)"); return; }

            bool own = owner != null && owner == me;
            if (!own && owner != null)
            {
                if (!CombatManager.ContainsAttackableFaction(owner.GetHostileFactionLayers(EDamageFromType.DirectAttack), me.faction))
                {
                    if (Plugin.DebugOn) Plugin.Debug("[CSD/client] melee spawn " + m.name + " ignored: owner " + owner.name + " (netId " + owner.netId + ") is neither us nor hostile");
                    return;
                }
            }
            if (Plugin.DebugOn) Plugin.Debug("[CSD/client] melee spawn " + m.name + " (" + m.GetType().Name + ", netId " + m.netId + ") owner=" + (owner != null ? owner.name + " netId " + owner.netId : "null")
                + " own=" + own + " attach=" + m.attachOwnerPosition + " begin=" + begin + " end=" + end + " offset=" + offset + " dir=" + attachedDirection + " range=" + rangeBonus);

            MeleeTrack track;
            if (!_melees.TryGetValue(m, out track))
            {
                track = new MeleeTrack();
                _melees[m] = track;
            }
            track.owner = owner;
            track.ownSwing = own;
            track.attachedDirection = attachedDirection;
            // begin was computed on the host from the host's copy of our position; mixing it with
            // our local (fresher) position would place the swing a latency-step behind us. The host
            // sends its own consistent offset instead.
            track.offset = offset;

            // the shape test reads these fields; on the client they were never initialised
            m.owner = owner;
            m.rangeBonus = rangeBonus;
            m.motionDataBegin = begin;
            m.motionDataEnd = end;
            m.height = height;
            // A hostile swing normally keeps the transform the host streams. A few enemy swing
            // prefabs (the Library DemonBook's dash / pacman-move / stamp volumes) carry no
            // NetworkTransform at all: vanilla moves them only in a server-side LateUpdate, so on
            // a client they stay at the spawn point for their whole life while the book flies on.
            // Those follow the owner we see, with the host's own attach offset - like our own swings.
            track.anchorToOwner = !own && owner != null && m.attachOwnerPosition && m.GetComponent<NetworkTransformBase>() == null;
            if (own || track.anchorToOwner)
            {
                m.transform.eulerAngles = new Vector3(0f, 0f, HorayUtility.GetAngle(end, begin));
                if (m.attachOwnerPosition) m.transform.position = owner.transform.position + AttachOffset(track, owner);
                else m.transform.position = begin;
                if (track.anchorToOwner && Plugin.DebugOn) Plugin.Debug("[CSD/client] melee " + m.name + " has no NetworkTransform: anchored to " + owner.name);
            }
        }

        private static Vector3 AttachOffset(MeleeTrack track, UnitAvatar owner)
        {
            return new Vector3(track.offset.x * track.attachedDirection * owner.transform.localScale.x, track.offset.y);
        }

        public static void OnMeleeUpdate(MeleeCollision m)
        {
            if (m == null || _melees.Count == 0) return;
            MeleeTrack track;
            if (!_melees.TryGetValue(m, out track)) return;
            if (!Active || !m.isClient || m.isServer) { _melees.Remove(m); return; }
            PlayerAvatar me = LocalAvatar;
            if (me == null) { _melees.Remove(m); return; }
            UnitAvatar owner = track.owner;
            if ((track.ownSwing || track.anchorToOwner) && owner == null) { _melees.Remove(m); return; }

            // The host keeps swings alive past their duration so that our (round-trip late)
            // reports still find them - our own swings and swings that can hit us; the hit window
            // itself must stay vanilla's, so we test only for the swing's nominal duration (the
            // same durationTimer vanilla runs on the host, counted from the moment we saw the swing).
            if (track.expired) return;
            float life = m.durationTimer != null ? m.durationTimer.time : 0.33f;
            if (track.age >= life)
            {
                track.expired = true;
                if (Plugin.DebugOn) Plugin.Debug("[CSD/client] melee " + m.name + (track.ownSwing ? " (ours)" : " (hostile)") + " done after " + track.frames + " frames / " + track.age.ToString("0.000") + "s (duration " + life.ToString("0.000") + "s), found " + track.found + " collider(s) in total");
                return;
            }
            track.age += Time.deltaTime;

            if ((track.ownSwing || track.anchorToOwner) && m.attachOwnerPosition)
            {
                m.transform.position = owner.transform.position + AttachOffset(track, owner);
            }
            if (track.hitCount < m.multiHit && m.multiHitIntervalTimer != null)
            {
                track.multiHitTimer += Time.deltaTime;
                if (track.multiHitTimer >= m.multiHitIntervalTimer.time)
                {
                    track.multiHitTimer = 0f;
                    track.attacked.Clear();
                    track.hitCount++;
                }
            }

            int n = R.MeleeCheckCollision(m, _meleeHits);
            track.frames++;
            track.found += n;
            // debug: what the shape test sees on the first frames of a swing (and later on, only when it finds something)
            bool verbose = Plugin.DebugOn && (track.frames <= 3 || n > 0) && track.frames <= 30;
            System.Text.StringBuilder sb = verbose ? new System.Text.StringBuilder() : null;
            if (sb != null)
                sb.Append("[CSD/client] melee ").Append(m.name).Append(track.ownSwing ? " (ours)" : " (hostile)").Append(" frame ").Append(track.frames)
                  .Append(" pos=").Append((Vector2)m.transform.position).Append(" ang=").Append(m.transform.eulerAngles.z.ToString("0")).Append(" found=").Append(n);
            for (int i = 0; i < n; i++)
            {
                Collider2D c = _meleeHits[i];
                if (c == null) continue;
                string why = null;
                CombatBehaviour cb = null;
                bool victimIsMe = false;
                if (owner != null && owner.transform == c.transform) why = "owner transform";
                else
                {
                    cb = CsdUtil.CombatBehaviourFromCollider(c);
                    if (cb == null) why = "no combat behaviour";
                    else if (owner != null && cb == owner) why = "owner";
                    else
                    {
                        victimIsMe = cb == me;
                        if (track.ownSwing && victimIsMe) why = "us";
                        else if (!track.ownSwing && !victimIsMe) why = "not us";
                        else if (track.attacked.Contains(cb)) why = "already reported";
                    }
                }
                if (sb != null) sb.Append(" | ").Append(c.name).Append('/').Append(c.transform.root.name).Append(cb != null ? " cb=" + cb.name : "").Append(why != null ? " -> " + why : " -> REPORT");
                if (why != null) continue;
                track.attacked.Add(cb);

                Vector2 hitPoint = c.ClosestPoint(m.transform.position);
                uint meleeNetId = m.netId;
                uint victimNetId = cb.netId;
                byte victimComponent = (byte)cb.ComponentIndex;
                CombatSnapshot snap = victimIsMe ? Capture(me) : default(CombatSnapshot);
                CsdRpc.SendToServer(me, CsdRpc.MeleeHit, w =>
                {
                    w.WriteUInt(meleeNetId);
                    w.WriteUInt(victimNetId);
                    w.WriteByte(victimComponent);
                    w.WriteVector2(hitPoint);
                    w.WriteBool(victimIsMe);
                    if (victimIsMe) snap.Write(w);
                });
                if (Plugin.DebugOn) Plugin.Debug("[CSD/client] melee " + m.name + " -> " + cb.name + " (netId " + victimNetId + "/" + victimComponent + ")" + (victimIsMe ? " " + snap : ""));
            }
            if (sb != null) Plugin.Debug(sb.ToString());
        }
    }
}

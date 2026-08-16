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
        }
        private static readonly List<PendingQuery> _queries = new List<PendingQuery>();

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
            _melees.Clear();
            _queries.Clear();
            _myHitboxes = null;
            _myHitboxOwner = null;
        }

        public static void Tick()
        {
            if (!NetworkClient.active && (_hostModded || _bullets.Count > 0 || _melees.Count > 0 || _queries.Count > 0)) Reset();
            SelfLineTick();
            if (!Plugin.Ready) return;
            if (_queries.Count > 0) AnswerQueries();
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
            if (protocol != Plugin.PROTOCOL_VERSION)
            {
                Plugin.Log.LogWarning("[CSD/client] host runs protocol " + protocol + " but we run " + Plugin.PROTOCOL_VERSION + " - mod stays off for this session.");
                _hostSilenceAnnounced = true;
                SelfLine("mod version mismatch (host protocol " + protocol + ", yours " + Plugin.PROTOCOL_VERSION + ") - update both");
                return;
            }
            if (avatar == null || !avatar.isOwned) return;
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
            _queries.Add(new PendingQuery { avatar = pa, requestId = requestId, shape = shape });
        }

        private static void AnswerQueries()
        {
            for (int i = 0; i < _queries.Count; i++)
            {
                PendingQuery q = _queries[i];
                try { Answer(q); }
                catch (Exception e) { Plugin.Log.LogError("[CSD/client] damage query " + q.requestId + " failed: " + e); }
            }
            _queries.Clear();
        }

        private static void Answer(PendingQuery q)
        {
            PlayerAvatar pa = q.avatar;
            if (pa == null) return;
            bool hit = true;
            string detail = "";
            if (q.shape != null && Plugin.ClientAreaHitVerification.Value)
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
        }

        private class BulletTrack
        {
            public BulletRelevance relevance;
            public int phase;
            public float phaseTimer;
            public int reported;           // reports the host has (optimistically) counted towards pierce
            public bool hasLast;
            public Vector3 lastPos;
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

        public static void OnBulletGone(Bullet b)
        {
            if (b != null) _bullets.Remove(b);
        }

        /// <summary>Host pushed the authoritative Bullet.isCollisionEnabled value.</summary>
        public static void OnBulletCollisionSync(Bullet b, bool enabled)
        {
            if (b == null) return;
            b.isCollisionEnabled = enabled;
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
                int i = track.IndexOf(cb);
                if (i >= 0) track.attacked.RemoveAt(i);
            }
            if (track.reported > 0) track.reported--;
            if (track.phase == 2 && !(b.pierceCreatureCount > 0 && b.pierceCreatureCount <= track.reported)) track.phase = 0;
        }

        private static BulletRelevance Classify(Bullet b, PlayerAvatar me)
        {
            if (CsdUtil.IsBulletExcluded(b)) return BulletRelevance.Ignore;
            UnitAvatar owner = b.NetworkOwner;
            if (owner != null && owner == me) return BulletRelevance.Own;
            // somebody else's bullet: only interesting if it can hurt us at all
            if (owner != null && !CombatManager.ContainsAttackableFaction(owner.GetHostileFactionLayers(EDamageFromType.None), me.faction)) return BulletRelevance.Ignore;
            return BulletRelevance.Hostile;
        }

        public static void OnBulletUpdate(Bullet b)
        {
            if (b == null || !Active || !Has(CsdFeatures.BulletHits) || !Plugin.ClientBulletHitDetection.Value) return;
            if (!b.isClient || b.isServer) return;
            // cheapest gates first: most bullets on screen cannot collide right now
            if (b.collosionType == Bullet.ECollisionTiming.None) return;

            BulletTrack track;
            if (!_bullets.TryGetValue(b, out track))
            {
                track = new BulletTrack();
                _bullets[b] = track;
            }
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
            Vector2 moveDir = track.hasLast ? (Vector2)(pos - track.lastPos) : Vector2.zero;
            track.lastPos = pos;
            track.hasLast = true;
            if (!b.isCollisionEnabled || track.phase != 0 || b.attackingCollider == null) return;

            PlayerAvatar me = LocalAvatar;
            if (me == null) return;
            if (track.relevance == BulletRelevance.Unknown) track.relevance = Classify(b, me);
            if (track.relevance == BulletRelevance.Ignore) return;
            bool own = track.relevance == BulletRelevance.Own;

            bool anyHit = false;
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
                Bounds bb = b.attackingCollider.bounds;
                ContactFilter2D f = R.BulletContactFilter(b);
                for (int i = 0; i < mine.Length; i++)
                {
                    Collider2D hb = mine[i];
                    if (hb == null || !hb.enabled || !bb.Intersects(hb.bounds)) continue;
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

        private static bool ConsiderBulletHit(Bullet b, BulletTrack track, PlayerAvatar me, bool own, Transform t, Vector3 bulletPos, Vector2 moveDir)
        {
            Vector2 dir;
            if (b.MoveModule == null)
            {
                dir = t.position - bulletPos;
            }
            else
            {
                switch (b.MoveModule.ShapeOfAttack)
                {
                    case EShapeOfAttack.Point: dir = t.position - bulletPos; break;
                    case EShapeOfAttack.Directional: dir = moveDir.sqrMagnitude > 0.000001f ? moveDir.normalized : (Vector2)(t.position - bulletPos); break;
                    default: dir = Vector2.zero; break;
                }
            }
            return ReportBulletHit(b, track, me, own, t, CsdUtil.BulletHitKind_Overlap, dir);
        }

        /// <summary>
        /// Shared tail of every client-side bullet hit: victim filter (own bullet vs. anything the
        /// host would let it hurt, hostile bullet vs. us only), per-bullet dedupe, snapshot when we
        /// are the victim, report. Returns true when a report went out.
        /// </summary>
        private static bool ReportBulletHit(Bullet b, BulletTrack track, PlayerAvatar me, bool own, Transform t, byte kind, Vector2 dir)
        {
            CombatBehaviour cb = CsdUtil.CombatBehaviourFromCollider(t);
            if (cb == null) return false;
            bool victimIsMe = cb == me;
            if (own)
            {
                if (victimIsMe && !b.canAttackOwner) return false;
                if (!CsdUtil.BulletCanHurt(b, cb)) return false;   // vanilla rejects these before any bookkeeping
            }
            else if (!victimIsMe)
            {
                return false;
            }
            if (track.IndexOf(cb) >= 0) return false;
            track.attacked.Add(new Attacked { cb = cb, timer = b.collisionStayDamageIntervalTimer.time });
            track.reported++;

            uint bulletNetId = b.netId;
            uint victimNetId = cb.netId;
            byte victimComponent = (byte)cb.ComponentIndex;
            CombatSnapshot snap = victimIsMe ? Capture(me) : default(CombatSnapshot);
            CsdRpc.SendToServer(me, CsdRpc.BulletHit, w =>
            {
                w.WriteUInt(bulletNetId);
                w.WriteUInt(victimNetId);
                w.WriteByte(victimComponent);
                w.WriteByte(kind);
                w.WriteVector2(dir);
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

            BulletTrack track;
            if (!_bullets.TryGetValue(b, out track))
            {
                track = new BulletTrack();
                _bullets[b] = track;
            }
            if (track.relevance == BulletRelevance.Unknown) track.relevance = Classify(b, me);
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
                if (ReportBulletHit(b, track, me, own, t, CsdUtil.BulletHitKind_Ray, dir)) reported++;
            }
        }

        // ------------------------------------------------------------------ melee hit detection

        private class MeleeTrack
        {
            public UnitAvatar owner;
            public bool ownSwing;          // our swing (attach to our local position) vs. an enemy swing against us (use synced transform)
            public Vector3 offset;         // vanilla's offsetFromAvatarPosition, as computed by the host
            public float attachedDirection;
            public float multiHitTimer;
            public int hitCount = 1;
            public readonly List<CombatBehaviour> attacked = new List<CombatBehaviour>();
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
            if (m == null || !Active || !Has(CsdFeatures.MeleeHits) || !Plugin.ClientMeleeHitDetection.Value) return;
            if (!m.isClient || m.isServer) return;
            PlayerAvatar me = LocalAvatar;
            if (me == null) return;
            if (!CsdUtil.IsBaseMeleeUpdate(m)) return;

            bool own = owner != null && owner == me;
            if (!own && owner != null)
            {
                if (!CombatManager.ContainsAttackableFaction(owner.GetHostileFactionLayers(EDamageFromType.DirectAttack), me.faction)) return;
            }

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
            if (own)
            {
                m.transform.eulerAngles = new Vector3(0f, 0f, HorayUtility.GetAngle(end, begin));
                if (m.attachOwnerPosition) m.transform.position = owner.transform.position + AttachOffset(track, owner);
                else m.transform.position = begin;
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
            if (track.ownSwing && owner == null) { _melees.Remove(m); return; }

            if (track.ownSwing && m.attachOwnerPosition)
            {
                m.transform.position = owner.transform.position + AttachOffset(track, owner);
            }
            if (track.hitCount < m.multiHit)
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
            for (int i = 0; i < n; i++)
            {
                Collider2D c = _meleeHits[i];
                if (c == null) continue;
                if (owner != null && owner.transform == c.transform) continue;
                CombatBehaviour cb = CsdUtil.CombatBehaviourFromCollider(c);
                if (cb == null || (owner != null && cb == owner)) continue;
                bool victimIsMe = cb == me;
                if (track.ownSwing) { if (victimIsMe) continue; }
                else if (!victimIsMe) continue;
                if (track.attacked.Contains(cb)) continue;
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
                if (Plugin.DebugOn) Plugin.Debug("[CSD/client] melee " + m.name + " -> " + cb.name + (victimIsMe ? " " + snap : ""));
            }
        }
    }
}

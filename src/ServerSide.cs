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
            AreaRecorder.Clear();
        }

        /// <summary>Tells the area hit recorder which players' hits are client-verified.</summary>
        private static void RefreshAreaTargets()
        {
            _areaAvatars.Clear();
            foreach (ModdedClient mc in _clients.Values)
            {
                if (!mc.acked || mc.avatar == null || (mc.features & CsdFeatures.AreaHits) == 0) continue;
                _areaAvatars.Add(mc.avatar);
            }
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
                if (_clients.Count > 0 || _pending.Count > 0) Reset();
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

        /// <summary>The host's own one-liner, e.g. "v1.2.1 host ON: guard/dodge, bullets, melee, area, fresh-pos".</summary>
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
            for (int i = 0; i < b.attackedList.Count; i++) if (b.attackedList[i].combatBehaviour == pa) return false;
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
            Vector2 dir;
            if (b.MoveModule == null) dir = hit.position - b.transform.position;
            else switch (b.MoveModule.ShapeOfAttack)
            {
                case EShapeOfAttack.Point: dir = hit.position - b.transform.position; break;
                case EShapeOfAttack.Directional: dir = b.MoveModule.CurMovingDirection; break;
                default: dir = Vector2.zero; break;
            }

            PendingDamage p = NewPending(PendingKind.Bullet, pa, mc, useSnapshot, shape);
            p.bullet = b; p.hitT = hit; p.direction = dir; p.pairKey = key; p.projectileNetId = b.netId;
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
                    bool wasHit = RunProjectile(victim, force, snap, () => BulletAttack(bb, victim, ht, dir, out code));
                    ApplyBulletPhase(b, wasHit);
                    break;
                }
                case PendingKind.Melee:
                {
                    MeleeCollision m = p.melee;
                    if (m == null || !m.isServer || m.netId == 0 || m.netId != p.projectileNetId || p.hitT == null) return;
                    MeleeCollision mm = m; Transform ht = p.hitT; int type = p.meleeType; Vector3 hp = p.hitPoint;
                    RunProjectile(p.victim, force, snap, () => R.MeleeAttack(mm, type, hp, ht));
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
        private static bool RunProjectile(PlayerAvatar victim, bool force, CombatSnapshot snap, Func<bool> body)
        {
            bool prevProj = ProjectileBypass;
            ProjectileBypass = true;
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
            finally { ProjectileBypass = prevProj; }
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
            if (b == null || !NetworkServer.active || !Plugin.On || !Plugin.HostBulletHitAuthority.Value) return;
            if (!b.isServer || b.netId == 0) return;
            if (_clients.Count == 0) return;
            bool enabled = b.isCollisionEnabled;
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
            foreach (ModdedClient mc in _clients.Values)
            {
                if (!mc.acked || (mc.features & CsdFeatures.MeleeHits) == 0 || mc.conn == null || !mc.conn.isReady) continue;
                CsdRpc.SendToClient(m, mc.conn, CsdRpc.MeleeSpawn, payload);
            }
        }

        public static void OnBulletHit(UnitAvatar avatar, NetworkConnectionToClient sender, uint bulletNetId, uint victimNetId, byte victimComponent, byte kind, Vector2 direction, bool hasSnapshot, CombatSnapshot snap)
        {
            PlayerAvatar me = avatar as PlayerAvatar;
            ModdedClient mc = GetModdedClient(me);
            if (mc == null || mc.conn != sender || (mc.features & CsdFeatures.BulletHits) == 0) return;
            if (!Plugin.HostBulletHitAuthority.Value) return;

            byte code = CsdRpc.HitResult_NotApplied;
            try
            {
                Bullet b = CsdUtil.FindComponent<Bullet>(NetworkServer.spawned, bulletNetId);
                if (b == null || !b.isServer) return;
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

                bool force = victimIsMe && hasSnapshot && (mc.features & CsdFeatures.DamageTakenAuthority) != 0;
                byte c = CsdRpc.HitResult_NotApplied;
                bool hit = RunProjectile(me, force, snap, () => BulletAttack(b, victim, hitT, direction, out c));
                code = c;
                if (Plugin.DebugOn) Plugin.Debug("[CSD/host] bullet " + b.name + " -> " + victim.name + " reported by conn " + sender.connectionId + " hit=" + hit + " code=" + code);
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
            else if (b.MoveModule == null)
            {
                vector = hit.position - b.transform.position;
            }
            else
            {
                switch (b.MoveModule.ShapeOfAttack)
                {
                    case EShapeOfAttack.Point: vector = hit.position - b.transform.position; break;
                    case EShapeOfAttack.Directional: vector = b.MoveModule.CurMovingDirection; break;
                    default: vector = Vector2.zero; break;
                }
            }

            if (b.ignored.Contains(combatBehaviour)) return false;
            for (int i = 0; i < b.attackedList.Count; i++)
                if (b.attackedList[i].combatBehaviour == combatBehaviour) { hitCode = CsdRpc.HitResult_Attacked; return false; }

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
            if (mc == null || mc.conn != sender || (mc.features & CsdFeatures.MeleeHits) == 0) return;
            if (!Plugin.HostMeleeHitAuthority.Value) return;

            MeleeCollision m = CsdUtil.FindComponent<MeleeCollision>(NetworkServer.spawned, meleeNetId);
            if (m == null || !m.isServer || !CsdUtil.IsBaseMeleeUpdate(m)) return;
            CombatBehaviour victim = CsdUtil.FindBehaviour(NetworkServer.spawned, victimNetId, victimComponent) as CombatBehaviour;
            if (victim == null) return;
            bool ownSwing = m.owner != null && m.owner == me;
            bool victimIsMe = victim == me;
            if (!ownSwing && !victimIsMe) return;   // a client may only register hits that involve itself
            if (ownSwing && victimIsMe) return;
            Transform hitT = CsdUtil.FindHitboxTransform(victim);
            if (hitT == null) return;

            bool force = victimIsMe && hasSnapshot && (mc.features & CsdFeatures.DamageTakenAuthority) != 0;
            bool hit = RunProjectile(me, force, snap, () => R.MeleeAttack(m, 0, hitPoint, hitT));
            if (Plugin.DebugOn) Plugin.Debug("[CSD/host] melee " + m.name + " -> " + victim.name + " reported by conn " + sender.connectionId + " hit=" + hit);
        }
    }
}

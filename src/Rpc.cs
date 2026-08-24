using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using Mirror.RemoteCalls;
using UnityEngine;

namespace ClientSideDamage
{
    /// <summary>
    /// All mod traffic rides on Mirror's regular Command / TargetRpc machinery, registered at
    /// runtime on the UnitAvatar component (which every PlayerAvatar has). This is deliberately
    /// NOT a custom NetworkMessage: an unknown NetworkMessage id makes Mirror disconnect the peer,
    /// while an unknown RPC hash is only logged. So an un-modded host or client is never kicked,
    /// the mod simply stays dormant for that pair.
    /// </summary>
    public static class CsdRpc
    {
        // server -> client (TargetRpc on the player's own avatar)
        public const string Hello = "CSD::Hello";
        public const string Enable = "CSD::Enable";
        public const string DamageQuery = "CSD::DamageQuery";
        public const string BulletHitResult = "CSD::BulletHitResult";
        public const string BulletCollision = "CSD::BulletCollision";   // registered on Bullet, not UnitAvatar
        public const string MeleeSpawn = "CSD::MeleeSpawn";             // registered on MeleeCollision
        public const string DaggerSpawn = "CSD::DaggerSpawn";           // correlates the game's non-networked DaggerGrowthBullet copies
        // client -> server (Command on the player's own avatar)
        public const string HelloAck = "CSD::HelloAck";
        public const string DamageReply = "CSD::DamageReply";
        public const string BulletHit = "CSD::BulletHit";
        public const string MeleeHit = "CSD::MeleeHit";
        public const string DaggerHit = "CSD::DaggerHit";

        /// <summary>What the host did with a reported bullet hit (mirrors Bullet.AttackOnServer's bookkeeping).</summary>
        public const byte HitResult_NotApplied = 0;   // Fail_Absolute or never applied: vanilla would test this pair again
        public const byte HitResult_Attacked = 1;     // added to attackedList, does not count towards pierce (Pass / Dodge)
        public const byte HitResult_Counted = 2;      // added to attackedList and counts towards pierce (Success / Block)

        private struct Entry
        {
            public Type component;
            public string name;
            public RemoteCallDelegate func;
            public bool command;
            public Entry(Type c, string n, RemoteCallDelegate f, bool cmd) { component = c; name = n; func = f; command = cmd; }
        }

        private static Entry[] _table;
        private static readonly Dictionary<ushort, string> _ourHashes = new Dictionary<ushort, string>();
        private static bool _registered;

        public static bool Registered { get { return _registered; } }

        /// <summary>
        /// Registers the mod's RPCs. Mirror keeps only 16 bits of the name hash and silently
        /// overwrites the registry entry on a clash, so before registering we make sure none of
        /// our hashes is already taken (a game RPC registered before us), and a hook on Mirror's
        /// RegisterDelegate (below) catches game RPCs registered after us. Either case disables
        /// the mod instead of letting two handlers fight over one hash.
        /// </summary>
        public static void Register()
        {
            if (_registered) return;
            Type ua = typeof(UnitAvatar);
            _table = new Entry[]
            {
                new Entry(ua, Hello, OnHello, false),
                new Entry(ua, Enable, OnEnable, false),
                new Entry(ua, DamageQuery, OnDamageQuery, false),
                new Entry(ua, BulletHitResult, OnBulletHitResult, false),
                new Entry(typeof(Bullet), BulletCollision, OnBulletCollision, false),
                new Entry(typeof(MeleeCollision), MeleeSpawn, OnMeleeSpawn, false),
                new Entry(ua, DaggerSpawn, OnDaggerSpawn, false),
                new Entry(ua, HelloAck, OnHelloAck, true),
                new Entry(ua, DamageReply, OnDamageReply, true),
                new Entry(ua, BulletHit, OnBulletHit, true),
                new Entry(ua, MeleeHit, OnMeleeHit, true),
                new Entry(ua, DaggerHit, OnDaggerHit, true),
            };
            _ourHashes.Clear();
            for (int i = 0; i < _table.Length; i++)
            {
                ushort h = Hash16(_table[i].name);
                string other;
                if (_ourHashes.TryGetValue(h, out other))
                    throw new Exception("RPC hash collision between " + other + " and " + _table[i].name);
                if (R.RegisteredRpc(h) != null)
                    throw new Exception("RPC hash collision: " + _table[i].name + " (0x" + h.ToString("X4") + ") is already registered by the game");
                _ourHashes[h] = _table[i].name;
            }
            for (int i = 0; i < _table.Length; i++)
            {
                Entry e = _table[i];
                if (e.command) RemoteProcedureCalls.RegisterCommand(e.component, e.name, e.func, true);
                else RemoteProcedureCalls.RegisterRpc(e.component, e.name, e.func);
            }
            for (int i = 0; i < _table.Length; i++)
            {
                Delegate registered = R.RegisteredRpc(Hash16(_table[i].name));
                if (registered == null || !registered.Equals(_table[i].func))
                    throw new Exception("RPC registration of " + _table[i].name + " did not stick");
            }
            _registered = true;
        }

        /// <summary>Called from the RegisterDelegate hook: a game RPC is about to take one of our hashes.</summary>
        internal static void OnForeignRegistration(Type componentType, string functionFullName, RemoteCallDelegate func)
        {
            if (!_registered) return;
            ushort h = Hash16(functionFullName);
            string ours;
            if (!_ourHashes.TryGetValue(h, out ours)) return;
            // our own (re-)registration or an identical delegate is not a collision
            for (int i = 0; i < _table.Length; i++)
                if (_table[i].name == functionFullName && _table[i].func.Equals(func)) return;
            Plugin.Disable("RPC hash collision: game RPC " + (componentType != null ? componentType.Name : "?") + "." + functionFullName
                + " and " + ours + " share hash 0x" + h.ToString("X4") + " (mod traffic would be misrouted)", "RPC hash collision with the game");
        }

        private static ushort Hash16(string name) { return (ushort)((uint)name.GetStableHashCode() & 0xFFFFu); }
        private static int Hash(string name) { return name.GetStableHashCode(); }

        // ---------------------------------------------------------------- send helpers

        public static void SendToClient(NetworkBehaviour avatar, NetworkConnectionToClient conn, string name, Action<NetworkWriter> payload)
        {
            if (!Plugin.Ready || avatar == null || conn == null) return;
            NetworkWriterPooled w = NetworkWriterPool.Get();
            try
            {
                if (payload != null) payload(w);
                R.SendTargetRPCInternal(avatar, conn, name, Hash(name), w, Channels.Reliable);
            }
            finally { NetworkWriterPool.Return(w); }
        }

        public static void SendToServer(UnitAvatar avatar, string name, Action<NetworkWriter> payload)
        {
            if (!Plugin.Ready || avatar == null) return;
            NetworkWriterPooled w = NetworkWriterPool.Get();
            try
            {
                if (payload != null) payload(w);
                R.SendCommandInternal(avatar, name, Hash(name), w, Channels.Reliable, true);
            }
            finally { NetworkWriterPool.Return(w); }
        }

        // ---------------------------------------------------------------- receivers
        // Mirror disconnects a peer whose message handler throws, so nothing may escape from here.

        private static void Guard(string name, Action body)
        {
            try { body(); }
            catch (Exception e) { Plugin.Log.LogError("[CSD] " + name + " handler failed: " + e); }
        }

        // ---------------------------------------------------------------- client side receivers

        private static void OnHello(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnHello", () =>
            {
                int protocol = reader.ReadInt();
                byte hostFeatures = reader.ReadByte();
                if (!Plugin.Ready || !NetworkClient.active) return;
                ClientSide.OnHello(obj as UnitAvatar, protocol, (CsdFeatures)hostFeatures);
            });
        }

        private static void OnEnable(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnEnable", () =>
            {
                byte features = reader.ReadByte();
                if (!Plugin.Ready || !NetworkClient.active) return;
                ClientSide.OnEnable(obj as UnitAvatar, (CsdFeatures)features);
            });
        }

        private static void OnBulletCollision(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnBulletCollision", () =>
            {
                bool enabled = reader.ReadBool();
                if (!Plugin.Ready || !NetworkClient.active || NetworkServer.active) return;
                ClientSide.OnBulletCollisionSync(obj as Bullet, enabled);
            });
        }

        private static void OnBulletHitResult(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnBulletHitResult", () =>
            {
                uint bulletNetId = reader.ReadUInt();
                uint victimNetId = reader.ReadUInt();
                byte victimComponent = reader.ReadByte();
                byte code = reader.ReadByte();
                if (!Plugin.Ready || !NetworkClient.active || NetworkServer.active) return;
                ClientSide.OnBulletHitResult(obj as UnitAvatar, bulletNetId, victimNetId, victimComponent, code);
            });
        }

        private static void OnMeleeSpawn(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnMeleeSpawn", () =>
            {
                uint ownerNetId = reader.ReadUInt();
                Vector2 begin = reader.ReadVector2();
                Vector2 end = reader.ReadVector2();
                float height = reader.ReadFloat();
                float attachedDirection = reader.ReadFloat();
                float rangeBonus = reader.ReadFloat();
                Vector3 offset = reader.ReadVector3();
                if (!Plugin.Ready || !NetworkClient.active || NetworkServer.active) return;
                UnitAvatar owner = ownerNetId == 0 ? null : CsdUtil.FindComponent<UnitAvatar>(NetworkClient.spawned, ownerNetId);
                ClientSide.OnMeleeSpawned(obj as MeleeCollision, owner, begin, end, height, attachedDirection, rangeBonus, offset);
            });
        }

        private static void OnDaggerSpawn(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnDaggerSpawn", () =>
            {
                uint daggerId = reader.ReadUInt();
                uint ownerNetId = reader.ReadUInt();
                Vector2 position = reader.ReadVector2();
                Vector2 direction = reader.ReadVector2();
                float damage = reader.ReadFloat();
                if (!Plugin.Ready || !NetworkClient.active || NetworkServer.active) return;
                ClientSide.OnDaggerSpawn(obj as UnitAvatar, daggerId, ownerNetId, position, direction, damage);
            });
        }

        private static void OnDamageQuery(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnDamageQuery", () =>
            {
                uint requestId = reader.ReadUInt();
                bool hasShape = reader.ReadBool();
                AreaShape shape = hasShape ? AreaShape.Read(reader) : null;
                if (!Plugin.Ready || !NetworkClient.active) return;
                ClientSide.OnDamageQuery(obj as UnitAvatar, requestId, shape);
            });
        }

        // ---------------------------------------------------------------- server side receivers

        private static void OnHelloAck(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnHelloAck", () =>
            {
                int protocol = reader.ReadInt();
                byte features = reader.ReadByte();
                if (!Plugin.Ready || !NetworkServer.active) return;
                ServerSide.OnHelloAck(obj as UnitAvatar, sender, protocol, (CsdFeatures)features);
            });
        }

        private static void OnDamageReply(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnDamageReply", () =>
            {
                uint requestId = reader.ReadUInt();
                bool hit = reader.ReadBool();
                CombatSnapshot snap = CombatSnapshot.Read(reader);
                if (!Plugin.Ready || !NetworkServer.active) return;
                ServerSide.OnDamageReply(obj as UnitAvatar, sender, requestId, hit, snap);
            });
        }

        private static void OnBulletHit(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnBulletHit", () =>
            {
                uint bulletNetId = reader.ReadUInt();
                uint victimNetId = reader.ReadUInt();
                byte victimComponent = reader.ReadByte();
                byte kind = reader.ReadByte();
                Vector2 direction = reader.ReadVector2();
                Vector2 bulletPos = reader.ReadVector2();
                bool hasSnapshot = reader.ReadBool();
                CombatSnapshot snap = default(CombatSnapshot);
                if (hasSnapshot) snap = CombatSnapshot.Read(reader);
                if (!Plugin.Ready || !NetworkServer.active) return;
                ServerSide.OnBulletHit(obj as UnitAvatar, sender, bulletNetId, victimNetId, victimComponent, kind, direction, bulletPos, hasSnapshot, snap);
            });
        }

        private static void OnMeleeHit(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnMeleeHit", () =>
            {
                uint meleeNetId = reader.ReadUInt();
                uint victimNetId = reader.ReadUInt();
                byte victimComponent = reader.ReadByte();
                Vector2 hitPoint = reader.ReadVector2();
                bool hasSnapshot = reader.ReadBool();
                CombatSnapshot snap = default(CombatSnapshot);
                if (hasSnapshot) snap = CombatSnapshot.Read(reader);
                if (!Plugin.Ready || !NetworkServer.active) return;
                ServerSide.OnMeleeHit(obj as UnitAvatar, sender, meleeNetId, victimNetId, victimComponent, hitPoint, hasSnapshot, snap);
            });
        }

        private static void OnDaggerHit(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient sender)
        {
            Guard("OnDaggerHit", () =>
            {
                uint daggerId = reader.ReadUInt();
                uint victimNetId = reader.ReadUInt();
                byte victimComponent = reader.ReadByte();
                Vector2 daggerPosition = reader.ReadVector2();
                Vector2 hitPoint = reader.ReadVector2();
                bool hasSnapshot = reader.ReadBool();
                CombatSnapshot snap = default(CombatSnapshot);
                if (hasSnapshot) snap = CombatSnapshot.Read(reader);
                if (!Plugin.Ready || !NetworkServer.active) return;
                ServerSide.OnDaggerHit(obj as UnitAvatar, sender, daggerId, victimNetId, victimComponent, daggerPosition, hitPoint, hasSnapshot, snap);
            });
        }
    }

    /// <summary>
    /// Mirror's weaver-generated static constructors register the game's RPCs lazily (when a
    /// NetworkBehaviour type is first touched), i.e. mostly after this plugin's Awake. Watch every
    /// registration so a game RPC that lands on one of our 16 bit hashes is noticed the moment it
    /// happens; the mod then goes dormant and the game's handler wins.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_RemoteProcedureCalls_RegisterDelegate
    {
        // the internal choke point plus the two public wrappers the weaver-generated code calls
        // (in case the runtime had already inlined the former into the latter before we patched)
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase m = AccessTools.Method(typeof(RemoteProcedureCalls), "RegisterDelegate");
            if (m == null) throw new MissingMethodException(typeof(RemoteProcedureCalls).FullName, "RegisterDelegate");
            yield return m;
            m = AccessTools.Method(typeof(RemoteProcedureCalls), "RegisterCommand");
            if (m != null) yield return m;
            m = AccessTools.Method(typeof(RemoteProcedureCalls), "RegisterRpc");
            if (m != null) yield return m;
        }

        private static void Prefix(Type componentType, string functionFullName, RemoteCallDelegate func)
        {
            try { CsdRpc.OnForeignRegistration(componentType, functionFullName, func); }
            catch (Exception e) { Plugin.Log.LogError("[CSD] RPC registration check failed: " + e); }
        }
    }
}

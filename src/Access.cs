using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace ClientSideDamage
{
    /// <summary>
    /// The game's Mono runtime enforces member visibility across assemblies, so every non-public
    /// member we touch goes through Harmony's DynamicMethod based accessors (which skip visibility
    /// checks) or plain reflection. Everything is resolved once, at plugin start; a missing member
    /// throws here so the plugin fails loudly instead of half-working.
    /// </summary>
    internal static class R
    {
        // ---- UnitAvatar
        public static readonly AccessTools.FieldRef<UnitAvatar, NestedBoolean> IsDodgeInvincibleApplied = F<UnitAvatar, NestedBoolean>("isDodgeInvincibleApplied");
        public static readonly AccessTools.FieldRef<UnitAvatar, bool> IsPerfectGuardAvailable = F<UnitAvatar, bool>("isPerfectGuardAvailable");
        public static readonly AccessTools.FieldRef<UnitAvatar, float> GuardStartTimer = F<UnitAvatar, float>("guardStartTimer");
        public static readonly AccessTools.FieldRef<UnitAvatar, float> PerfectGuardLatencyBonus = F<UnitAvatar, float>("perfectGuardLatencyBonus");
        public static readonly AccessTools.FieldRef<UnitAvatar, bool> IsAvailableGuard = F<UnitAvatar, bool>("isAvailableGuard");
        public static readonly AccessTools.FieldRef<UnitAvatar, bool> IsHitInvincibleEnabled = F<UnitAvatar, bool>("isHitInvincibleEnabled");

        // ---- PlayerAvatar
        public static readonly AccessTools.FieldRef<PlayerAvatar, WeaponControllerSimple> WeaponController = F<PlayerAvatar, WeaponControllerSimple>("weaponController");

        // ---- Bullet
        public static readonly AccessTools.FieldRef<Bullet, int> BulletCurrentPierceCount = F<Bullet, int>("currentPierceCreatureCount");
        public static readonly AccessTools.FieldRef<Bullet, int> BulletHitCount = F<Bullet, int>("hitCount");
        public static readonly AccessTools.FieldRef<Bullet, int> BulletCollisionPhase = F<Bullet, int>("collosionOperationPhase");
        public static readonly AccessTools.FieldRef<Bullet, ContactFilter2D> BulletContactFilter = F<Bullet, ContactFilter2D>("contactFilter");
        public static readonly AccessTools.FieldRef<Bullet, EDamageElementalType> BulletElementalType = F<Bullet, EDamageElementalType>("elementalType");
        public static readonly AccessTools.FieldRef<Bullet, Timer> BulletCollisionCheckTimer = F<Bullet, Timer>("collisionCheckTimer");

        // ---- Unit_ChakramThrower
        public static readonly AccessTools.FieldRef<Unit_ChakramThrower, List<GameObject>> ChakramList = F<Unit_ChakramThrower, List<GameObject>>("chakrams");

        // ---- MeleeCollision
        public static readonly AccessTools.FieldRef<MeleeCollision, float> MeleeAttachedDirection = F<MeleeCollision, float>("attachedDirection");
        public static readonly AccessTools.FieldRef<MeleeCollision, Vector3> MeleeOffsetFromAvatar = F<MeleeCollision, Vector3>("offsetFromAvatarPosition");
        public static readonly AccessTools.FieldRef<MeleeCollision, List<CombatBehaviour>> MeleeAttackedInSwing = F<MeleeCollision, List<CombatBehaviour>>("attackedInSwing");
        public static readonly AccessTools.FieldRef<MeleeCollision, List<CombatBehaviour>> MeleeSharedTarget = F<MeleeCollision, List<CombatBehaviour>>("sharedTarget");

        // ---- NestedBoolean (a nesting counter; we adjust it by a delta rather than replacing it)
        private static readonly FieldInfo _nestedCounter = AccessTools.Field(typeof(NestedBoolean), "counter");
        public static int NestedCounter(NestedBoolean nb)
        {
            if (_nestedCounter == null) throw new MissingFieldException(typeof(NestedBoolean).FullName, "counter");
            return (int)_nestedCounter.GetValue(nb);
        }

        // ---- MeleeCollision (protected virtual methods; MethodInfo.Invoke dispatches virtually)
        private static readonly MethodInfo _meleeAttack = M(typeof(MeleeCollision), "Attack");
        private static readonly MethodInfo _meleeCheckCollision = M(typeof(MeleeCollision), "CheckCollision");
        private static readonly object[] _args3 = new object[3];
        private static readonly object[] _args1 = new object[1];

        public static bool MeleeAttack(MeleeCollision m, int type, Vector3 hitPoint, Transform hitTransform)
        {
            _args3[0] = type; _args3[1] = hitPoint; _args3[2] = hitTransform;
            return (bool)_meleeAttack.Invoke(m, _args3);
        }

        public static int MeleeCheckCollision(MeleeCollision m, Collider2D[] hits)
        {
            _args1[0] = hits;
            return (int)_meleeCheckCollision.Invoke(m, _args1);
        }

        // ---- DamageInstance (private constructor)
        public static DamageInstance NewDamageInstance()
        {
            return (DamageInstance)Activator.CreateInstance(typeof(DamageInstance), true);
        }

        // ---- Mirror NetworkTransformBase.Apply (protected virtual)
        private static readonly MethodInfo _ntApply = M(typeof(NetworkTransformBase), "Apply");
        private static readonly object[] _args2 = new object[2];

        public static void NetworkTransformApply(NetworkTransformBase nt, TransformSnapshot interpolated, TransformSnapshot endGoal)
        {
            _args2[0] = interpolated; _args2[1] = endGoal;
            _ntApply.Invoke(nt, _args2);
        }

        // ---- Mirror NetworkBehaviour send helpers (protected)
        private static readonly MethodInfo _sendCommand = M(typeof(NetworkBehaviour), "SendCommandInternal");
        private static readonly MethodInfo _sendTargetRpc = M(typeof(NetworkBehaviour), "SendTargetRPCInternal");
        private static readonly object[] _args5a = new object[5];
        private static readonly object[] _args5b = new object[5];

        public static void SendCommandInternal(NetworkBehaviour nb, string functionFullName, int hash, NetworkWriter writer, int channel, bool requiresAuthority)
        {
            _args5a[0] = functionFullName; _args5a[1] = hash; _args5a[2] = writer; _args5a[3] = channel; _args5a[4] = requiresAuthority;
            _sendCommand.Invoke(nb, _args5a);
        }

        public static void SendTargetRPCInternal(NetworkBehaviour nb, NetworkConnection conn, string functionFullName, int hash, NetworkWriter writer, int channel)
        {
            _args5b[0] = conn; _args5b[1] = functionFullName; _args5b[2] = hash; _args5b[3] = writer; _args5b[4] = channel;
            _sendTargetRpc.Invoke(nb, _args5b);
        }

        // ---- Mirror RemoteProcedureCalls registry (private static dictionary of internal Invoker objects)
        private static readonly FieldInfo _remoteCallDelegates = AccessTools.Field(typeof(Mirror.RemoteCalls.RemoteProcedureCalls), "remoteCallDelegates");

        /// <summary>Returns the delegate registered for a 16 bit RPC hash, or null.</summary>
        public static Delegate RegisteredRpc(ushort hash)
        {
            if (_remoteCallDelegates == null) return null;
            IDictionary dict = _remoteCallDelegates.GetValue(null) as IDictionary;
            if (dict == null || !dict.Contains(hash)) return null;
            object invoker = dict[hash];
            if (invoker == null) return null;
            FieldInfo fn = AccessTools.Field(invoker.GetType(), "function");
            return fn == null ? null : fn.GetValue(invoker) as Delegate;
        }

        // ---- helpers
        private static AccessTools.FieldRef<TObj, TField> F<TObj, TField>(string name)
        {
            FieldInfo fi = AccessTools.Field(typeof(TObj), name);
            if (fi == null) throw new MissingFieldException(typeof(TObj).FullName, name);
            if (fi.FieldType != typeof(TField)) throw new Exception(typeof(TObj).FullName + "." + name + " is " + fi.FieldType + ", expected " + typeof(TField));
            return AccessTools.FieldRefAccess<TObj, TField>(fi);
        }

        private static MethodInfo M(Type t, string name)
        {
            MethodInfo mi = AccessTools.Method(t, name);
            if (mi == null) throw new MissingMethodException(t.FullName, name);
            return mi;
        }

        /// <summary>Touch every accessor so all lookups happen (and fail) at plugin start.</summary>
        public static void Init()
        {
            if (IsDodgeInvincibleApplied == null || WeaponController == null || BulletContactFilter == null || _meleeAttack == null || _sendCommand == null
                || _nestedCounter == null || _remoteCallDelegates == null)
                throw new Exception("accessor initialisation failed");
        }
    }
}

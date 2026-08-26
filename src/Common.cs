using System;
using System.Collections.Generic;
using System.Reflection;
using Mirror;
using UnityEngine;

namespace ClientSideDamage
{
    /// <summary>Which client-authoritative features are active for one host/client pair.</summary>
    [Flags]
    public enum CsdFeatures : byte
    {
        None = 0,
        DamageTakenAuthority = 1,   // host asks the client for its guard/dodge state before resolving damage against it
        BulletHits = 2,             // client registers bullet hits involving its own player
        MeleeHits = 4,              // client registers its own melee swing hits
        AreaHits = 8,               // client verifies host-detected area/ground/boss hits against its own position (flow D)
    }

    /// <summary>
    /// The client's authoritative view of the state that decides whether an incoming hit is
    /// blocked or dodged. Captured on the client at the moment it perceives the hit and sent to
    /// the host, which then evaluates the vanilla damage formula with this state forced in.
    /// </summary>
    public struct CombatSnapshot
    {
        public bool guardActive;      // the player considers its guard up
        public float guardElapsed;    // seconds since the guard was raised (perfect-guard window)
        public Vector2 guardDir;      // direction the guard is facing (aim direction)
        public bool dodgeInvincible;  // locally predicted dash i-frames are active
        public bool clientOnlyDodge;  // ignore the host's own dodge state (config)

        public void Write(NetworkWriter w)
        {
            w.WriteBool(guardActive);
            w.WriteFloat(guardElapsed);
            w.WriteVector2(guardDir);
            w.WriteBool(dodgeInvincible);
            w.WriteBool(clientOnlyDodge);
        }

        public static CombatSnapshot Read(NetworkReader r)
        {
            CombatSnapshot s;
            s.guardActive = r.ReadBool();
            s.guardElapsed = r.ReadFloat();
            s.guardDir = r.ReadVector2();
            s.dodgeInvincible = r.ReadBool();
            s.clientOnlyDodge = r.ReadBool();
            return s;
        }

        public override string ToString()
        {
            return "guard=" + guardActive + "(" + guardElapsed.ToString("0.00") + "s dir=" + guardDir + ") dodge=" + dodgeInvincible;
        }
    }

    public static class CsdUtil
    {
        /// <summary>The Hitbox transform vanilla collision code would have handed to Bullet.AttackOnServer / MeleeCollision.Attack.</summary>
        public static Transform FindHitboxTransform(CombatBehaviour victim)
        {
            if (victim == null) return null;
            Hitbox[] boxes = victim.GetComponentsInChildren<Hitbox>(true);
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i] != null && boxes[i].combatBehaviour == victim && boxes[i].gameObject.activeInHierarchy)
                    return boxes[i].transform;
            }
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i] != null && boxes[i].combatBehaviour == victim)
                    return boxes[i].transform;
            }
            return null;
        }

        public static CombatBehaviour CombatBehaviourFromCollider(Component c)
        {
            if (c == null) return null;
            Hitbox hb = c.GetComponent<Hitbox>();
            if (hb == null) return null;
            return hb.GetCombatBehaviour(0);
        }

        /// <summary>
        /// Client side melee detection re-uses the base MeleeCollision update loop. Subclasses that
        /// replace it (e.g. Circle_Distance) are left to the host on both sides.
        /// </summary>
        private static readonly Dictionary<Type, bool> _baseMeleeCache = new Dictionary<Type, bool>();
        public static bool IsBaseMeleeUpdate(MeleeCollision m)
        {
            if (m == null) return false;
            Type t = m.GetType();
            bool ok;
            if (_baseMeleeCache.TryGetValue(t, out ok)) return ok;
            MethodInfo mi = t.GetMethod("OnUpdateServer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            ok = mi != null && mi.DeclaringType == typeof(MeleeCollision);
            _baseMeleeCache[t] = ok;
            return ok;
        }

        /// <summary>
        /// The host-side checks Bullet.AttackOnServer / UnitAvatar.ApplyDamage make before a bullet
        /// hit can do anything (ignored list, leader / follower, faction). Evaluated on the client
        /// too so it does not report pairs the host is certain to reject.
        /// </summary>
        public static bool BulletCanHurt(Bullet b, CombatBehaviour cb)
        {
            return BulletCanHurt(b, cb, b != null ? b.AttackableFactionLayers : 0L);
        }

        /// <summary>Client variant using the authoritative faction mask sent by the host.</summary>
        public static bool BulletCanHurt(Bullet b, CombatBehaviour cb, long targetFactionLayers)
        {
            if (b == null || cb == null) return false;
            if (b.ignored.Contains(cb)) return false;
            UnitAvatar owner = b.NetworkOwner;
            UnitAvatar u = cb as UnitAvatar;
            if (owner != null && u != null)
            {
                if (u.NetworkLeader == owner) return false;
                if (u.followers != null && u.followers.Contains(owner)) return false;
                if (u.monsterType != EMonsterType.Dummy && !CombatManager.ContainsAttackableFaction(targetFactionLayers, u.faction)) return false;
            }
            return true;
        }

        /// <summary>Vanilla's "already attacked" test (Bullet.attackedList) as AttackOnServer performs it.</summary>
        public static bool BulletAlreadyAttacked(Bullet b, CombatBehaviour cb)
        {
            List<Bullet.Attacked> list = b != null ? b.attackedList : null;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if (list[i].combatBehaviour == cb) return true;
            return false;
        }

        /// <summary>
        /// The hit direction as vanilla Bullet.AttackOnServer computes it, from the bullet position
        /// and the moving direction the caller has (CurMovingDirection on the host, the observed
        /// displacement on the client, whose fallback is the point direction).
        /// </summary>
        /// <paramref name="pointFallback"/>: use the point direction when the moving direction is
        /// unknown (near zero). The host passes CurMovingDirection straight through like vanilla (a
        /// zero direction means "guard from any facing" in ApplyDamage); the client only knows the
        /// observed displacement, which is zero for one frame after spawn.
        public static Vector2 VanillaHitDirection(Bullet b, Vector3 bulletPos, Vector3 hitPos, Vector2 movingDir, bool pointFallback)
        {
            Vector2 point = hitPos - bulletPos;
            if (b.MoveModule == null) return point;
            switch (b.MoveModule.ShapeOfAttack)
            {
                case EShapeOfAttack.Point: return point;
                case EShapeOfAttack.Directional: return !pointFallback || movingDir.sqrMagnitude > 0.000001f ? movingDir : point;
                default: return Vector2.zero;
            }
        }

        /// <summary>How a client registered a bullet hit; the host mirrors the matching vanilla path.</summary>
        public const byte BulletHitKind_Overlap = 0;   // Bullet.Update overlap test
        public const byte BulletHitKind_Ray = 1;       // BulletMoveModule_RaycastArrow hitscan

        /// <summary>
        /// Bullets whose hit volume is only simulated on the host (colliders that grow in
        /// server-only code) cannot be reproduced client side; they stay host-detected on both
        /// sides. Same list on host and client (same DLL), so both agree.
        /// </summary>
        public static bool IsBulletExcluded(Bullet b)
        {
            if (b == null) return true;
            BulletMoveModule m = b.MoveModule;
            if (m == null) return false;
            return m is BulletMoveModule_WaveWithParticle
                || m is BulletMoveModule_RingWave;
        }

        public static NetworkBehaviour FindBehaviour(Dictionary<uint, NetworkIdentity> spawned, uint netId, byte componentIndex)
        {
            NetworkIdentity id;
            if (!spawned.TryGetValue(netId, out id) || id == null) return null;
            NetworkBehaviour[] behaviours = id.NetworkBehaviours;
            if (behaviours == null || componentIndex >= behaviours.Length) return null;
            return behaviours[componentIndex];
        }

        public static T FindComponent<T>(Dictionary<uint, NetworkIdentity> spawned, uint netId) where T : Component
        {
            NetworkIdentity id;
            if (!spawned.TryGetValue(netId, out id) || id == null) return null;
            return id.GetComponent<T>();
        }

        /// <summary>DamageInstance objects are pooled by the game (ring of 20); copy the one we were handed so it survives a round trip.</summary>
        public static DamageInstance CloneDamage(DamageInstance src)
        {
            DamageInstance d = R.NewDamageInstance();
            d.origin = src.origin;
            d.id = src.id;
            d.damagePoint = src.damagePoint;
            d.failed = src.failed;
            d.targetFactionLayers = src.targetFactionLayers;
            d.damage = src.damage;
            d.damageType = src.damageType;
            d.fromType = src.fromType;
            d.direction = src.direction;
            d.staggeringLevel = src.staggeringLevel;
            d.externalForcePower = src.externalForcePower;
            d.victim = src.victim;
            d.criticalChancePercent = src.criticalChancePercent;
            d.criticalDamageRate = src.criticalDamageRate;
            d.criticalDamageMultiplier = src.criticalDamageMultiplier;
            d.ignoreDefense = src.ignoreDefense;
            d.boingToHit = src.boingToHit;
            d.showDamage = src.showDamage;
            d.useCustomColor = src.useCustomColor;
            d.stun = src.stun;
            d.color = src.color;
            d.elementalType = src.elementalType;
            d.isSystemDamage = src.isSystemDamage;
            d.hpSteal = src.hpSteal;
            d.ignoreDebuff = src.ignoreDebuff;
            d.absoluteEvasionPercent = src.absoluteEvasionPercent;
            d.ignoreDebuffTouch = src.ignoreDebuffTouch;
            d.damageObject = src.damageObject;
            d.isExecutionAttack = src.isExecutionAttack;
            d.isCriticalAttack = src.isCriticalAttack;
            d.damageResult = src.damageResult;
            d.flag = src.flag;
            return d;
        }
    }
}

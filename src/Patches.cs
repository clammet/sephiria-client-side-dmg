using System;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace ClientSideDamage
{
    // ---------------------------------------------------------------- damage resolution (host)

    [HarmonyPatch(typeof(UnitAvatar), "ApplyDamage", new Type[] { typeof(DamageInstance) })]
    internal static class Patch_UnitAvatar_ApplyDamage
    {
        private static bool Prefix(UnitAvatar __instance, DamageInstance damage, ref EApplyDamageResult __result)
        {
            if (!Plugin.On || !NetworkServer.active) return true;
            try
            {
                EApplyDamageResult r;
                if (ServerSide.TryHandleApplyDamage(__instance, damage, out r))
                {
                    __result = r;
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[CSD/host] ApplyDamage prefix failed, falling back to vanilla: " + e);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerAvatar), "GetGuardDirection")]
    internal static class Patch_PlayerAvatar_GetGuardDirection
    {
        private static bool Prefix(PlayerAvatar __instance, ref Vector2 __result)
        {
            if (ServerSide.GuardDirectionOverride.Active && ServerSide.GuardDirectionOverride.Target == __instance)
            {
                __result = ServerSide.GuardDirectionOverride.Direction;
                return false;
            }
            return true;
        }
    }

    // ---------------------------------------------------------------- freshest client position on the host

    [HarmonyPatch(typeof(NetworkTransformReliable), "UpdateServer")]
    internal static class Patch_NetworkTransformReliable_UpdateServer
    {
        private static bool Prefix(NetworkTransformReliable __instance)
        {
            if (!NetworkServer.active) return true;
            try { return !ServerSide.TryApplyLatestClientPosition(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] latest-position apply failed: " + e); return true; }
        }
    }

    // ---------------------------------------------------------------- host side hit tests: switched off for
    // client-registered pairs (flow B/C), or parked whole and replayed on the client's reply (flow A/D)

    [HarmonyPatch(typeof(Bullet), "AttackOnServer")]
    internal static class Patch_Bullet_AttackOnServer
    {
        private static bool Prefix(Bullet __instance, Transform hit, ref bool __result)
        {
            if (!NetworkServer.active || !Plugin.On) return true;
            try
            {
                if (ServerSide.ShouldSuppressBulletServerHit(__instance, hit) || ServerSide.TryParkBulletHit(__instance, hit))
                {
                    __result = false;
                    return false;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] bullet attack prefix failed: " + e); }
            return true;
        }
    }

    [HarmonyPatch(typeof(MeleeCollision), "Attack")]
    internal static class Patch_MeleeCollision_Attack
    {
        private static bool Prefix(MeleeCollision __instance, int type, Vector3 hitPoint, Transform hitTransform, ref bool __result)
        {
            if (!NetworkServer.active || !Plugin.On) return true;
            try
            {
                if (ServerSide.ShouldSuppressMeleeServerHit(__instance, hitTransform) || ServerSide.TryParkMeleeHit(__instance, type, hitPoint, hitTransform))
                {
                    __result = false;
                    return false;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] melee attack prefix failed: " + e); }
            return true;
        }
    }

    // ---------------------------------------------------------------- client side hit detection

    [HarmonyPatch(typeof(Bullet), "Update")]
    internal static class Patch_Bullet_Update
    {
        private static void Postfix(Bullet __instance)
        {
            if (NetworkServer.active) return;
            try { ClientSide.OnBulletUpdate(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/client] bullet detection failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(BulletMoveModule_RaycastArrow), "UserCode_RpcSetSprite__Single")]
    internal static class Patch_RaycastArrow_RpcSetSprite
    {
        private static void Postfix(BulletMoveModule_RaycastArrow __instance, float distance)
        {
            if (NetworkServer.active) return;
            try { ClientSide.OnRaycastArrowSprite(__instance, distance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/client] hitscan detection failed: " + e); }
        }
    }

    // host -> modded clients: keep Bullet.isCollisionEnabled in sync (see ServerSide.SyncBulletCollisionState)
    [HarmonyPatch(typeof(Bullet), "OnSpawnFinalized")]
    internal static class Patch_Bullet_OnSpawnFinalized
    {
        private static void Postfix(Bullet __instance)
        {
            if (!NetworkServer.active) return;
            try { ServerSide.SyncBulletCollisionState(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] bullet collision sync failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(Bullet), "ActivateCollision")]
    internal static class Patch_Bullet_ActivateCollision
    {
        private static void Postfix(Bullet __instance)
        {
            if (!NetworkServer.active) return;
            try { ServerSide.SyncBulletCollisionState(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] bullet collision sync failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(Bullet), "DeactivateCollision")]
    internal static class Patch_Bullet_DeactivateCollision
    {
        private static void Postfix(Bullet __instance)
        {
            if (!NetworkServer.active) return;
            try { ServerSide.SyncBulletCollisionState(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] bullet collision sync failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(Bullet), "OnStopClient")]
    internal static class Patch_Bullet_OnStopClient
    {
        private static void Postfix(Bullet __instance) { ClientSide.OnBulletGone(__instance); }
    }

    [HarmonyPatch(typeof(Bullet), "OnDespawn")]
    internal static class Patch_Bullet_OnDespawn
    {
        private static void Postfix(Bullet __instance) { ClientSide.OnBulletGone(__instance); }
    }

    // host -> modded clients: swing parameters for the client side shape test (see ServerSide.SendMeleeSpawn)
    [HarmonyPatch(typeof(MeleeCollision), "OnSpawnFinalized")]
    internal static class Patch_MeleeCollision_OnSpawnFinalized
    {
        private static void Postfix(MeleeCollision __instance)
        {
            if (!NetworkServer.active) return;
            try { ServerSide.SendMeleeSpawn(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] melee spawn sync failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(MeleeCollision), "Update")]
    internal static class Patch_MeleeCollision_Update
    {
        private static void Postfix(MeleeCollision __instance)
        {
            if (NetworkServer.active) return;
            try { ClientSide.OnMeleeUpdate(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/client] melee detection failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(MeleeCollision), "OnDespawn")]
    internal static class Patch_MeleeCollision_OnDespawn
    {
        private static void Postfix(MeleeCollision __instance) { ClientSide.OnMeleeGone(__instance); }
    }

    [HarmonyPatch(typeof(MeleeCollision), "OnDestroy")]
    internal static class Patch_MeleeCollision_OnDestroy
    {
        private static void Postfix(MeleeCollision __instance) { ClientSide.OnMeleeGone(__instance); }
    }

    // ---------------------------------------------------------------- client side guard / dodge prediction

    [HarmonyPatch(typeof(PlayerAvatar), "SubAttackButtonDown")]
    internal static class Patch_PlayerAvatar_SubAttackButtonDown
    {
        private static void Prefix(PlayerAvatar __instance) { ClientSide.OnSubAttackDown(__instance); }
    }

    [HarmonyPatch(typeof(PlayerAvatar), "SubAttackButtonUp")]
    internal static class Patch_PlayerAvatar_SubAttackButtonUp
    {
        private static void Prefix(PlayerAvatar __instance) { ClientSide.OnSubAttackUp(__instance); }
    }

    [HarmonyPatch(typeof(UnitAvatar), "HookGuardEnabledValue")]
    internal static class Patch_UnitAvatar_HookGuardEnabledValue
    {
        private static void Postfix(UnitAvatar __instance, bool newValue)
        {
            try { ClientSide.OnGuardSync(__instance, newValue); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/client] guard sync tracking failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(CharacterDash), "StartDash")]
    internal static class Patch_CharacterDash_StartDash
    {
        private static void Prefix(CharacterDash __instance, out bool __state) { __state = __instance.IsDashing; }
        private static void Postfix(CharacterDash __instance, bool __state)
        {
            if (!__state && __instance.IsDashing)
            {
                try { ClientSide.OnDashStarted(__instance); }
                catch (Exception e) { Plugin.Log.LogError("[CSD/client] dash tracking failed: " + e); }
            }
        }
    }

    // ---------------------------------------------------------------- lifecycle

    [HarmonyPatch(typeof(PlayerAvatar), "OnStartServer")]
    internal static class Patch_PlayerAvatar_OnStartServer
    {
        private static void Postfix(PlayerAvatar __instance)
        {
            try { ServerSide.OnPlayerAvatarStartServer(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] player tracking failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(HorayNetworkManager), "OnServerDisconnect")]
    internal static class Patch_HorayNetworkManager_OnServerDisconnect
    {
        private static void Prefix(NetworkConnectionToClient conn)
        {
            try { ServerSide.OnServerDisconnect(conn); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] disconnect handling failed: " + e); }
        }
    }

    // A floor / scene transition drops any parked damage query so a hit from the previous floor
    // cannot land after the teleport.
    [HarmonyPatch(typeof(DungeonManager), "LocalMoveFloor")]
    internal static class Patch_DungeonManager_LocalMoveFloor
    {
        private static void Prefix(PlayerAvatar avatar)
        {
            try { ServerSide.FlushPending("floor move", avatar); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] flush on floor move failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(HorayNetworkManager), "OnServerSceneChanged")]
    internal static class Patch_HorayNetworkManager_OnServerSceneChanged
    {
        private static void Prefix()
        {
            try { ServerSide.FlushPending("scene change"); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] flush on scene change failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(HorayNetworkManager), "OnStopServer")]
    internal static class Patch_HorayNetworkManager_OnStopServer
    {
        private static void Postfix() { ServerSide.Reset(); }
    }

    [HarmonyPatch(typeof(HorayNetworkManager), "OnClientDisconnect")]
    internal static class Patch_HorayNetworkManager_OnClientDisconnect
    {
        private static void Postfix() { ClientSide.Reset(); }
    }

    [HarmonyPatch(typeof(HorayNetworkManager), "OnStopClient")]
    internal static class Patch_HorayNetworkManager_OnStopClient
    {
        private static void Postfix() { ClientSide.Reset(); }
    }
}

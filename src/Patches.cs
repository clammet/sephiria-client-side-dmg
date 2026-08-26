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
                if (ServerSide.TrySuppressDaggerServerDamage(__instance, damage, out r))
                {
                    __result = r;
                    return false;
                }
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
                // a parked bullet is frozen: the rest of the Update that parked it must not register hits either
                if (ServerSide.IsParked(__instance) || ServerSide.ShouldSuppressBulletServerHit(__instance, hit) || ServerSide.TryParkBulletHit(__instance, hit))
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

    // client-owned swings outlive their duration by an RTT-derived grace so the client's reports still
    // find them (see ServerSide.TryDeferMeleeDestroy)
    [HarmonyPatch(typeof(MeleeCollision), "DestroySelf")]
    internal static class Patch_MeleeCollision_DestroySelf
    {
        private static bool Prefix(MeleeCollision __instance, bool forceDestroy)
        {
            if (!NetworkServer.active || !Plugin.On) return true;
            try { return !ServerSide.TryDeferMeleeDestroy(__instance, forceDestroy); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] melee linger check failed: " + e); return true; }
        }
    }

    // ---------------------------------------------------------------- client side hit detection

    [HarmonyPatch(typeof(Bullet), "Update")]
    internal static class Patch_Bullet_Update
    {
        // host: speed tracking, and a parked bullet skips its vanilla update entirely (see ServerSide.TryParkBulletDestroy)
        private static bool Prefix(Bullet __instance)
        {
            if (!NetworkServer.active) return true;
            try { return !ServerSide.OnBulletUpdateHost(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] bullet update hook failed: " + e); return true; }
        }

        private static void Postfix(Bullet __instance)
        {
            if (NetworkServer.active)
            {
                try { ServerSide.OnBulletUpdatedHost(__instance); }
                catch (Exception e) { Plugin.Log.LogError("[CSD/host] bullet update hook failed: " + e); }
                return;
            }
            try { ClientSide.OnBulletUpdate(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/client] bullet detection failed: " + e); }
        }

        // vanilla Update threw: the postfix did not run, drop the per-Update moving state anyway
        private static void Finalizer(Exception __exception)
        {
            if (__exception != null && NetworkServer.active) ServerSide.ClearMoving();
        }
    }

    // Pentaxis (Unit_LibraryGuard) moves and tests its SpinDash box only on the server. The joined
    // client performs the corresponding test against the boss path it actually sees.
    [HarmonyPatch(typeof(Unit_LibraryGuard), "Update")]
    internal static class Patch_Unit_LibraryGuard_Update
    {
        private static void Postfix(Unit_LibraryGuard __instance)
        {
            try
            {
                if (NetworkServer.active) ServerSide.OnPentaxisUpdateHost(__instance);
                else ClientSide.OnPentaxisUpdate(__instance);
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD] Pentaxis spin-dash detection failed: " + e); }
        }
    }

    // host: which bullet is moving right now and whether its Move had tile contact - tells a DestroySelf
    // issued from Bullet.Update's tile loop apart from a lifetime / arrival destroy (see ServerSide.TryParkBulletDestroy)
    [HarmonyPatch(typeof(BulletMoveModule), "Move")]
    internal static class Patch_BulletMoveModule_Move
    {
        private static void Prefix(BulletMoveModule __instance)
        {
            if (!NetworkServer.active) return;
            try { ServerSide.OnBulletMoveBegin(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] bullet move hook failed: " + e); }
        }

        private static void Postfix(BulletMoveModule __instance, int __result)
        {
            if (!NetworkServer.active) return;
            try { ServerSide.OnBulletMoveEnd(__instance, __result); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] bullet move hook failed: " + e); }
        }
    }

    // a bullet that wants to destroy itself while a client's hit report may still be on its way is parked instead
    [HarmonyPatch(typeof(Bullet), "DestroySelf")]
    internal static class Patch_Bullet_DestroySelf
    {
        private static bool Prefix(Bullet __instance, bool forceDestroy)
        {
            if (!NetworkServer.active || !Plugin.On) return true;
            try { return !ServerSide.TryParkBulletDestroy(__instance, forceDestroy); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] bullet park check failed: " + e); return true; }
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
        private static void Postfix(Bullet __instance)
        {
            ClientSide.OnBulletGone(__instance);
            if (NetworkServer.active) ServerSide.OnBulletGoneHost(__instance);
        }
    }

    // Growth-parry daggers are not Mirror NetworkBehaviours. Correlate the independently
    // instantiated host/client copies and run their vanilla circle hit test on authoritative clients.
    [HarmonyPatch(typeof(Charm_GrowthParry), "RpcCreateBullet", new Type[] { typeof(Vector2), typeof(Vector2), typeof(float) })]
    internal static class Patch_CharmGrowthParry_RpcCreateBullet
    {
        private static void Prefix(Charm_GrowthParry __instance, Vector2 spawnPosition, Vector2 direction, float damage)
        {
            if (!NetworkServer.active || __instance == null) return;
            try
            {
                DaggerGrowthBullet template = __instance.bulletPrefab != null ? __instance.bulletPrefab.GetComponent<DaggerGrowthBullet>() : null;
                ServerSide.OnDaggerSpawn(__instance.NetworkAvatar, template, spawnPosition, direction, damage);
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] growth dagger spawn sync failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(DaggerGrowthBullet), "Initialize", new Type[] { typeof(UnitAvatar), typeof(Vector2), typeof(Vector2), typeof(float), typeof(bool) })]
    internal static class Patch_DaggerGrowthBullet_Initialize
    {
        private static void Postfix(DaggerGrowthBullet __instance, UnitAvatar owner, Vector2 position, Vector2 direction, float damage, bool isServerObject)
        {
            try
            {
                ServerSide.OnDaggerInitialized(__instance, owner, position, direction, damage, isServerObject);
                ClientSide.OnDaggerInitialized(__instance, owner, position, direction, damage, isServerObject);
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD] growth dagger initialise tracking failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(DaggerGrowthBullet), "Update")]
    internal static class Patch_DaggerGrowthBullet_Update
    {
        private static void Postfix(DaggerGrowthBullet __instance)
        {
            try { ClientSide.OnDaggerUpdate(__instance); }
            catch (Exception e) { Plugin.Log.LogError("[CSD/client] growth dagger hit test failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(DaggerGrowthBullet), "HitCheck")]
    internal static class Patch_DaggerGrowthBullet_HitCheck
    {
        private static void Prefix(DaggerGrowthBullet __instance, out DaggerGrowthBullet __state)
        {
            __state = ServerSide.PushDaggerHitCheck(__instance);
        }

        private static Exception Finalizer(Exception __exception, DaggerGrowthBullet __state)
        {
            ServerSide.PopDaggerHitCheck(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(DaggerGrowthBullet), "OnDestroy")]
    internal static class Patch_DaggerGrowthBullet_OnDestroy
    {
        private static void Prefix(DaggerGrowthBullet __instance)
        {
            try
            {
                ServerSide.OnDaggerGone(__instance);
                ClientSide.OnDaggerGone(__instance);
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD] growth dagger cleanup failed: " + e); }
        }
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

    // ---------------------------------------------------------------- lobby creation (host status chat line)

    /// <summary>
    /// SteamInvitation.HandleLobbyCreate is what shows "You've created a lobby. You can now invite
    /// people..." on the host (Steam); UI_MultiplayerPanel_E.HandleCreated is the EOS counterpart.
    /// The host's status line is posted right there. Installed by hand (not via PatchAll) so a
    /// rename in a game update only costs the chat line, not the mod.
    /// </summary>
    internal static class LobbyCreatedHooks
    {
        private const string InviteGuideKey = "UI_Multiplayer_InviteGuide";
        private static string _inviteGuideText;

        public static void Install()
        {
            int n = 0;
            // 1. the creation handlers themselves
            HarmonyMethod post = new HarmonyMethod(AccessTools.Method(typeof(LobbyCreatedHooks), nameof(Postfix)));
            string[][] targets = { new[] { "SteamInvitation", "HandleLobbyCreate" }, new[] { "UI_MultiplayerPanel", "HandleCreated" }, new[] { "UI_MultiplayerPanel_E", "HandleCreated" } };
            for (int i = 0; i < targets.Length; i++)
            {
                Type t = AccessTools.TypeByName(targets[i][0]);
                System.Reflection.MethodInfo m = t == null ? null : AccessTools.Method(t, targets[i][1]);
                if (m == null) { Plugin.Log.LogWarning("[CSD] lobby creation hook target " + targets[i][0] + "." + targets[i][1] + " not found"); continue; }
                Plugin.HarmonyInstance.Patch(m, null, post, null, null, null);
                Plugin.Log.LogInfo("[CSD] lobby creation hook: " + targets[i][0] + "." + targets[i][1]);
                n++;
            }
            // 2. the system message the player actually sees ("You've created a lobby. You can now invite people...")
            System.Reflection.MethodInfo open = AccessTools.Method(typeof(UI_SystemMessage), "Open", new Type[] { typeof(string), typeof(float), typeof(bool) });
            if (open != null)
            {
                Plugin.HarmonyInstance.Patch(open, null, new HarmonyMethod(AccessTools.Method(typeof(LobbyCreatedHooks), nameof(SystemMessagePostfix))), null, null, null);
                Plugin.Log.LogInfo("[CSD] lobby creation hook: UI_SystemMessage.Open (" + InviteGuideKey + ")");
                n++;
            }
            else Plugin.Log.LogWarning("[CSD] lobby creation hook target UI_SystemMessage.Open not found");
            if (n == 0) Plugin.Log.LogWarning("[CSD] no lobby creation hook found (host status chat line will not be posted)");
        }

        private static void Postfix(System.Reflection.MethodBase __originalMethod)
        {
            try
            {
                Plugin.Log.LogInfo("[CSD/host] lobby creation handler ran: " + (__originalMethod != null ? __originalMethod.DeclaringType.Name + "." + __originalMethod.Name : "?"));
                ServerSide.OnLobbyCreated();
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] lobby status chat failed: " + e); }
        }

        private static void SystemMessagePostfix(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message)) return;
                if (_inviteGuideText == null)
                {
                    try { _inviteGuideText = Loc._(InviteGuideKey); } catch { _inviteGuideText = ""; }
                    if (string.IsNullOrEmpty(_inviteGuideText)) _inviteGuideText = "";
                }
                bool match = _inviteGuideText.Length > 0 && message == _inviteGuideText;
                if (Plugin.DebugOn) Plugin.Debug("[CSD] system message: \"" + message + "\"" + (match ? " (lobby invite guide)" : ""));
                if (!match) return;
                Plugin.Log.LogInfo("[CSD/host] lobby invite guide shown");
                ServerSide.OnLobbyCreated();
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD/host] lobby status chat failed: " + e); }
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

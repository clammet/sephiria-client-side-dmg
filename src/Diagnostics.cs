using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace ClientSideDamage
{
    /// <summary>
    /// Runtime health checks that only write to the BepInEx log: a Harmony self-test at start-up
    /// (does a detour actually run on this Mono build?), lifecycle transitions, and an F9 dump.
    /// </summary>
    internal static class Diagnostics
    {
        private static bool _selfTestHit;
        private static bool _firstUpdateLogged;
        private static bool _serverWasActive, _clientWasReady;
        private static bool _hotkeyBroken;
        private static Type _keyboardType;
        private static PropertyInfo _keyboardCurrent, _f9Key, _wasPressed;

        /// <summary>
        /// Patches a pure game function with a prefix, calls it, and reports whether the prefix ran.
        /// If it did not, no patch of this mod does anything at runtime, whatever PatchAll said.
        /// </summary>
        public static void HarmonySelfTest()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(HorayUtility), "GetAngleFromVector", new Type[] { typeof(Vector2) });
                if (target == null) { Plugin.Log.LogWarning("[CSD/diag] self-test target HorayUtility.GetAngleFromVector not found"); return; }
                MethodInfo prefix = AccessTools.Method(typeof(Diagnostics), nameof(SelfTestPrefix));
                _selfTestHit = false;
                Plugin.HarmonyInstance.Patch(target, new HarmonyMethod(prefix), null, null, null, null);
                float r = HorayUtility.GetAngleFromVector(Vector2.right);
                Plugin.HarmonyInstance.Unpatch(target, prefix);
                if (_selfTestHit) Plugin.Log.LogInfo("[CSD/diag] Harmony self-test OK: patches are effective at runtime (GetAngleFromVector(right) = " + r + ")");
                else Plugin.Log.LogError("[CSD/diag] Harmony self-test FAILED: a patched method ran without its prefix. Detours are not effective on this runtime - none of this mod's hooks will fire.");
                int n = 0; foreach (MethodBase m in Plugin.HarmonyInstance.GetPatchedMethods()) n++;
                Plugin.Log.LogInfo("[CSD/diag] " + n + " methods carry patches from " + Plugin.GUID);
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD/diag] Harmony self-test threw: " + e); }
        }

        private static void SelfTestPrefix() { _selfTestHit = true; }

        /// <summary>Called every frame from Plugin.Update (before anything else).</summary>
        public static void Tick()
        {
            if (!_firstUpdateLogged)
            {
                _firstUpdateLogged = true;
                Plugin.Log.LogInfo("[CSD/diag] first Update (Ready=" + Plugin.Ready + ", On=" + Plugin.On + ", DebugLog=" + Plugin.DebugOn + ")");
            }
            bool sa = NetworkServer.active;
            if (sa != _serverWasActive) { _serverWasActive = sa; Plugin.Log.LogInfo("[CSD/diag] NetworkServer.active -> " + sa); }
            bool cr = NetworkClient.active && NetworkClient.ready;
            if (cr != _clientWasReady) { _clientWasReady = cr; Plugin.Log.LogInfo("[CSD/diag] NetworkClient ready -> " + cr); }
            if (F9Pressed()) Dump();
        }

        // Unity's new Input System (the game uses it) via reflection so the build does not need the assembly;
        // falls back to the legacy Input class, and gives up if neither works.
        private static bool F9Pressed()
        {
            if (_hotkeyBroken) return false;
            try
            {
                if (_keyboardType == null)
                {
                    _keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                    if (_keyboardType != null)
                    {
                        _keyboardCurrent = _keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                        _f9Key = _keyboardType.GetProperty("f9Key");
                    }
                }
                if (_keyboardType != null && _keyboardCurrent != null && _f9Key != null)
                {
                    object kb = _keyboardCurrent.GetValue(null);
                    if (kb == null) return false;
                    object key = _f9Key.GetValue(kb);
                    if (key == null) return false;
                    if (_wasPressed == null) _wasPressed = key.GetType().GetProperty("wasPressedThisFrame");
                    return _wasPressed != null && (bool)_wasPressed.GetValue(key);
                }
                return Input.GetKeyDown(KeyCode.F9);
            }
            catch (Exception e)
            {
                _hotkeyBroken = true;
                Plugin.Log.LogWarning("[CSD/diag] F9 hotkey unavailable: " + e.Message);
                return false;
            }
        }

        /// <summary>F9: one-shot state dump plus a test line in the HUD log and the chat.</summary>
        public static void Dump()
        {
            try
            {
                DungeonManager dm = DungeonManager.Instance;
                GameLogWriter glw = GameLogWriter.Instance;
                ScreenFader fader = ScreenFader.Instance;
                string s = "[CSD/diag] F9 dump: Ready=" + Plugin.Ready + " On=" + Plugin.On + " DebugLog=" + Plugin.DebugOn
                    + " | NetworkServer.active=" + NetworkServer.active + " connections=" + NetworkServer.connections.Count
                    + " NetworkClient.active=" + NetworkClient.active + " ready=" + NetworkClient.ready + " localPlayer=" + (NetworkClient.localPlayer != null)
                    + " | DungeonManager=" + (dm != null) + (dm != null ? " isServer=" + dm.isServer + " lobbyPhase=" + dm.lobbyCreatedPhase + " runStarted=" + dm.isRunStarted : "")
                    + " | GameLogWriter=" + (glw != null) + " | fader=" + (fader != null ? fader.FadingState.ToString() : "none")
                    + " | host status: " + ServerSide.HostStatusLine();
                Plugin.Log.LogInfo(s);
                int n = 0; foreach (MethodBase m in Plugin.HarmonyInstance.GetPatchedMethods()) n++;
                Plugin.Log.LogInfo("[CSD/diag] patched methods: " + n);
                if (glw != null) glw.WriteLog("CSD : diag " + DateTime.Now.ToString("HH:mm:ss") + " (HUD log path)", Color.cyan);
                if (dm != null && NetworkClient.active && dm.isServer) dm.Chat(null, "CSD", "diag " + DateTime.Now.ToString("HH:mm:ss") + " (chat RPC path)");
            }
            catch (Exception e) { Plugin.Log.LogError("[CSD/diag] dump failed: " + e); }
        }
    }
}

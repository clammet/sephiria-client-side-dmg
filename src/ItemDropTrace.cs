using System;
using System.Text;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace ClientSideDamage
{
    /// <summary>
    /// Temporary tracing for dropped items (LootableItem / Item) that show on the host but not on a
    /// joined client. Every step of a drop's network life writes one "[CSD/item]" line to the BepInEx
    /// log: host-side spawn, the spawn message sent to each connection, client-side receipt (with the
    /// renderer state that decides whether it is visible and interactable), and any client-side
    /// destruction with the call stack. Drops are rare, so this is not gated behind DebugLog.
    /// </summary>
    internal static class ItemDropTrace
    {
        private static string Describe(Item item)
        {
            if (item == null) return "item=null";
            Transform t = item.transform;
            StringBuilder sb = new StringBuilder();
            sb.Append("netId=").Append(item.netId)
              .Append(" entity=").Append(item.itemEntityID)
              .Append(" instance=").Append(item.itemInstanceID)
              .Append(" qty=").Append(item.itemQuantity)
              .Append(" bound=").Append(item.isBound)
              .Append(" owned=").Append(item.isOwned)
              .Append(" pos=").Append(t.position.ToString("F2"))
              .Append(" local=").Append(t.localPosition.ToString("F2"))
              .Append(" parent=").Append(t.parent != null ? t.parent.name : "none")
              .Append(" active=").Append(item.gameObject.activeSelf).Append('/').Append(item.gameObject.activeInHierarchy);
            if (item.iconRenderer != null)
                sb.Append(" icon=").Append(item.iconRenderer.enabled).Append('/').Append(item.iconRenderer.sprite != null ? item.iconRenderer.sprite.name : "nosprite");
            return sb.ToString();
        }

        private static string Connections()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var kv in NetworkServer.connections)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(kv.Key).Append(kv.Value.isReady ? ":ready" : ":NOT-ready");
            }
            return sb.ToString();
        }

        [HarmonyPatch(typeof(Item), "OnStartServer")]
        private static class Patch_Item_OnStartServer
        {
            private static void Postfix(Item __instance)
            {
                try
                {
                    Plugin.Log.LogInfo("[CSD/item] host spawn " + Describe(__instance) + " connections=[" + Connections() + "]");
                }
                catch (Exception e) { Plugin.Log.LogError("[CSD/item] trace failed: " + e); }
            }
        }

        [HarmonyPatch(typeof(NetworkServer), "SendSpawnMessage")]
        private static class Patch_NetworkServer_SendSpawnMessage
        {
            private static void Postfix(NetworkIdentity identity, NetworkConnectionToClient conn)
            {
                try
                {
                    if (identity == null || conn == null) return;
                    Item item = identity.GetComponent<Item>();
                    if (item == null) return;
                    Plugin.Log.LogInfo("[CSD/item] host sent spawn netId=" + identity.netId + " -> conn " + conn.connectionId
                        + (conn.isReady ? " (ready)" : " (NOT ready)") + " observers=" + identity.observers.Count);
                }
                catch (Exception e) { Plugin.Log.LogError("[CSD/item] trace failed: " + e); }
            }
        }

        [HarmonyPatch(typeof(Item), "OnStartClient")]
        private static class Patch_Item_OnStartClient
        {
            private static void Postfix(Item __instance)
            {
                try
                {
                    Plugin.Log.LogInfo("[CSD/item] " + (NetworkServer.active ? "host-local" : "client") + " received " + Describe(__instance)
                        + " clientReady=" + NetworkClient.ready);
                }
                catch (Exception e) { Plugin.Log.LogError("[CSD/item] trace failed: " + e); }
            }
        }

        [HarmonyPatch(typeof(NetworkClient), "DestroyObject")]
        private static class Patch_NetworkClient_DestroyObject
        {
            private static void Prefix(uint netId)
            {
                try
                {
                    if (NetworkServer.active) return;   // host: vanilla server destroy, not interesting
                    NetworkIdentity ni;
                    if (!NetworkClient.spawned.TryGetValue(netId, out ni) || ni == null) return;
                    Item item = ni.GetComponent<Item>();
                    if (item == null) return;
                    Plugin.Log.LogInfo("[CSD/item] client destroy " + Describe(item) + "\n" + Environment.StackTrace);
                }
                catch (Exception e) { Plugin.Log.LogError("[CSD/item] trace failed: " + e); }
            }
        }
    }
}

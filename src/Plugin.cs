using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ClientSideDamage
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.sephiria.clientsidedamage";
        public const string NAME = "Client Side Damage";
        public const string VERSION = "1.2.0";

        // Bumped whenever the wire format of any RPC changes. Host and client must match.
        public const int PROTOCOL_VERSION = 5;

        public static ManualLogSource Log;
        public static Harmony HarmonyInstance;

        /// <summary>
        /// True once every start-up step succeeded (accessors resolved, RPCs registered without a
        /// hash collision, every Harmony patch applied). Cleared again if a collision with a game
        /// RPC is detected later. While false the mod is completely dormant, whatever the config says.
        /// </summary>
        public static bool Ready;

        /// <summary>The mod is initialised and switched on in the config.</summary>
        public static bool On { get { return Ready && Enabled != null && Enabled.Value; } }

        /// <summary>Cached General/DebugLog value: hot paths test this before building log strings.</summary>
        public static bool DebugOn;

        /// <summary>Short, player-facing reason why the mod is not Ready (empty while it is).</summary>
        public static string DisabledReason = "";

        // ---- config ----
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> DebugLog;

        // Host side (only matters on the machine that hosts the game)
        public static ConfigEntry<bool> HostDamageTakenAuthority;
        public static ConfigEntry<bool> HostBulletHitAuthority;
        public static ConfigEntry<bool> HostMeleeHitAuthority;
        public static ConfigEntry<float> HostReplyTimeout;
        public static ConfigEntry<bool> HostUseLatestClientPosition;
        public static ConfigEntry<bool> HostAreaHitAuthority;
        public static ConfigEntry<float> HostAreaHitMargin;

        // Client side (only matters on the machine that joins a game)
        public static ConfigEntry<bool> ClientDamageTakenAuthority;
        public static ConfigEntry<bool> ClientBulletHitDetection;
        public static ConfigEntry<bool> ClientMeleeHitDetection;
        public static ConfigEntry<bool> ClientPredictGuardFromInput;
        public static ConfigEntry<bool> ClientOnlyDodge;
        public static ConfigEntry<bool> ClientAreaHitVerification;
        public static ConfigEntry<bool> ClientAreaHitAnchoring;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch. When false the mod does nothing (vanilla host-authoritative combat).");
            DebugLog = Config.Bind("General", "DebugLog", false,
                "Verbose logging of every damage query / hit report. Only for troubleshooting.");

            HostDamageTakenAuthority = Config.Bind("Host", "DamageTakenAuthority", true,
                "When hosting: damage dealt to a joined player is resolved with that player's own guard/dodge state (asked from the client) instead of the host's copy of it.");
            HostBulletHitAuthority = Config.Bind("Host", "BulletHitAuthority", true,
                "When hosting: bullet/projectile hits that involve a joined player (as attacker or victim) are registered by that player's client instead of the host.");
            HostMeleeHitAuthority = Config.Bind("Host", "MeleeHitAuthority", true,
                "When hosting: melee swings performed by a joined player are hit-tested by that player's client instead of the host.");
            HostReplyTimeout = Config.Bind("Host", "ReplyTimeoutSeconds", 1.0f,
                new ConfigDescription("When hosting: if a client does not answer a damage query within this many seconds the hit is resolved with the host's state (vanilla behaviour).",
                    new AcceptableValueRange<float>(0.1f, 5f)));

            HostUseLatestClientPosition = Config.Bind("Host", "UseLatestClientPosition", true,
                "When hosting: apply a joined player's newest position update immediately instead of playing it back through Mirror's interpolation buffer. Every host-side test that uses that player's position (monster melee, ground/area effects, aiming) then sees them ~1-3 network ticks fresher. The host sees that player move slightly less smoothly.");

            HostAreaHitAuthority = Config.Bind("Host", "AreaHitAuthority", true,
                "When hosting: area / ground / boss hits the host detects against a joined player (stomps, cracks, lasers, ground fire, traps, explosions, ...) are verified by that player's client against its own position at the moment it sees the effect. The client answers hit or no-hit; a no-hit drops the damage.");
            HostAreaHitMargin = Config.Bind("Host", "AreaHitMargin", 0f,
                new ConfigDescription("When hosting, experimental: also let the client verify area hits the host narrowly missed. Every area test is widened by this many world units for joined players; if the widened test finds them, the client decides with the exact shape. 0 = off. Effects that keep per-victim state (shared target lists, once-per-victim traps) may then skip a player once because of a widened test the client rejected.",
                    new AcceptableValueRange<float>(0f, 3f)));

            ClientDamageTakenAuthority = Config.Bind("Client", "DamageTakenAuthority", true,
                "When joining: answer the host's damage queries with our local guard/dodge state.");
            ClientBulletHitDetection = Config.Bind("Client", "BulletHitDetection", true,
                "When joining: detect bullet hits locally (our own bullets against anything, and any bullet against us) and report them to the host.");
            ClientMeleeHitDetection = Config.Bind("Client", "MeleeHitDetection", true,
                "When joining: detect our own melee swing hits locally and report them to the host.");
            ClientPredictGuardFromInput = Config.Bind("Client", "PredictGuardFromInput", true,
                "When joining: treat the guard as already raised from the moment the guard button is pressed (instead of waiting for the host to confirm it), once the mod has seen the current weapon guard on that button.");
            ClientOnlyDodge = Config.Bind("Client", "ClientOnlyDodge", false,
                "When joining: only our locally predicted dash i-frames count as a dodge. When false the dodge counts if either our prediction OR the host's own state says we are invincible (more forgiving, never worse than vanilla).");
            ClientAreaHitVerification = Config.Bind("Client", "AreaHitVerification", true,
                "When joining: verify area / ground / boss hits the host detected against us with our own position at the moment we see the effect, and reject the ones that miss us here.");
            ClientAreaHitAnchoring = Config.Bind("Client", "AreaHitAnchoring", true,
                "When joining: move a verified shape to where we currently see the object it belongs to (a sweeping laser, a charging boss) before testing it, instead of using the host's world position. Turn off to test host world positions only.");

            DebugOn = DebugLog.Value;
            DebugLog.SettingChanged += (sender, args) => { DebugOn = DebugLog.Value; };

            // Everything that can fail on a game update happens here. Either all of it succeeds
            // and the mod is Ready, or nothing stays behind (patches are rolled back) and the
            // mod is dormant - never half-patched.
            HarmonyInstance = new Harmony(GUID);
            try
            {
                R.Init();
                CsdRpc.Register();
                HarmonyInstance.PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError("Failed to initialise (game version mismatch?), mod disabled: " + e);
                DisabledReason = "init failed (game update?)";
                try { HarmonyInstance.UnpatchSelf(); } catch (Exception ue) { Log.LogError("Rollback of partial patches failed: " + ue); }
                return;
            }
            Ready = true;

            // runtime config changes re-negotiate the feature set with the other side
            EventHandler onHost = (sender, args) => { try { ServerSide.OnHostConfigChanged(); } catch (Exception e) { Log.LogError(e); } };
            EventHandler onClient = (sender, args) => { try { ClientSide.OnClientConfigChanged(); } catch (Exception e) { Log.LogError(e); } };
            Enabled.SettingChanged += onHost;
            Enabled.SettingChanged += onClient;
            HostDamageTakenAuthority.SettingChanged += onHost;
            HostBulletHitAuthority.SettingChanged += onHost;
            HostMeleeHitAuthority.SettingChanged += onHost;
            HostAreaHitAuthority.SettingChanged += onHost;
            ClientDamageTakenAuthority.SettingChanged += onClient;
            ClientBulletHitDetection.SettingChanged += onClient;
            ClientMeleeHitDetection.SettingChanged += onClient;
            ClientAreaHitVerification.SettingChanged += onClient;

            // optional: shapes recorded from these callers are anchored to the object running the test
            try { AreaRecorder.PatchKnownCallers(); }
            catch (Exception e) { Log.LogWarning("Area hit caller tracking setup failed (area shapes will stay in world space): " + e); }
            Log.LogInfo(NAME + " " + VERSION + " loaded (protocol " + PROTOCOL_VERSION + ")");
        }

        private void Update()
        {
            // ServerSide.Tick runs even when not Ready (it then only announces the mod status to
            // joining players); everything else needs a fully initialised mod
            try
            {
                ServerSide.Tick();
                if (Ready) ClientSide.Tick();
            }
            catch (Exception e)
            {
                Log.LogError("Tick error: " + e);
            }
        }

        /// <summary>Detected after start-up (a game RPC registered later collided with one of ours): go dormant.</summary>
        public static void Disable(string reason, string shortReason)
        {
            if (!Ready) return;
            Ready = false;
            DisabledReason = shortReason;
            Log.LogError(NAME + " disabled: " + reason);
        }

        public static void Debug(string msg)
        {
            if (DebugOn) Log.LogInfo(msg);
        }
    }
}

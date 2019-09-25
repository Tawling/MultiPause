using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Harmony;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace MultiPause
{
    public class ModEntry : StardewModdingAPI.Mod
    {
        public const string PAUSE_IF_ANY = "ANY";
        public const string PAUSE_IF_ALL = "ALL";
        public const string AUTO_PAUSE_FOR_BALANCE = "AUTO";

        bool _shouldTimePass = false;
        bool queryMessageSent = false;

        public static Config Config { get; private set; }

        static Dictionary<long, STPState> PlayerStates { get; set; } = new Dictionary<long, STPState>();

        static List<CodeInstruction> SavedILCode = new List<CodeInstruction>();

        static IMonitor monitor;

        public void logStates()
        {
            foreach (var s in PlayerStates)
            {
                monitor.Log($"{s.Key}: {{ShouldTimePass: {s.Value.ShouldTimePass}, IsOnline: {s.Value.IsOnline}, TotalTimePaused: {s.Value.TotalTimePaused}}}");
            }
        }

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<Config>();

            monitor = this.Monitor;

            _shouldTimePass = Config.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_IF_ANY;

            monitor.Log($"Pause mode: {Config.PauseMode_ANY_ALL_AUTO}");

            var harmony = HarmonyInstance.Create("taw.multipause.patch1");
            monitor.Log("Patching...");

            var original = typeof(Game1).GetMethod("shouldTimePass");
            var prefix = typeof(ShouldTimePassPatch).GetMethod("Prefix");
            var transpiler = typeof(ShouldTimePassPatch).GetMethod("Transpiler");
            harmony.Patch(original, prefix: new HarmonyMethod(prefix), transpiler: new HarmonyMethod(transpiler));

            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Multiplayer.PeerContextReceived += this.OnPeerContextReceived;
            helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
            helper.Events.Multiplayer.PeerDisconnected += this.OnPeerDisconnected;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        }

        public static IEnumerable<CodeInstruction> CopySTPMethod(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            foreach (var c in SavedILCode)
            {
                yield return c;
            }
            monitor.Log("Copied method");
        }

            public static bool ShouldTimePassForCurrentPlayer()
        {
            if (Game1.isFestival() || Game1.CurrentEvent != null && Game1.CurrentEvent.isWedding)
                return false;
            if (Game1.paused || Game1.freezeControls || Game1.overlayMenu != null || Game1.activeClickableMenu != null && !(Game1.activeClickableMenu is StardewValley.Menus.BobberBar))
                return false;
            if (!Game1.player.CanMove && !Game1.player.UsingTool)
                return Game1.player.forceTimePass;
            return true;
        }

        public static int GetMinTimePaused()
        {
            int min = Int32.MaxValue;

            foreach (KeyValuePair<long, STPState> entry in PlayerStates)
            {
                min = Math.Min(min, entry.Value.TotalTimePaused);
            }

            return min;
        }

        public static bool ShouldPause()
        {
            if (Config.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_IF_ALL)
            {
                foreach (Farmer farmer in Game1.getOnlineFarmers())
                {
                    PlayerStates.TryGetValue(farmer.UniqueMultiplayerID, out STPState state);
                    if (state != null && state.IsOnline && state.ShouldTimePass)
                    {
                        return false;
                    }
                }
                return true;
            }
            else if (Config.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_IF_ANY)
            {
                foreach (Farmer farmer in Game1.getOnlineFarmers())
                {
                    PlayerStates.TryGetValue(farmer.UniqueMultiplayerID, out STPState state);
                    if (state != null && state.IsOnline && !state.ShouldTimePass)
                    {
                        return true;
                    }
                }
                return false;
            }
            else if (Config.PauseMode_ANY_ALL_AUTO.ToUpper() == AUTO_PAUSE_FOR_BALANCE)
            {

                int min = Int32.MaxValue;
                bool minPaused = false;
                foreach (Farmer farmer in Game1.getOnlineFarmers())
                {
                    PlayerStates.TryGetValue(farmer.UniqueMultiplayerID, out STPState state);
                    if (state != null && state.IsOnline && state.TotalTimePaused <= min)
                    {
                        if (state.TotalTimePaused == min && !state.ShouldTimePass)
                        {
                            minPaused = true;
                        }
                        else if (state.TotalTimePaused < min)
                        {
                            min = state.TotalTimePaused;
                            minPaused = !state.ShouldTimePass;
                        }
                    }
                }
                return minPaused;
            }
            return false;
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            foreach (KeyValuePair<long, STPState> entry in PlayerStates)
            {
                if (!entry.Value.ShouldTimePass || !entry.Value.IsOnline)
                {
                    entry.Value.TotalTimePaused++;
                }
            }

            if (Context.IsWorldReady)
            {
                var shouldTimePass = ShouldTimePassForCurrentPlayer();
                if (shouldTimePass != _shouldTimePass)
                {
                    _shouldTimePass = shouldTimePass;
                    SetPlayerState(Game1.player.UniqueMultiplayerID, shouldTimePass, Game1.ticks);

                    STPMessage message = new STPMessage(shouldTimePass);
                    this.Helper.Multiplayer.SendMessage(message, "ShouldTimePassState", modIDs: new[] { this.ModManifest.UniqueID });
                    monitor.Log($"Sent state {shouldTimePass}");
                    logStates();
                }

                if (!queryMessageSent)
                {
                    this.Helper.Multiplayer.SendMessage(true, "QueryShouldTimePassStates", modIDs: new[] { this.ModManifest.UniqueID });
                    queryMessageSent = true;
                    monitor.Log("Sent query message");
                }
            } else
            {
                queryMessageSent = false;
            }
        }

        private void SetPlayerState(long playerID, bool state, int ticks = -1, int totalTimePaused = -1)
        {
            PlayerStates.TryGetValue(playerID, out STPState dictState);
            if (dictState == null)
            {
                dictState = new STPState(state, ticks);
                PlayerStates.Add(playerID, dictState);
            }

            dictState.ShouldTimePass = state;
            if (ticks > -1)
                dictState.Ticks = ticks;
            if (totalTimePaused > -1)
                dictState.TotalTimePaused = totalTimePaused;
        }

        private void OnPeerContextReceived(object sender, PeerContextReceivedEventArgs e)
        {
            PlayerStates.TryGetValue(e.Peer.PlayerID, out STPState dictState);
            if (dictState == null)
            {
                dictState = new STPState(Config.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_IF_ANY, 0, totalTimePaused: GetMinTimePaused(), online: true);
                PlayerStates.Add(e.Peer.PlayerID, dictState);
            }
            else
            {
                dictState.ShouldTimePass = Config.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_IF_ANY;
                dictState.IsOnline = true;
            }
            monitor.Log($"Player joined {e.Peer.PlayerID}");
            logStates();
        }

        private void OnModMessageReceived(object sender, ModMessageReceivedEventArgs e)
        {
            if (e.Type == "ShouldTimePassState" && e.FromModID == this.ModManifest.UniqueID)
            {
                STPMessage message = e.ReadAs<STPMessage>();
                SetPlayerState(e.FromPlayerID, message.ShouldTimePass, ticks: message.Ticks);
                monitor.Log($"Received state {message.ShouldTimePass} from {e.FromPlayerID}");
                logStates();
            } else if (e.Type == "QueryShouldTimePassStates" && e.FromModID == this.ModManifest.UniqueID)
            {
                monitor.Log($"Query received from {e.FromPlayerID}");
                this.Helper.Multiplayer.SendMessage(_shouldTimePass, "ShouldTimePassState", modIDs: new[] { this.ModManifest.UniqueID });
            }
        }

        private void OnPeerDisconnected(object sender, PeerDisconnectedEventArgs e)
        {
            PlayerStates.TryGetValue(e.Peer.PlayerID, out STPState dictState);
            if (dictState == null)
            {
                dictState = new STPState(false, 0, totalTimePaused: GetMinTimePaused(), online: false);
                PlayerStates.Add(e.Peer.PlayerID, dictState);
            }
            else
            {

                dictState.ShouldTimePass = false;
                dictState.IsOnline = false;
            }
            monitor.Log($"Player left {e.Peer.PlayerID}");
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            queryMessageSent = false;
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            foreach (KeyValuePair<long, STPState> entry in PlayerStates)
            {
                entry.Value.TotalTimePaused = 0;
            }
            monitor.Log($"Reset entries to 0");
            logStates();
        }

        class STPMessage
        {
            public bool ShouldTimePass;
            public int Ticks;

            public STPMessage(bool shouldTimePass)
            {
                this.ShouldTimePass = shouldTimePass;
                Ticks = Game1.ticks;
            }
        }

        [HarmonyPatch(typeof(Game1))]
        [HarmonyPatch("shouldTimePass")]
        class ShouldTimePassPatch
        {
            public static bool Prefix(ref bool __result)
            {
                if (Game1.IsMultiplayer)
                {
                    __result = !ShouldPause();
                    return false;
                }
                return true;
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                foreach (var c in instructions)
                {
                    if (c.opcode == OpCodes.Call && c.operand.ToString().StartsWith("Boolean get_IsMultiplayer()"))
                    {
                        CodeInstruction loadFalse = new CodeInstruction(OpCodes.Ldc_I4_0);
                        loadFalse.labels = c.labels;
                        SavedILCode.Add(loadFalse);
                        yield return loadFalse;
                    }
                    else
                    {
                        SavedILCode.Add(c);
                        yield return c;
                    }
                }
                monitor.Log($"Patched.");// Copying method...");
                /*var harmony = HarmonyInstance.Create("taw.multipause.patch2");
                harmony.Patch(typeof(ModEntry).GetMethod("ShouldTimePassForCurrentPlayer"), transpiler: new HarmonyMethod(typeof(ModEntry).GetMethod("CopySTPMethod")));*/
            }
        }

        class STPState
        {
            public bool ShouldTimePass = false;
            public int Ticks;
            public int TotalTimePaused = 0;
            public bool IsOnline = true;

            public STPState(bool state, int ticks, int totalTimePaused = 0, bool online = true)
            {
                ShouldTimePass = state;
                Ticks = ticks;
                TotalTimePaused = totalTimePaused;
                IsOnline = online;
            }
        }
    }
}

using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.CampaignSystem;
using NobleSociety.Behaviors;
using NobleSociety.Addons;
using NSLog = NobleSociety.Logging.FileLogger;

namespace NobleSociety
{
    public class SubModule : MBSubModuleBase
    {
        private static bool _harmonyPatched;
        private static Harmony _harmony;
        private static bool _behaviorsAdded;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            if (_harmonyPatched) return;

            try
            {
                // Keep this ID stable/unique across builds
                _harmony = new Harmony("noblesociety.patch");
                _harmony.PatchAll();
                _harmonyPatched = true;
                NSLog.Log("[NobleSociety] Harmony.PatchAll succeeded.");
                // Avoid UI toasts here; UI stack may not be ready yet.
            }
            catch (System.Exception ex)
            {
                NSLog.Log("[NobleSociety][ERROR] Harmony patching failed: " + ex);
                // If you prefer a hard fail, rethrow:
                // throw;
            }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);

            // Only add behaviors for campaign games, and only once per game start
            if (!(gameStarterObject is CampaignGameStarter starter)) return;
            if (_behaviorsAdded) return;

            try
            {
                starter.AddBehavior(new NobleSocietyBehavior());
                starter.AddBehavior(new NobleMemoryEventsBehavior());
                starter.AddBehavior(new MeetingTracker());
                starter.AddBehavior(new CourtshipTelemetry());
                starter.AddBehavior(new NoblePatronageBehavior());
                starter.AddBehavior(new AIClanEconomyBehavior());
                starter.AddBehavior(new IdleNobleNeutralSeatingBehavior());
                starter.AddBehavior(new NpcRomanceCampaignBehavior());

                _behaviorsAdded = true;

                NSLog.Log("[NobleSociety] Behaviors registered.");
                InformationManager.DisplayMessage(new InformationMessage("[NobleSociety] Loaded."));
            }
            catch (System.Exception ex)
            {
                _behaviorsAdded = false; // let a future start try again
                NSLog.Log("[NobleSociety][ERROR] Adding behaviors failed: " + ex);
            }
        }

        // Optional: UI toast when main menu first appears
        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            // Safe to show UI here if desired:
            // InformationManager.DisplayMessage(new InformationMessage("[NobleSociety] Ready."));
        }
    }
}

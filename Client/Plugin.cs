using BepInEx;
using BepInEx.Logging;
using QuestHandbook.Diagnostics;
using QuestHandbook.Patches;

namespace QuestHandbook
{
    [
        BepInPlugin("com.kee.questhandbook", "Quest Handbook", "0.1.0"),
        BepInDependency("com.SPT.core", "4.1.0"),
    ]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource? LogSource;

        public void Awake()
        {
            LogSource = Logger;

            EnablePatch("handbook-open(controller)", new HandbookOpenControllerPatch());
            EnablePatch("handbook-open(session)", new HandbookOpenSessionPatch());
            EnablePatch("handbook-entity-shown", new HandbookEntityShownPatch());
            EnablePatch("handbook-quest-chosen", new HandbookQuestChosenPatch());
            EnablePatch("handbook-quest-icon", new HandbookQuestIconPatch());
            EnablePatch("handbook-close", new HandbookClosePatch());
            EnablePatch("profile-sync-raids", new ProfileQuestsSyncPatch());

            HandbookTrace.Enable();

            Logger.LogInfo($"Quest Handbook {Info.Metadata.Version} started");
        }

        private static void EnablePatch(string name, SPT.Reflection.Patching.ModulePatch patch)
        {
            try
            {
                patch.Enable();
            }
            catch (System.Exception ex)
            {
                LogSource?.LogError($"QuestHandbook: failed to enable patch '{name}': {ex}");
            }
        }
    }
}

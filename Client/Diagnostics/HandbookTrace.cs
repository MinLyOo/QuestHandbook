using EFT.HandBook;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;

namespace QuestHandbook.Diagnostics
{
    // Development-only observer: logs every HandbookScreen.Show overload call and
    // the screen's lifecycle (Awake/OnEnable/Start/OnDisable/OnDestroy) with its
    // instance id so the real open/close flow can be reconstructed from the log.
    // Remove (or stop calling Enable) before release.
    internal static class HandbookTrace
    {
        private static bool _enabled;
        private static Harmony? _harmony;

        public static void Enable()
        {
            if (_enabled)
            {
                return;
            }

            _enabled = true;
            try
            {
                _harmony = new Harmony("com.kee.questhandbook.trace");

                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(HandbookScreen))
                             .Where(m => m.Name == "Show"))
                {
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(HandbookTrace), nameof(LogShow)));
                    Plugin.LogSource?.LogInfo($"HandbookTrace patched Show({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                }

                PatchLifecycle("Awake", nameof(LogAwake));
                PatchLifecycle("OnEnable", nameof(LogOnEnable));
                PatchLifecycle("Start", nameof(LogStart));
                PatchLifecycle("OnDisable", nameof(LogOnDisable));
                PatchLifecycle("OnDestroy", nameof(LogOnDestroy));

                Plugin.LogSource?.LogInfo("HandbookTrace enabled");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"HandbookTrace enable failed: {ex}");
            }
        }

        private static void PatchLifecycle(string methodName, string postfixName)
        {
            try
            {
                MethodInfo? m = AccessTools.Method(typeof(HandbookScreen), methodName);
                if (m == null)
                {
                    Plugin.LogSource?.LogWarning($"HandbookTrace: no {methodName} on HandbookScreen");
                    return;
                }

                _harmony!.Patch(m, postfix: new HarmonyMethod(typeof(HandbookTrace), postfixName));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"HandbookTrace patch {methodName} failed: {ex}");
            }
        }

        private static string Describe(HandbookScreen screen)
        {
            if (screen == null || screen.gameObject == null)
            {
                return "<gone>";
            }

            string parent = screen.transform.parent != null ? screen.transform.parent.name : "<none>";
            return $"id={screen.gameObject.GetInstanceID()} activeSelf={screen.gameObject.activeSelf} " +
                   $"activeInHierarchy={screen.gameObject.activeInHierarchy} parent={parent}";
        }

        private static void LogShow(HandbookScreen __instance) => Plugin.LogSource?.LogInfo($"HB.Show: {Describe(__instance)}");
        private static void LogAwake(HandbookScreen __instance) => Plugin.LogSource?.LogInfo($"HB.Awake: {Describe(__instance)}");
        private static void LogOnEnable(HandbookScreen __instance) => Plugin.LogSource?.LogInfo($"HB.OnEnable: {Describe(__instance)}");
        private static void LogStart(HandbookScreen __instance) => Plugin.LogSource?.LogInfo($"HB.Start: {Describe(__instance)}");
        private static void LogOnDisable(HandbookScreen __instance) => Plugin.LogSource?.LogInfo($"HB.OnDisable: {Describe(__instance)}");
        private static void LogOnDestroy(HandbookScreen __instance) => Plugin.LogSource?.LogInfo($"HB.OnDestroy: {Describe(__instance)}");
    }
}

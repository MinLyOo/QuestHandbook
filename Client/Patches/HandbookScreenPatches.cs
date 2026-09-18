using EFT.HandBook;
using EFT.InventoryLogic;
using HarmonyLib;
using QuestHandbook.HandBook;
using SPT.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace QuestHandbook.Patches
{
    internal class HandbookOpenControllerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(EFT.HandBook.HandbookScreen))
                .FirstOrDefault(m => m.Name == "Show" && m.GetParameters().Length == 1);
        }

        [PatchPostfix]
        static void Postfix(EFT.HandBook.HandbookScreen __instance)
        {
            QuestBookUGUI.OnHandbookOpened(__instance);
        }
    }

    internal class HandbookOpenSessionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(EFT.HandBook.HandbookScreen))
                .FirstOrDefault(m => m.Name == "Show" && m.GetParameters().Length == 4);
        }

        [PatchPrefix]
        static void Prefix(EFT.HandBook.Handbook handbook, EFT.IEftSession session)
        {
            QuestBookData.SetProfile(session?.Profile);
            QuestBookUGUI.EnsureBookNodes(handbook);
        }

        [PatchPostfix]
        static void Postfix(EFT.HandBook.HandbookScreen __instance)
        {
            QuestBookUGUI.OnHandbookOpened(__instance);
        }
    }

    internal class HandbookEntityShownPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EntitiesPanel), nameof(EntitiesPanel.ShowEntity));
        }

        [PatchPrefix]
        static bool Prefix(HandbookNode node)
        {
            return QuestBookUGUI.OnCategorySelected(node);
        }
    }

    internal class HandbookQuestChosenPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EntitiesPanel), nameof(EntitiesPanel.NodeChosen));
        }

        [PatchPrefix]
        static bool Prefix(EntitiesPanel __instance, HandbookNode node)
        {
            return QuestBookUGUI.OnNodeChosen(__instance, node);
        }

        [PatchPostfix]
        static void Postfix(EntitiesPanel __instance, HandbookNode node)
        {
            QuestBookUGUI.OnNodeChosenDone(__instance, node);
        }
    }

    internal class HandbookQuestIconPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EntityIcon), nameof(EntityIcon.Show));
        }

        [PatchPrefix]
        static bool Prefix(EntityIcon __instance, Item item)
        {
            if (QuestBookUGUI.IsHiddenIconItem(item))
            {
                __instance.gameObject.SetActive(false);
                return false;
            }

            return true;
        }
    }

    internal class HandbookClosePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EFT.HandBook.HandbookScreen), nameof(EFT.HandBook.HandbookScreen.Close));
        }

        [PatchPrefix]
        static void Prefix()
        {
            QuestBookUGUI.OnHandbookClosed();
        }
    }

    // Sync completed quests from profile when a raid starts.
    internal class ProfileQuestsSyncPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EFT.LocalGame), nameof(EFT.LocalGame.Create));
        }

        [PatchPostfix]
        static void SyncStatus(EFT.Profile profile)
        {
            QuestBookData.SetProfile(profile);
        }
    }
}

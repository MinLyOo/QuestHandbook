using EFT;
using Newtonsoft.Json;
using SPT.Common.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace QuestHandbook
{
    internal static class QuestBookData
    {
        public sealed class Payload
        {
            public List<string> Traders { get; set; } = new();
            public List<Quest> Quests { get; set; } = new();
        }

        public sealed class Unlock
        {
            public string TraderId { get; set; } = "";
            public string TemplateId { get; set; } = "";
        }

        public sealed class Objective
        {
            [JsonProperty("type")] public string Type { get; set; } = "";
            [JsonProperty("target")] public List<string> Target { get; set; } = new();
            [JsonProperty("value")] public double Value { get; set; }
            [JsonProperty("onlyFoundInRaid")] public bool OnlyFoundInRaid { get; set; }
            [JsonProperty("locale")] public string? Locale { get; set; }
        }

        public sealed class RewardItemInfo
        {
            [JsonProperty("templateId")] public string TemplateId { get; set; } = "";
            [JsonProperty("count")] public int Count { get; set; }
        }

        public sealed class Reward
        {
            [JsonProperty("type")] public string Type { get; set; } = "";
            [JsonProperty("value")] public double Value { get; set; }
            [JsonProperty("target")] public string? Target { get; set; }
            [JsonProperty("items")] public List<RewardItemInfo> Items { get; set; } = new();
            [JsonProperty("stage")] public string? Stage { get; set; }
        }

        public sealed class Quest
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string QuestName { get; set; } = "";
            public string TraderId { get; set; } = "";
            public string Image { get; set; } = "";
            public List<string> PreReqs { get; set; } = new();
            public List<Unlock> Unlocks { get; set; } = new();
            public int MinLevel { get; set; }
            public int LoyaltyLevel { get; set; }
            public List<Objective> Objectives { get; set; } = new();
            public List<Reward> Rewards { get; set; } = new();
        }

        public static List<Quest> All = new();
        public static List<string> TraderOrder = new();
        public static string? LastError;

        private static readonly Dictionary<string, string> QuestNameById = new();
        private static readonly Dictionary<string, List<string>> UnlockTextByQuest = new();
        private static readonly Dictionary<string, List<string>> RewardTextByQuest = new();
        private static readonly HashSet<string> CompletedQuestIds = new();
        private static bool _loaded;

        public static bool IsLoaded => _loaded;

        public static bool TryEnsureLoaded(bool force = false)
        {
            if (_loaded && !force) return true;

            try
            {
                Plugin.LogSource?.LogInfo("QuestHandbook: requesting /quest-book/quests ...");
                string response = RequestHandler.GetJson("/quest-book/quests");
                Payload payload = JsonConvert.DeserializeObject<Payload>(response)!;

                if (payload.Quests is null or { Count: 0 })
                {
                    LastError = "server returned no quest payload";
                    Plugin.LogSource?.LogError($"QuestHandbook: {LastError}");
                    return false;
                }

                TraderOrder = payload.Traders;
                All = payload.Quests;

                QuestNameById.Clear();
                foreach (var quest in All)
                {
                    if (!string.IsNullOrEmpty(quest.Id) && !QuestNameById.ContainsKey(quest.Id))
                    {
                        QuestNameById.Add(quest.Id, ResolveQuestDisplayName(quest));
                    }
                }

                PreComputeUnlockTexts();
                PreComputeRewardTexts();
                LastError = null;
                _loaded = true;
                Plugin.LogSource?.LogInfo($"QuestHandbook: loaded {All.Count} quests across {TraderOrder.Count} traders");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Plugin.LogSource?.LogError($"QuestHandbook: failed to load quest data: {ex}");
                return false;
            }
        }

        public static string ResolveQuestDisplayName(Quest quest)
        {
            string name = quest.Name;
            if (!string.IsNullOrEmpty(name))
            {
                string localized = name.Localized(null);
                if (!string.IsNullOrEmpty(localized) && localized != name) return localized;
            }
            return !string.IsNullOrEmpty(quest.QuestName) ? quest.QuestName : quest.Id ?? "?";
        }

        public static string ResolveQuestNameById(string questId)
        {
            return QuestNameById.TryGetValue(questId, out string name) ? name : questId;
        }

        /// <summary>Looks up the full quest entry (trader, level, prerequisites) by id.</summary>
        public static Quest? FindQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return null;
            return All.FirstOrDefault(q => q.Id == questId);
        }

        public static string? ResolveQuestDescription(string? questId)
        {
            if (string.IsNullOrEmpty(questId)) return null;
            string[] keys = { questId + " description", "quests/" + questId + "/description" };
            foreach (string key in keys)
            {
                try
                {
                    string localized = key.Localized(null);
                    if (!string.IsNullOrEmpty(localized) && localized != key) return localized;
                }
                catch { }
            }
            return null;
        }

        public static string ResolveItemName(string templateId)
        {
            if (string.IsNullOrEmpty(templateId)) return "?";
            try
            {
                string nameKey = templateId + " Name";
                string localized = nameKey.Localized(null);
                if (!string.IsNullOrEmpty(localized) && localized != nameKey) return localized;
                string shortNameKey = templateId + " ShortName";
                localized = shortNameKey.Localized(null);
                if (!string.IsNullOrEmpty(localized) && localized != shortNameKey) return localized;
            }
            catch { }
            return templateId;
        }

        public static string ResolveTraderName(string traderId)
        {
            if (string.IsNullOrEmpty(traderId)) return "?";
            try
            {
                string key = traderId + " Nickname";
                string localized = key.Localized(null);
                if (!string.IsNullOrEmpty(localized) && localized != key) return localized;
            }
            catch { }
            return traderId;
        }

        /// <summary>Localizes an English skill name (e.g. "Endurance" → "耐力"); keeps the raw name as fallback.</summary>
        public static string ResolveSkillName(string skill)
        {
            if (string.IsNullOrEmpty(skill)) return "?";
            try
            {
                string localized = skill.Localized(null);
                if (!string.IsNullOrEmpty(localized) && localized != skill) return localized;
            }
            catch { }
            return skill;
        }

        public static bool IsQuestCompleted(string questId)
        {
            return CompletedQuestIds.Contains(questId);
        }

        public static void MarkCompleted(string questId)
        {
            if (!string.IsNullOrEmpty(questId)) CompletedQuestIds.Add(questId);
        }

        public static void ClearCompleted()
        {
            CompletedQuestIds.Clear();
        }

        /// <summary>Local profile captured when the handbook is opened; used to read quest completion.</summary>
        private static EFT.Profile? _profile;

        /// <summary>Remembers the live profile so completion can be re-read every time the book is opened.</summary>
        public static void SetProfile(EFT.Profile? profile)
        {
            if (profile == null) return;
            _profile = profile;
            RefreshCompleted();
        }

        /// <summary>Marks quests whose status is Success as completed.</summary>
        public static void RefreshCompleted()
        {
            try
            {
                if (_profile == null) return;

                object? raw = ReadMember(_profile, "QuestsData");
                if (raw is not System.Collections.IEnumerable quests) return;

                var completed = new HashSet<string>();
                foreach (object quest in quests)
                {
                    if (quest == null) continue;
                    if (ReadMember(quest, "Status")?.ToString() != "Success") continue;

                    string? id = ReadMember(quest, "Id")?.ToString();
                    if (string.IsNullOrEmpty(id)) id = ReadMember(ReadMember(quest, "Template"), "Id")?.ToString();
                    if (!string.IsNullOrEmpty(id)) completed.Add(id!);
                }

                CompletedQuestIds.Clear();
                foreach (string id in completed) CompletedQuestIds.Add(id);
                Plugin.LogSource?.LogInfo($"QuestHandbook: {CompletedQuestIds.Count} completed quests in profile");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestHandbook: failed to read quest status: {ex.Message}");
            }
        }

        /// <summary>Field-first member lookup; EFT exposes these profile/quest entries as public fields.</summary>
        private static object? ReadMember(object? target, string name)
        {
            if (target == null) return null;
            Type type = target.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);
            return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);
        }

        public static IReadOnlyList<string> GetUnlockTexts(string questId)
        {
            return UnlockTextByQuest.TryGetValue(questId, out var rows) ? rows : Array.Empty<string>();
        }

        public static IReadOnlyList<string> GetRewardTexts(string questId)
        {
            return RewardTextByQuest.TryGetValue(questId, out var rows) ? rows : Array.Empty<string>();
        }

        public static List<Objective>? GetObjectives(string questId)
        {
            foreach (var q in All)
            {
                if (q.Id == questId) return q.Objectives;
            }
            return null;
        }

        private static void PreComputeUnlockTexts()
        {
            UnlockTextByQuest.Clear();
            foreach (var quest in All)
            {
                if (string.IsNullOrEmpty(quest.Id) || quest.Unlocks.Count == 0) continue;
                var rows = new List<(string Trader, string Item)>();
                foreach (var unlock in quest.Unlocks)
                {
                    if (string.IsNullOrEmpty(unlock.TemplateId)) continue;
                    string trader = ResolveTraderName(unlock.TraderId);
                    string item = ResolveItemName(unlock.TemplateId);
                    rows.Add((trader, item));
                }
                rows.Sort((a, b) =>
                {
                    int cmp = StringComparer.Ordinal.Compare(a.Item, b.Item);
                    return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Trader, b.Trader);
                });
                UnlockTextByQuest[quest.Id] = rows.Select(r => r.Item + "  [" + r.Trader + "]").ToList();
            }
        }

        private static void PreComputeRewardTexts()
        {
            RewardTextByQuest.Clear();
            foreach (var quest in All)
            {
                if (string.IsNullOrEmpty(quest.Id) || quest.Rewards.Count == 0) continue;
                var lines = new List<string>();
                foreach (var reward in quest.Rewards)
                {
                    switch (reward.Type)
                    {
                        case "Experience":
                            if (reward.Value > 0) lines.Add("经验 +" + reward.Value.ToString("N0"));
                            break;
                        case "TraderStanding":
                            if (reward.Value != 0 && !string.IsNullOrEmpty(reward.Target))
                            {
                                string traderName = ResolveTraderName(reward.Target);
                                string sign = reward.Value >= 0 ? "+" : "-";
                                lines.Add(traderName + " 声望 " + sign + Math.Abs(reward.Value).ToString("F2"));
                            }
                            break;
                        case "Item":
                            foreach (var ri in reward.Items)
                            {
                                string itemName = ResolveItemName(ri.TemplateId);
                                string countStr = ri.Count > 1 ? " x" + ri.Count : "";
                                lines.Add(itemName + countStr);
                            }
                            break;
                        case "Skill":
                            if (reward.Value > 0 && !string.IsNullOrEmpty(reward.Target))
                                lines.Add(ResolveSkillName(reward.Target) + " 技能经验 +" + reward.Value.ToString("N0"));
                            break;
                        case "TraderUnlock":
                            if (!string.IsNullOrEmpty(reward.Target))
                                lines.Add("解锁商人 " + ResolveTraderName(reward.Target));
                            break;
                        case "HideoutRecipe":
                            if (!string.IsNullOrEmpty(reward.Target))
                                lines.Add("解锁藏身处配方 " + ResolveRecipeRewardName(quest, reward.Target!));
                            break;
                    }
                }
                if (lines.Count > 0) RewardTextByQuest[quest.Id] = lines;
            }
        }

        /// <summary>
        /// SPT's ProductionScheme reward targets a BSG scheme id that is indexed neither by the hideout
        /// recipes nor by the item tables, so the product is resolved through the recipe this quest unlocks.
        /// </summary>
        private static string ResolveRecipeRewardName(Quest quest, string target)
        {
            string direct = RecipeBookData.ResolveProductName(target);
            if (!string.IsNullOrEmpty(direct) && direct != target) return direct;

            if (RecipeBookData.All.Count == 0) RecipeBookData.TryEnsureLoaded();

            List<string> names = new();
            foreach (var recipe in RecipeBookData.All)
            {
                if (recipe.QuestId != quest.Id || string.IsNullOrEmpty(recipe.EndProduct)) continue;
                string name = ResolveItemName(recipe.EndProduct);
                if (string.IsNullOrEmpty(name) || name == recipe.EndProduct) continue;
                if (recipe.Count > 1) name += " x" + recipe.Count;
                if (!names.Contains(name)) names.Add(name);
            }
            return names.Count > 0 ? string.Join(" / ", names) : target;
        }

        private static void LoadCompletedStatus()
        {
            // Status sync via Harmony patch on Profile.Save
        }
    }
}

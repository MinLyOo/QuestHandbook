using EFT;
using Newtonsoft.Json;
using SPT.Common.Http;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QuestHandbook
{
    /// <summary>
    /// Client-side cache of hideout construction / upgrade data.
    /// Loads from the mod route /quest-book/construction.
    /// </summary>
    internal static class ConstructionBookData
    {
        public sealed class Requirement
        {
            [JsonProperty("type")] public string Type { get; set; } = "";
            [JsonProperty("templateId")] public string? TemplateId { get; set; }
            [JsonProperty("count")] public int Count { get; set; }
            [JsonProperty("foundInRaid")] public bool FoundInRaid { get; set; }
            [JsonProperty("areaType")] public int AreaType { get; set; }
            [JsonProperty("requiredLevel")] public int RequiredLevel { get; set; }
            [JsonProperty("skillName")] public string? SkillName { get; set; }
            [JsonProperty("skillLevel")] public int SkillLevel { get; set; }
            [JsonProperty("traderId")] public string? TraderId { get; set; }
            [JsonProperty("loyaltyLevel")] public int LoyaltyLevel { get; set; }
            [JsonProperty("fleaPrice")] public double FleaPrice { get; set; }
        }

        public sealed class Bonus
        {
            [JsonProperty("id")] public string Id { get; set; } = "";
            [JsonProperty("type")] public string Type { get; set; } = "";
            [JsonProperty("value")] public double Value { get; set; }
            [JsonProperty("visible")] public bool Visible { get; set; }
        }

        public sealed class Stage
        {
            [JsonProperty("level")] public int Level { get; set; }
            [JsonProperty("constructionTime")] public double ConstructionTime { get; set; }
            [JsonProperty("slots")] public int Slots { get; set; }
            [JsonProperty("requirements")] public List<Requirement> Requirements { get; set; } = new();
            [JsonProperty("bonuses")] public List<Bonus> Bonuses { get; set; } = new();
            [JsonProperty("unlockedRecipeIds")] public List<string> UnlockedRecipeIds { get; set; } = new();
            [JsonProperty("unlockedRecipeCount")] public int UnlockedRecipeCount { get; set; }
        }

        public sealed class Area
        {
            [JsonProperty("areaType")] public int AreaType { get; set; }
            [JsonProperty("stages")] public List<Stage> Stages { get; set; } = new();

            public string DisplayName => ResolveAreaName(AreaType);
        }

        public sealed class Payload
        {
            [JsonProperty("areas")] public List<Area> Areas { get; set; } = new();
        }

        public static List<Area> AllAreas = new();
        public static string? LastError;

        private static bool _loaded;

        public static bool IsLoaded => _loaded;

        public static bool TryEnsureLoaded(bool force = false)
        {
            if (_loaded && !force) return true;

            try
            {
                string json = RequestHandler.GetJson("/quest-book/construction");
                Payload? payload = JsonConvert.DeserializeObject<Payload>(json);
                if (payload?.Areas is { Count: > 0 })
                {
                    AllAreas = payload.Areas;
                    LastError = null;
                    _loaded = true;
                    Plugin.LogSource?.LogInfo($"QuestHandbook: loaded {AllAreas.Count} construction areas");
                    return true;
                }

                LastError = "no areas returned by /quest-book/construction";
                Plugin.LogSource?.LogError($"QuestHandbook: {LastError}");
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Plugin.LogSource?.LogError($"QuestHandbook: failed to load construction data: {ex}");
                return false;
            }
        }

        /// <summary>Resolves a facility display name from the game locale.</summary>
        public static string ResolveAreaName(int areaType)
        {
            string key = $"hideout_area_{areaType}_name";
            try
            {
                string localized = key.Localized(null);
                if (!string.IsNullOrEmpty(localized) && localized != key) return localized;
            }
            catch { }
            return "设施 " + areaType;
        }

        /// <summary>Resolves an item display name from the game locale.</summary>
        public static string ResolveItemName(string? templateId)
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

        /// <summary>Resolves a trader display name from the game locale.</summary>
        public static string ResolveTraderName(string? traderId)
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

        /// <summary>Resolves a skill display name from the game locale.</summary>
        public static string ResolveSkillName(string? skillName)
        {
            if (string.IsNullOrEmpty(skillName)) return "?";
            try
            {
                string localized = skillName.Localized(null);
                if (!string.IsNullOrEmpty(localized) && localized != skillName) return localized;
            }
            catch { }
            return skillName;
        }

        /// <summary>Areas sorted by area type, each with stages sorted by level.</summary>
        public static List<Area> GetOrderedAreas()
        {
            return AllAreas.OrderBy(a => a.AreaType).ToList();
        }

        /// <summary>
        /// Localized display text for a hideout bonus. Per-instance bonuses (TextBonus) carry their
        /// own locale entry keyed by bonus id; typed bonuses fall back to "hideout_&lt;Type&gt;".
        /// </summary>
        public static string ResolveBonusName(string? type, string? id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                string byIdKey = "hideout_" + id;
                try
                {
                    string localized = byIdKey.Localized(null);
                    if (!string.IsNullOrEmpty(localized) && localized != byIdKey) return localized;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(type))
            {
                string byTypeKey = "hideout_" + type;
                try
                {
                    string localized = byTypeKey.Localized(null);
                    if (!string.IsNullOrEmpty(localized) && localized != byTypeKey) return localized;
                }
                catch { }
            }

            return type ?? "";
        }

        /// <summary>
        /// Per-hour unit suffix for regeneration bonuses, extracted from the game locale template
        /// (e.g. "{0} ep/hr ({1} ep/hr in total)" → "能量/小时"). Empty for non-rate bonuses.
        /// </summary>
        public static string ResolveBonusUnit(string? type)
        {
            string? template = type switch
            {
                "EnergyRegeneration" => "{0} ep/hr ({1} ep/hr in total)",
                "HealthRegeneration" => "{0} hp/hr ({1} hp/hr in total)",
                "HydrationRegeneration" => "{0} wp/hr ({1} wp/hr in total)",
                _ => null,
            };
            if (template is null) return "";

            try
            {
                string localized = template.Localized(null);
                if (string.IsNullOrEmpty(localized) || localized == template) return "";
                int paren = localized.IndexOf(" (", StringComparison.Ordinal);
                string head = paren > 0 ? localized.Substring(0, paren) : localized;
                return head.Replace("{0}", "").Trim();
            }
            catch { return ""; }
        }
    }
}
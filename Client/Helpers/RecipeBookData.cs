using EFT;
using Newtonsoft.Json;
using SPT.Common.Http;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QuestHandbook
{
    /// <summary>
    /// Client-side cache of hideout production recipes, grouped by facility (area).
    /// Data comes from the mod route when available, otherwise from SPT's built-in
    /// /client/hideout/production/recipes endpoint so no server mod update is required.
    /// </summary>
    internal static class RecipeBookData
    {
        public sealed class Requirement
        {
            [JsonProperty("type")] public string Type { get; set; } = "";
            [JsonProperty("templateId")] public string TemplateId { get; set; } = "";
            [JsonProperty("count")] public int Count { get; set; }
            [JsonProperty("isSpawnedInSession")] public bool FoundInRaid { get; set; }
            [JsonProperty("isFunctional")] public bool Functional { get; set; }
            [JsonProperty("requiredLevel")] public int RequiredLevel { get; set; }
            [JsonProperty("resource")] public int Resource { get; set; }
            [JsonProperty("questId")] public string? QuestId { get; set; }
            [JsonProperty("fleaPrice")] public double FleaPrice { get; set; }
        }

        public sealed class Recipe
        {
            [JsonProperty("_id")] public string SptId { get; set; } = "";
            [JsonProperty("id")] public string Id { get; set; } = "";
            [JsonProperty("areaType")] public int AreaType { get; set; }
            [JsonProperty("areaName")] public string AreaName { get; set; } = "";
            [JsonProperty("endProduct")] public string EndProduct { get; set; } = "";
            [JsonProperty("count")] public int Count { get; set; }
            [JsonProperty("productionTime")] public int ProductionTime { get; set; }
            [JsonProperty("continuous")] public bool Continuous { get; set; }
            [JsonProperty("locked")] public bool Locked { get; set; }
            [JsonProperty("isEncoded")] public bool Encoded { get; set; }
            [JsonProperty("needFuelForAllProductionTime")] public bool NeedFuel { get; set; }
            [JsonProperty("productionLimitCount")] public int ProductionLimit { get; set; }
            [JsonProperty("productValue")] public double ProductValue { get; set; }
            [JsonProperty("fleaPrice")] public double FleaPrice { get; set; }
            [JsonProperty("requirements")] public List<Requirement> Requirements { get; set; } = new();

            [JsonIgnore] public int AreaLevel { get; set; }
            [JsonIgnore] public string? QuestId { get; set; }
            [JsonIgnore] public List<Requirement> Materials { get; set; } = new();
            [JsonIgnore] public List<Requirement> Tools { get; set; } = new();
            [JsonIgnore] public List<Requirement> Resources { get; set; } = new();

            /// <summary>Flea price of all consumed materials (single production run).</summary>
            [JsonIgnore] public double MaterialCost => Materials.Sum(m => m.FleaPrice * Math.Max(m.Count, 1));

            /// <summary>Flea price of the whole production output.</summary>
            [JsonIgnore] public double ProductTotal => FleaPrice * Math.Max(Count, 1);
        }

        public sealed class Payload
        {
            [JsonProperty("recipes")] public List<Recipe> Recipes { get; set; } = new();
        }

        public static List<Recipe> All = new();
        public static string? LastError;

        private static readonly Dictionary<int, string> AreaNameByType = new();
        private static bool _loaded;

        public static bool IsLoaded => _loaded;

        public static bool TryEnsureLoaded(bool force = false)
        {
            if (_loaded && !force) return true;

            try
            {
                List<Recipe>? recipes = LoadFromModRoute() ?? LoadFromNativeRoute();
                if (recipes is null or { Count: 0 })
                {
                    LastError = "no recipes returned by /quest-book/recipes or /client/hideout/production/recipes";
                    Plugin.LogSource?.LogError($"QuestHandbook: {LastError}");
                    return false;
                }

                foreach (Recipe recipe in recipes) Normalize(recipe);
                All = recipes;

                AreaNameByType.Clear();
                foreach (Recipe recipe in All) AreaNameByType.TryAdd(recipe.AreaType, recipe.AreaName);

                LastError = null;
                _loaded = true;
                Plugin.LogSource?.LogInfo($"QuestHandbook: loaded {All.Count} recipes across {AreaNameByType.Count} facilities");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Plugin.LogSource?.LogError($"QuestHandbook: failed to load recipe data: {ex}");
                return false;
            }
        }

        /// <summary>Recipe payload served by this mod (includes item value + facility enum name).</summary>
        private static List<Recipe>? LoadFromModRoute()
        {
            try
            {
                Payload? payload = JsonConvert.DeserializeObject<Payload>(RequestHandler.GetJson("/quest-book/recipes"));
                if (payload?.Recipes is { Count: > 0 })
                {
                    Plugin.LogSource?.LogInfo("QuestHandbook: recipe source = mod route");
                    return payload.Recipes;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogInfo($"QuestHandbook: mod recipe route unavailable ({ex.Message}), falling back to native route");
            }
            return null;
        }

        /// <summary>SPT's built-in hideout production endpoint — always present, no server mod needed.</summary>
        private static List<Recipe>? LoadFromNativeRoute()
        {
            try
            {
                Payload? payload = JsonConvert.DeserializeObject<Payload>(RequestHandler.GetJson("/client/hideout/production/recipes"));
                if (payload?.Recipes is { Count: > 0 })
                {
                    Plugin.LogSource?.LogInfo("QuestHandbook: recipe source = native SPT route");
                    return payload.Recipes;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestHandbook: native recipe route failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>Splits the flat requirement list into materials / tools / resources and extracts area level + quest gate.</summary>
        private static void Normalize(Recipe recipe)
        {
            if (string.IsNullOrEmpty(recipe.Id)) recipe.Id = recipe.SptId;
            if (recipe.Count <= 0) recipe.Count = 1;

            foreach (Requirement requirement in recipe.Requirements)
            {
                switch (requirement.Type)
                {
                    case "Area":
                        if (requirement.RequiredLevel > recipe.AreaLevel) recipe.AreaLevel = requirement.RequiredLevel;
                        break;
                    case "Item":
                        recipe.Materials.Add(requirement);
                        break;
                    case "Tool":
                        recipe.Tools.Add(requirement);
                        break;
                    case "Resource":
                        if (requirement.Count <= 0) requirement.Count = requirement.Resource;
                        recipe.Resources.Add(requirement);
                        break;
                    case "QuestComplete":
                        recipe.QuestId ??= requirement.QuestId;
                        break;
                }
            }
            recipe.Requirements.Clear();
        }

        /// <summary>Recipe count per facility, keyed by area type.</summary>
        public static List<KeyValuePair<int, List<Recipe>>> GroupByArea()
        {
            return All.GroupBy(r => r.AreaType)
                      .OrderBy(g => g.Key)
                      .Select(g => new KeyValuePair<int, List<Recipe>>(g.Key, g.ToList()))
                      .ToList();
        }

        /// <summary>Resolves a facility display name from the game locale, falling back to the enum name.</summary>
        public static string ResolveAreaName(int areaType)
        {
            string key = $"hideout_area_{areaType}_name";
            try
            {
                string localized = key.Localized(null);
                if (!string.IsNullOrEmpty(localized) && localized != key) return localized;
            }
            catch { }

            return AreaNameByType.TryGetValue(areaType, out string? name) && !string.IsNullOrEmpty(name)
                ? name
                : "设施 " + areaType;
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

        /// <summary>
        /// Display name of the crafted item for a production scheme id (quest reward "ProductionScheme"
        /// targets the scheme, not an item template). Falls back to the raw value when unknown.
        /// </summary>
        public static string ResolveProductName(string schemeOrTemplateId)
        {
            if (string.IsNullOrEmpty(schemeOrTemplateId)) return "?";
            if (!_loaded && !TryEnsureLoaded()) return schemeOrTemplateId;

            foreach (Recipe recipe in All)
            {
                if (recipe.Id != schemeOrTemplateId && recipe.SptId != schemeOrTemplateId) continue;
                string product = ResolveItemName(recipe.EndProduct);
                if (!string.IsNullOrEmpty(product) && product != recipe.EndProduct) return product;
            }
            return ResolveItemName(schemeOrTemplateId);
        }
    }
}

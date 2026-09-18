using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Quest;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Ragfair;
using SPTarkov.Server.Core.Utils;
using System.Reflection;
using System.Text.Json.Serialization;

namespace QuestHandbook
{
    [Injectable]
    public class QuestHandbookMod(
        HttpResponseUtil httpResponseUtil,
        QuestHelper questHelper,
        TradersTable tradersTable,
        HideoutTable hideoutTable,
        HandbookHelper handbookHelper,
        RagfairPriceService ragfairPriceService,
        ISptLogger<QuestHandbookMod> logger
    )
    {
        private readonly HttpResponseUtil _httpResponseUtil = httpResponseUtil;
        private readonly QuestHelper _questHelper = questHelper;
        private readonly TradersTable _tradersTable = tradersTable;
        private readonly HideoutTable _hideoutTable = hideoutTable;
        private readonly HandbookHelper _handbookHelper = handbookHelper;
        private readonly RagfairPriceService _ragfairPriceService = ragfairPriceService;
        private readonly ISptLogger<QuestHandbookMod> _logger = logger;

        private readonly object _lock = new();
        private QuestBookPayload? _cache;
        private RecipeBookPayload? _recipeCache;
        private ConstructionBookPayload? _constructionCache;

        public ValueTask<string> HandleQuestBook()
        {
            if (_cache is null)
            {
                lock (_lock)
                {
                    if (_cache is null)
                    {
                        try { _cache = BuildPayload(); }
                        catch (Exception ex)
                        {
                            _logger.Error($"QuestHandbook: failed to build quest payload: {ex}");
                            _cache = new QuestBookPayload();
                        }
                    }
                }
            }
            return new ValueTask<string>(_httpResponseUtil.NoBody(_cache));
        }

        public ValueTask<string> HandleRecipeBook()
        {
            if (_recipeCache is null)
            {
                lock (_lock)
                {
                    if (_recipeCache is null)
                    {
                        try { _recipeCache = BuildRecipePayload(); }
                        catch (Exception ex)
                        {
                            _logger.Error($"QuestHandbook: failed to build recipe payload: {ex}");
                            _recipeCache = new RecipeBookPayload();
                        }
                    }
                }
            }
            return new ValueTask<string>(_httpResponseUtil.NoBody(_recipeCache));
        }

        public ValueTask<string> HandleConstructionBook()
        {
            if (_constructionCache is null)
            {
                lock (_lock)
                {
                    if (_constructionCache is null)
                    {
                        try { _constructionCache = BuildConstructionPayload(); }
                        catch (Exception ex)
                        {
                            _logger.Error($"QuestHandbook: failed to build construction payload: {ex}");
                            _constructionCache = new ConstructionBookPayload();
                        }
                    }
                }
            }
            return new ValueTask<string>(_httpResponseUtil.NoBody(_constructionCache));
        }

        private RecipeBookPayload BuildRecipePayload()
        {
            RecipeBookPayload payload = new();
            List<HideoutProduction>? recipes = _hideoutTable.Production?.Recipes;
            if (recipes is not { Count: > 0 }) return payload;

            foreach (HideoutProduction recipe in recipes)
            {
                if (recipe.AreaType is not { } areaType) continue;
                int areaNumber = (int)areaType;
                if (areaNumber < 0) continue;

                RecipeBookRecipe entry = new()
                {
                    Id = recipe.Id.ToString(),
                    AreaType = areaNumber,
                    AreaName = areaType.ToString(),
                    EndProduct = recipe.EndProduct.ToString(),
                    Count = recipe.Count ?? 1,
                    ProductionTime = (int)(recipe.ProductionTime ?? 0),
                    Continuous = recipe.Continuous ?? false,
                    Locked = recipe.Locked ?? false,
                    Encoded = recipe.IsEncoded ?? false,
                    NeedFuel = recipe.NeedFuelForAllProductionTime ?? false,
                    ProductionLimit = (int)(recipe.ProductionLimitCount ?? 0),
                    ProductValue = Math.Round(_handbookHelper.GetTemplatePrice(recipe.EndProduct), 0),
                    FleaPrice = GetFleaPrice(recipe.EndProduct),
                };

                foreach (Requirement requirement in recipe.Requirements ?? [])
                {
                    entry.Requirements.Add(new RecipeBookRequirement
                    {
                        Type = requirement.Type ?? "",
                        TemplateId = requirement.TemplateId?.ToString() ?? "",
                        Count = (int)(requirement.Count ?? 0),
                        FoundInRaid = requirement.IsSpawnedInSession ?? false,
                        Functional = requirement.IsFunctional ?? false,
                        RequiredLevel = requirement.RequiredLevel ?? 0,
                        Resource = (int)(requirement.Resource ?? 0),
                        QuestId = requirement.QuestId?.ToString(),
                        FleaPrice = requirement.TemplateId is { } requirementTpl ? GetFleaPrice(requirementTpl) : 0,
                    });
                }

                payload.Recipes.Add(entry);
            }

            payload.Recipes.Sort((a, b) => a.AreaType != b.AreaType
                ? a.AreaType.CompareTo(b.AreaType)
                : StringComparer.Ordinal.Compare(a.EndProduct, b.EndProduct));
            return payload;
        }

        /// <summary>Builds the hideout construction / upgrade overview payload.
        /// Reads each hideout area's stages (levels 1+), their material/condition requirements,
        /// unlocked bonuses, and cross-references recipes newly available at each level.</summary>
        private ConstructionBookPayload BuildConstructionPayload()
        {
            ConstructionBookPayload payload = new();
            _logger.Info("QuestHandbook: BuildConstructionPayload started");

            // ── Build a recipe-area-level lookup: areaType → level → list of (endProduct, count) ──
            Dictionary<int, Dictionary<int, List<(string product, int cnt)>>> recipeByAreaLevel = new();
            List<HideoutProduction>? recipes = _hideoutTable.Production?.Recipes;
            if (recipes is { Count: > 0 })
            {
                foreach (HideoutProduction recipe in recipes)
                {
                    if (recipe.AreaType is not { } recipeAreaType) continue;
                    int areaType = (int)recipeAreaType;
                    if (areaType < 0) continue;

                    // Find the highest "Area" requirement level for this recipe → the minimum area stage needed.
                    int minLevel = 1; // default: unlocked from level 1 (area exists)
                    if (recipe.Requirements is { Count: > 0 })
                    {
                        foreach (Requirement req in recipe.Requirements)
                        {
                            if (req.Type == "Area" && req.AreaType is { } raType && (int)raType == areaType && req.RequiredLevel is { } rl)
                                minLevel = Math.Max(minLevel, (int)rl);
                        }
                    }

                    if (!recipeByAreaLevel.TryGetValue(areaType, out var byLevel))
                    {
                        byLevel = new();
                        recipeByAreaLevel[areaType] = byLevel;
                    }
                    if (!byLevel.TryGetValue(minLevel, out var list))
                    {
                        list = new();
                        byLevel[minLevel] = list;
                    }
                    list.Add((recipe.EndProduct.ToString(), recipe.Count ?? 1));
                }
                _logger.Info($"QuestHandbook: recipe-area level lookup has {recipeByAreaLevel.Count} area types");
            }

            // ── Iterate hideout areas ──
            var areasProp = _hideoutTable.GetType().GetProperty("Areas");
            if (areasProp is null)
            {
                _logger.Error("QuestHandbook: 'Areas' property not found on HideoutTable");
                return payload;
            }
            var areas = areasProp.GetValue(_hideoutTable) as System.Collections.IEnumerable;
            if (areas is null)
            {
                _logger.Error("QuestHandbook: HideoutTable.Areas is null or not IEnumerable");
                return payload;
            }

            int areaCount = 0;
            foreach (object? areaObj in areas)
            {
                if (areaObj is null) continue;

                // HideoutArea.Type is an enum (HideoutAreas), not an int — must convert, not type-test.
                object? areaTypeVal = ReadMember(areaObj, "Type");
                if (areaTypeVal is null)
                {
                    _logger.Warning("QuestHandbook: area has no Type member, skipping");
                    continue;
                }
                int areaType = ToInt(areaTypeVal);
                if (areaType < 0)
                {
                    _logger.Warning($"QuestHandbook: area has invalid Type {areaType}, skipping");
                    continue;
                }

                object? enabledVal = ReadMember(areaObj, "Enabled");
                if (enabledVal is not null && !ToBool(enabledVal))
                {
                    _logger.Info($"QuestHandbook: area type {areaType} is disabled, skipping");
                    continue;
                }

                var stagesVal = ReadMember(areaObj, "Stages");
                if (stagesVal is null)
                {
                    _logger.Warning($"QuestHandbook: area type {areaType} has no Stages");
                    continue;
                }

                // Collect stage entries: try IDictionary (string-keyed), fall back to IList (int-indexed)
                var stageEntries = new List<(int level, object stageObj)>();

                if (stagesVal is System.Collections.IDictionary stagesDict)
                {
                    foreach (var rawKey in stagesDict.Keys)
                    {
                        if (int.TryParse(rawKey?.ToString(), out int l) && l > 0)
                        {
                            var val = stagesDict[rawKey];
                            if (val != null) stageEntries.Add((l, val));
                        }
                    }
                }
                else if (stagesVal is System.Collections.IList stagesList)
                {
                    for (int i = 0; i < stagesList.Count; i++)
                    {
                        var val = stagesList[i];
                        if (val != null)
                        {
                            // IList: index i → level i+1 (skip index 0 = initial state)
                            int level = i + 1;
                            if (level > 0) stageEntries.Add((level, val));
                        }
                    }
                }
                else
                {
                    _logger.Warning($"QuestHandbook: area type {areaType} Stages is neither IDictionary nor IList (type={stagesVal.GetType().Name})");
                    continue;
                }

                if (stageEntries.Count == 0)
                {
                    _logger.Info($"QuestHandbook: area type {areaType} has no stages (level > 0), skipping");
                    continue;
                }

                stageEntries.Sort((a, b) => a.level.CompareTo(b.level));

                ConstructionAreaData areaData = new() { AreaType = areaType };

                foreach (var (level, stageObj) in stageEntries)
                {
                    ConstructionStageData stageData = new() { Level = level };

                    stageData.ConstructionTime = ToDouble(ReadMember(stageObj, "ConstructionTime"));
                    stageData.Slots = ToInt(ReadMember(stageObj, "Slots"));

                    // ── Requirements ──
                    var reqsVal = ReadMember(stageObj, "Requirements");
                    if (reqsVal is System.Collections.IEnumerable reqs)
                    {
                        foreach (object? req in reqs)
                        {
                            if (req is null) continue;
                            string reqType = ToStr(ReadMember(req, "Type"));
                            var cr = new ConstructionRequirement { Type = reqType };

                            switch (reqType)
                            {
                                case "Item":
                                    cr.TemplateId = ToStr(ReadMember(req, "TemplateId"));
                                    cr.Count = ToInt(ReadMember(req, "Count"));
                                    cr.FoundInRaid = ToBool(ReadMember(req, "IsSpawnedInSession"));
                                    if (cr.TemplateId is { Length: >= 24 })
                                        cr.FleaPrice = GetFleaPrice(new MongoId(cr.TemplateId));
                                    break;
                                case "Area":
                                    cr.AreaType = ToInt(ReadMember(req, "AreaType"));
                                    cr.RequiredLevel = ToInt(ReadMember(req, "RequiredLevel"));
                                    break;
                                case "Skill":
                                    cr.SkillName = ToStr(ReadMember(req, "SkillName"));
                                    cr.SkillLevel = ToInt(ReadMember(req, "SkillLevel"));
                                    break;
                                case "TraderLoyalty":
                                    cr.TraderId = ToStr(ReadMember(req, "TraderId"));
                                    cr.LoyaltyLevel = ToInt(ReadMember(req, "LoyaltyLevel"));
                                    break;
                            }
                            stageData.Requirements.Add(cr);
                        }
                    }

                    // ── Bonuses ──
                    var bonusesVal = ReadMember(stageObj, "Bonuses");
                    if (bonusesVal is System.Collections.IEnumerable bonuses)
                    {
                        foreach (object? bonus in bonuses)
                        {
                            if (bonus is null) continue;
                            object? visibleVal = ReadMember(bonus, "Visible");
                            stageData.Bonuses.Add(new ConstructionBonus
                            {
                                Id = ToStr(ReadMember(bonus, "Id")),
                                Type = ToStr(ReadMember(bonus, "Type")),
                                Value = ToDouble(ReadMember(bonus, "Value")),
                                Visible = visibleVal is null || ToBool(visibleVal),
                            });
                        }
                    }

                    // ── Recipes unlocked at this level ──
                    if (recipeByAreaLevel.TryGetValue(areaType, out var byLevel)
                        && byLevel.TryGetValue(level, out var unlocked))
                    {
                        stageData.UnlockedRecipeIds = unlocked.Select(u => u.product).ToList();
                        stageData.UnlockedRecipeCount = unlocked.Count;
                    }

                    areaData.Stages.Add(stageData);
                }

                payload.Areas.Add(areaData);
                areaCount++;
            }

            payload.Areas.Sort((a, b) => a.AreaType.CompareTo(b.AreaType));
            _logger.Info($"QuestHandbook: construction payload built with {payload.Areas.Count} areas (processed {areaCount})");
            return payload;
        }

        /// <summary>Reads a member (field-first, then property) from an object via reflection.</summary>
        private static object? ReadMember(object target, string name)
        {
            try
            {
                var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null) return field.GetValue(target);
                var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return prop?.GetValue(target);
            }
            catch { return null; }
        }

        /// <summary>Converts a boxed member to int; handles enums and nullable wrappers.</summary>
        private static int ToInt(object? value)
        {
            if (value is null) return 0;
            try { return Convert.ToInt32(value); } catch { return 0; }
        }

        /// <summary>Converts a boxed member to double; handles nullable wrappers.</summary>
        private static double ToDouble(object? value)
        {
            if (value is null) return 0d;
            try { return Convert.ToDouble(value); } catch { return 0d; }
        }

        /// <summary>Converts a boxed member to bool; handles nullable wrappers.</summary>
        private static bool ToBool(object? value)
        {
            if (value is null) return false;
            try { return Convert.ToBoolean(value); } catch { return false; }
        }

        /// <summary>Converts a boxed member to string; handles enums by returning their name.</summary>
        private static string ToStr(object? value)
        {
            if (value is null) return "";
            return value as string ?? value.ToString() ?? "";
        }

        /// <summary>Flea market price for a template; 0 when the template is unknown to the price service.</summary>
        private double GetFleaPrice(MongoId templateId)
        {
            try
            {
                string id = templateId.ToString();
                if (string.IsNullOrEmpty(id) || id.Length < 24) return 0;
                return Math.Round(_ragfairPriceService.GetFleaPriceForItem(templateId), 0);
            }
            catch
            {
                return 0;
            }
        }

        private QuestBookPayload BuildPayload()
        {
            List<Quest> allQuests = _questHelper.GetQuestsFromDb();
            Dictionary<MongoId, List<Quest>> questsByTrader = [];
            foreach (Quest quest in allQuests)
            {
                if (string.IsNullOrEmpty(quest.TraderId.ToString())) continue;
                if (!questsByTrader.TryGetValue(quest.TraderId, out List<Quest>? list))
                {
                    list = [];
                    questsByTrader.Add(quest.TraderId, list);
                }
                list.Add(quest);
            }

            List<string> orderedTraderIds = [];
            List<MongoId> known = [];
            foreach (FieldInfo traderField in typeof(Traders).GetFields(BindingFlags.Static | BindingFlags.Public))
            {
                if (traderField.GetValue(null) is not MongoId traderId || !questsByTrader.ContainsKey(traderId)) continue;
                known.Add(traderId);
                orderedTraderIds.Add(traderId.ToString());
            }

            List<MongoId> unknown = questsByTrader.Keys.Where(id => !known.Contains(id)).ToList();
            unknown.Sort((a, b) => StringComparer.Ordinal.Compare(GetTraderSortName(a), GetTraderSortName(b)));
            orderedTraderIds.AddRange(unknown.Select(id => id.ToString()));

            Dictionary<MongoId, Dictionary<MongoId, MongoId>> assortTplByTrader = BuildAssortTplLookup();
            List<QuestBookQuest> payloadQuests = [];

            foreach (MongoId traderId in known.Concat(unknown))
            {
                foreach (Quest quest in questsByTrader[traderId])
                {
                    MongoId questId = quest.Id;
                    List<string> prereqs = [];
                    var startConditions = quest.Conditions?.AvailableForStart;
                    if (startConditions is { Count: > 0 })
                    {
                        foreach (QuestCondition condition in startConditions)
                        {
                            if (condition.ConditionType == "Quest")
                            {
                                string? target = condition.Target?.Item;
                                if (!string.IsNullOrEmpty(target) && prereqs.IndexOf(target) < 0) prereqs.Add(target);
                            }
                        }
                    }

                    int minLevel = 0, loyaltyLevel = 0;
                    if (startConditions is { Count: > 0 })
                    {
                        foreach (QuestCondition cond in startConditions)
                        {
                            if (cond.ConditionType == "Level") minLevel = (int)(cond.Value ?? 0);
                            else if (cond.ConditionType == "TraderLoyalty") loyaltyLevel = (int)(cond.Value ?? 0);
                        }
                    }

                    payloadQuests.Add(new QuestBookQuest
                    {
                        Id = questId.ToString(),
                        Name = quest.Name,
                        QuestName = quest.QuestName,
                        TraderId = quest.TraderId.ToString(),
                        Image = ResolveQuestImage(quest),
                        PreReqs = prereqs,
                        Unlocks = BuildQuestUnlocks(questId, assortTplByTrader),
                        MinLevel = minLevel,
                        LoyaltyLevel = loyaltyLevel,
                        Objectives = BuildQuestObjectives(quest),
                        Rewards = BuildQuestRewards(quest),
                    });
                }
            }

            return new QuestBookPayload { Traders = orderedTraderIds, Quests = payloadQuests };
        }

        private Dictionary<MongoId, Dictionary<MongoId, MongoId>> BuildAssortTplLookup()
        {
            var result = new Dictionary<MongoId, Dictionary<MongoId, MongoId>>();
            foreach ((MongoId traderId, Trader trader) in _tradersTable)
            {
                if (trader?.Assort?.Items is not { Count: > 0 } items) continue;
                var tplById = new Dictionary<MongoId, MongoId>();
                foreach (Item item in items)
                {
                    if (!string.IsNullOrEmpty(item.Template.ToString())) tplById[item.Id] = item.Template;
                }
                if (tplById.Count > 0) result[traderId] = tplById;
            }
            return result;
        }

        private List<QuestBookUnlock> BuildQuestUnlocks(MongoId questId, Dictionary<MongoId, Dictionary<MongoId, MongoId>> assortTplByTrader)
        {
            var unlocks = new List<QuestBookUnlock>();
            var seen = new HashSet<string>();
            foreach ((MongoId traderId, Trader trader) in _tradersTable)
            {
                if (trader?.QuestAssort is not { Count: > 0 } questAssort) continue;
                if (!assortTplByTrader.TryGetValue(traderId, out Dictionary<MongoId, MongoId>? tplById)) continue;

                foreach ((string statusKey, Dictionary<MongoId, MongoId> assortUnlocks) in questAssort)
                {
                    if (statusKey is null || !IsUnlockStatus(statusKey)) continue;
                    foreach ((MongoId assortId, MongoId unlockedByQuest) in assortUnlocks)
                    {
                        if (unlockedByQuest != questId || !tplById.TryGetValue(assortId, out MongoId templateId)) continue;
                        string dedupeKey = $"{traderId}|{templateId}";
                        if (!seen.Add(dedupeKey)) continue;
                        unlocks.Add(new QuestBookUnlock { TraderId = traderId.ToString(), TemplateId = templateId.ToString() });
                    }
                }
            }
            unlocks.Sort((a, b) => StringComparer.Ordinal.Compare(a.TraderId, b.TraderId));
            return unlocks;
        }

        private string? ResolveQuestImage(Quest quest)
        {
            try
            {
                Type type = quest.GetType();
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (string.Equals(property.Name, "Image", StringComparison.OrdinalIgnoreCase))
                    {
                        return property.GetValue(quest) as string;
                    }
                }
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (string.Equals(field.Name, "Image", StringComparison.OrdinalIgnoreCase))
                    {
                        return field.GetValue(quest) as string;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"QuestHandbook: failed to read quest image: {ex.Message}");
            }
            return null;
        }

        private List<QuestBookObjective> BuildQuestObjectives(Quest quest)
        {
            var result = new List<QuestBookObjective>();
            var finishCondsProp = quest.Conditions?.GetType().GetProperty("AvailableForFinish");
            var finishConds = finishCondsProp?.GetValue(quest.Conditions) as IEnumerable<object>;
            if (finishConds is null) return result;

            foreach (var cond in finishConds)
            {
                string ct = GetConditionType(cond);
                double val = GetConditionValue(cond);
                bool fir = GetConditionFIR(cond);
                string? locale = GetConditionLocale(cond);

                switch (ct)
                {
                    case "HandoverItem":
                        result.Add(new QuestBookObjective { Type = "HandoverItem", Target = ExtractTarget(GetConditionTarget(cond)), Value = val, OnlyFoundInRaid = fir, Locale = locale });
                        break;
                    case "FindItem":
                        result.Add(new QuestBookObjective { Type = "FindItem", Target = ExtractTarget(GetConditionTarget(cond)), Value = val > 0 ? val : 1, Locale = locale });
                        break;
                    case "LeaveItemAtLocation":
                        result.Add(new QuestBookObjective { Type = "LeaveItem", Target = ExtractTarget(GetConditionTarget(cond)), Value = val > 0 ? val : 1, Locale = locale });
                        break;
                    case "CounterCreator":
                    {
                        var counterObj = GetConditionCounter(cond);
                        if (counterObj == null) break;
                        var subProp = counterObj.GetType().GetProperty("Conditions");
                        var subs = subProp?.GetValue(counterObj) as IEnumerable<object>;
                        if (subs is null) break;

                        string subType = GetConditionSubType(cond);
                        if (subType == "Elimination")
                        {
                            string? botTarget = null;
                            double killCount = val > 0 ? val : 1;
                            foreach (var sub in subs)
                            {
                                if (GetConditionType(sub) == "Kills")
                                {
                                    killCount = GetConditionValue(sub) > 0 ? GetConditionValue(sub) : killCount;
                                    var tgt = ExtractTarget(GetConditionTarget(sub));
                                    if (tgt.Count > 0) botTarget = tgt[0];
                                    break;
                                }
                            }
                            result.Add(new QuestBookObjective { Type = "Kill", Target = botTarget != null ? new List<string> { botTarget } : [], Value = killCount });
                        }
                        else if (subType == "Completion")
                        {
                            foreach (var sub in subs)
                            {
                                string st = GetConditionType(sub);
                                if (st == "VisitPlace")
                                {
                                    string? zone = GetConditionZoneId(sub);
                                    if (!string.IsNullOrEmpty(zone))
                                        result.Add(new QuestBookObjective { Type = "Visit", Target = new List<string> { zone }, Value = 1 });
                                }
                                else if (st == "Location")
                                {
                                    var locTargets = ExtractTarget(GetConditionTarget(sub));
                                    if (locTargets.Count > 0)
                                        result.Add(new QuestBookObjective { Type = "Location", Target = locTargets, Value = 1 });
                                }
                                else if (st == "ExitStatus")
                                {
                                    string? status = GetConditionStatus(sub);
                                    if (!string.IsNullOrEmpty(status))
                                        result.Add(new QuestBookObjective { Type = "Extract", Target = new List<string> { status }, Value = 1 });
                                }
                            }
                        }
                        break;
                    }
                    default:
                        result.Add(new QuestBookObjective { Type = ct, Target = ExtractTarget(GetConditionTarget(cond)), Value = val > 0 ? val : 1 });
                        break;
                }
            }
            return result;
        }

        private List<QuestBookReward> BuildQuestRewards(Quest quest)
        {
            var result = new List<QuestBookReward>();
            var rewardsDict = quest.Rewards;
            if (rewardsDict == null) return result;

            var enumerator = rewardsDict.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var entry = enumerator.Current;
                var kvpType = entry.GetType();
                string stage = "";
                var keyProp = kvpType.GetProperty("Key");
                if (keyProp != null)
                {
                    var keyValue = keyProp.GetValue(entry);
                    if (keyValue != null) stage = keyValue.ToString() ?? "";
                }
                var valueProp = kvpType.GetProperty("Value");
                var rewardsList = valueProp?.GetValue(entry) as IEnumerable<object>;
                if (rewardsList == null) continue;

                foreach (var rawReward in rewardsList)
                {
                    string rewardType = "";
                    try { rewardType = (rawReward.GetType().GetProperty("Type")?.GetValue(rawReward)?.ToString()) ?? ""; } catch { }

                    switch (rewardType)
                    {
                        case "Experience":
                        {
                            double expVal = 0;
                            try { expVal = Convert.ToDouble(rawReward.GetType().GetProperty("Value")?.GetValue(rawReward) ?? 0d); } catch { }
                            result.Add(new QuestBookReward { Type = "Experience", Value = expVal, Stage = stage });
                            break;
                        }
                        case "TraderStanding":
                        {
                            string? traderId = "";
                            try { traderId = (rawReward.GetType().GetProperty("Target")?.GetValue(rawReward) as string) ?? (rawReward.GetType().GetProperty("TraderId")?.GetValue(rawReward)?.ToString()); } catch { }
                            if (string.IsNullOrEmpty(traderId)) break;
                            double sv = 0;
                            try
                            {
                                var rv = rawReward.GetType().GetProperty("Value")?.GetValue(rawReward);
                                if (rv is decimal dc) sv = Convert.ToDouble(dc);
                                else if (rv is double d) sv = d;
                                else if (rv is int i) sv = i;
                            }
                            catch { }
                            result.Add(new QuestBookReward { Type = "TraderStanding", Value = Math.Round(sv, 2), Target = traderId, Stage = stage });
                            break;
                        }
                        case "Item":
                        {
                            var itemInfos = CollectRewardItems(reflection: true, rawReward);
                            if (itemInfos.Count > 0)
                                result.Add(new QuestBookReward { Type = "Item", Items = itemInfos, Stage = stage });
                            break;
                        }
                        case "Skill":
                        {
                            string? skillName = "";
                            try { skillName = rawReward.GetType().GetProperty("Target")?.GetValue(rawReward) as string; } catch { }
                            if (string.IsNullOrEmpty(skillName)) break;
                            double sv = 0;
                            try { sv = Convert.ToDouble(rawReward.GetType().GetProperty("Value")?.GetValue(rawReward) ?? 0d); } catch { }
                            result.Add(new QuestBookReward { Type = "Skill", Value = sv, Target = skillName, Stage = stage });
                            break;
                        }
                        case "TraderUnlock":
                        {
                            string? tId = "";
                            try { tId = rawReward.GetType().GetProperty("Target")?.GetValue(rawReward) as string; } catch { }
                            if (string.IsNullOrEmpty(tId)) break;
                            result.Add(new QuestBookReward { Type = "TraderUnlock", Target = tId, Stage = stage });
                            break;
                        }
                        case "ProductionScheme":
                        {
                            string? target = "";
                            try { target = rawReward.GetType().GetProperty("Target")?.GetValue(rawReward) as string; } catch { }
                            if (!string.IsNullOrEmpty(target))
                                result.Add(new QuestBookReward { Type = "HideoutRecipe", Target = target, Stage = stage });
                            break;
                        }
                    }
                }
            }
            return result;
        }

        private static List<RewardItemInfo> CollectRewardItems(bool reflection = false, object? rawReward = null)
        {
            var items = new List<RewardItemInfo>();
            object? rewardItemsList = null;
            if (!reflection && rawReward is Reward reward) rewardItemsList = reward.Items;
            else if (reflection && rawReward != null)
            {
                try { rewardItemsList = rawReward.GetType().GetProperty("Items")?.GetValue(rawReward); } catch { }
            }
            var itemList = rewardItemsList as IEnumerable<object>;
            if (itemList == null) return items;

            foreach (var itemObj in itemList)
            {
                string? tplId = "";
                try { tplId = itemObj.GetType().GetProperty("Template")?.GetValue(itemObj)?.ToString(); } catch { }
                if (string.IsNullOrEmpty(tplId)) continue;

                int count = 1;
                if (!reflection)
                {
                    var upd = itemObj.GetType().GetProperty("Upd")?.GetValue(itemObj);
                    if (upd != null)
                    {
                        var soc = upd.GetType().GetProperty("StackObjectsCount")?.GetValue(upd) as int?;
                        if (soc.HasValue && soc.Value > 1) count = soc.Value;
                    }
                }
                else
                {
                    try
                    {
                        var updObj = itemObj.GetType().GetProperty("Upd")?.GetValue(itemObj);
                        if (updObj != null)
                        {
                            var socObj = updObj.GetType().GetProperty("StackObjectsCount")?.GetValue(updObj) as int?;
                            if (socObj.HasValue && socObj.Value > 1) count = socObj.Value;
                        }
                    }
                    catch { }
                }
                items.Add(new RewardItemInfo { TemplateId = tplId!, Count = count });
            }
            return items;
        }

        private static List<string> ExtractTarget(object? target)
        {
            if (target is null) return [];
            try
            {
                var itemProp = target.GetType().GetProperty("Item");
                if (itemProp != null)
                {
                    string? item = itemProp.GetValue(target) as string;
                    if (!string.IsNullOrEmpty(item)) return [item];
                }
                var listProp = target.GetType().GetProperty("List");
                if (listProp?.GetValue(target) is System.Collections.IList list)
                {
                    var result = new List<string>();
                    foreach (var e in list)
                    {
                        if (e is string s && !string.IsNullOrEmpty(s)) result.Add(s);
                    }
                    if (result.Count > 0) return result;
                }
            }
            catch { }
            return [];
        }

        private static string GetConditionType(object cond)
        {
            try
            {
                var p = cond.GetType().GetProperty("ConditionType");
                return p?.GetValue(cond) as string ?? "";
            }
            catch { return ""; }
        }

        private static string GetConditionSubType(object cond)
        {
            try
            {
                var p = cond.GetType().GetProperty("Type");
                return p?.GetValue(cond) as string ?? "";
            }
            catch { return ""; }
        }

        private static double GetConditionValue(object cond)
        {
            try
            {
                var p = cond.GetType().GetProperty("Value");
                object? v = p?.GetValue(cond);
                return v switch { double d => d, int i => i, _ => 0 };
            }
            catch { return 0; }
        }

        private static bool GetConditionFIR(object cond)
        {
            try
            {
                var p = cond.GetType().GetProperty("OnlyFoundInRaid");
                object? v = p?.GetValue(cond);
                if (v is bool b) return b;
                var nullable = v as bool?;
                if (nullable.HasValue) return nullable.Value;
                return false;
            }
            catch { return false; }
        }

        private static object? GetConditionTarget(object cond)
        {
            try { return cond.GetType().GetProperty("Target")?.GetValue(cond); }
            catch { return null; }
        }

        private static object? GetConditionCounter(object cond)
        {
            try { return cond.GetType().GetProperty("Counter")?.GetValue(cond); }
            catch { return null; }
        }

        private static string? GetConditionZoneId(object cond)
        {
            try { return cond.GetType().GetProperty("ZoneId")?.GetValue(cond) as string; }
            catch { return null; }
        }

        private static string? GetConditionStatus(object cond)
        {
            try { return cond.GetType().GetProperty("Status")?.GetValue(cond) as string; }
            catch { return null; }
        }

        private static bool IsUnlockStatus(string statusKey)
        {
            string lowered = statusKey.ToLowerInvariant();
            return lowered.Contains("started") || lowered.Contains("success");
        }

        private string GetTraderSortName(MongoId traderId)
        {
            if (_tradersTable.TryGetValue(traderId, out Trader? trader) && trader?.Base is { } traderBase)
            {
                return traderBase.Nickname ?? traderBase.Name ?? traderId.ToString();
            }
            return traderId.ToString();
        }

        /// <summary>
        /// Extracts the locale field from a quest condition for localization.
        /// </summary>
        private static string? GetConditionLocale(object cond)
        {
            try
            {
                var localeProp = cond.GetType().GetProperty("Locale");
                if (localeProp != null)
                {
                    var localeVal = localeProp.GetValue(cond);
                    if (localeVal != null) return localeVal.ToString();
                }

                // Try "locale" field (lowercase)
                var localeField = cond.GetType().GetField("locale");
                if (localeField != null)
                {
                    var localeVal = localeField.GetValue(cond);
                    if (localeVal != null) return localeVal.ToString();
                }
            }
            catch
            {
                // Ignore errors
            }

            return null;
        }
    }

    internal sealed class QuestBookPayload
    {
        [JsonPropertyName("traders")] public List<string> Traders { get; init; } = [];
        [JsonPropertyName("quests")] public List<QuestBookQuest> Quests { get; init; } = [];
    }

    internal sealed class QuestBookQuest
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("questName")] public string? QuestName { get; init; }
        [JsonPropertyName("image")] public string? Image { get; init; }
        [JsonPropertyName("traderId")] public string TraderId { get; init; } = "";
        [JsonPropertyName("prereqs")] public List<string> PreReqs { get; init; } = [];
        [JsonPropertyName("unlocks")] public List<QuestBookUnlock> Unlocks { get; init; } = [];
        [JsonPropertyName("minLevel")] public int MinLevel { get; init; }
        [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; init; }
        [JsonPropertyName("objectives")] public List<QuestBookObjective> Objectives { get; init; } = [];
        [JsonPropertyName("rewards")] public List<QuestBookReward> Rewards { get; init; } = [];
    }

    internal sealed class QuestBookUnlock
    {
        [JsonPropertyName("traderId")] public string TraderId { get; init; } = "";
        [JsonPropertyName("templateId")] public string TemplateId { get; init; } = "";
    }

    internal sealed class QuestBookObjective
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("target")] public List<string> Target { get; init; } = [];
        [JsonPropertyName("value")] public double Value { get; init; }
        [JsonPropertyName("onlyFoundInRaid")] public bool OnlyFoundInRaid { get; init; }
        [JsonPropertyName("locale")] public string? Locale { get; init; }
    }

    internal sealed class QuestBookReward
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("value")] public double Value { get; init; }
        [JsonPropertyName("target")] public string? Target { get; init; }
        [JsonPropertyName("items")] public List<RewardItemInfo> Items { get; init; } = [];
        [JsonPropertyName("stage")] public string? Stage { get; init; }
    }

    internal sealed class RewardItemInfo
    {
        [JsonPropertyName("templateId")] public string TemplateId { get; init; } = "";
        [JsonPropertyName("count")] public int Count { get; init; }
    }

    internal sealed class RecipeBookPayload
    {
        [JsonPropertyName("recipes")] public List<RecipeBookRecipe> Recipes { get; init; } = [];
    }

    internal sealed class RecipeBookRecipe
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("areaType")] public int AreaType { get; init; }
        [JsonPropertyName("areaName")] public string AreaName { get; init; } = "";
        [JsonPropertyName("endProduct")] public string EndProduct { get; init; } = "";
        [JsonPropertyName("count")] public int Count { get; init; }
        [JsonPropertyName("productionTime")] public int ProductionTime { get; init; }
        [JsonPropertyName("continuous")] public bool Continuous { get; init; }
        [JsonPropertyName("locked")] public bool Locked { get; init; }
        [JsonPropertyName("isEncoded")] public bool Encoded { get; init; }
        [JsonPropertyName("needFuelForAllProductionTime")] public bool NeedFuel { get; init; }
        [JsonPropertyName("productionLimitCount")] public int ProductionLimit { get; init; }
        [JsonPropertyName("productValue")] public double ProductValue { get; init; }
        [JsonPropertyName("fleaPrice")] public double FleaPrice { get; init; }
        [JsonPropertyName("requirements")] public List<RecipeBookRequirement> Requirements { get; init; } = [];
    }

    internal sealed class RecipeBookRequirement
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("templateId")] public string TemplateId { get; init; } = "";
        [JsonPropertyName("count")] public int Count { get; init; }
        [JsonPropertyName("isSpawnedInSession")] public bool FoundInRaid { get; init; }
        [JsonPropertyName("isFunctional")] public bool Functional { get; init; }
        [JsonPropertyName("requiredLevel")] public int RequiredLevel { get; init; }
        [JsonPropertyName("resource")] public int Resource { get; init; }
        [JsonPropertyName("questId")] public string? QuestId { get; init; }
        [JsonPropertyName("fleaPrice")] public double FleaPrice { get; init; }
    }

    // ── Construction Book payload data classes ──

    internal sealed class ConstructionBookPayload
    {
        [JsonPropertyName("areas")] public List<ConstructionAreaData> Areas { get; set; } = [];
    }

    internal sealed class ConstructionAreaData
    {
        [JsonPropertyName("areaType")] public int AreaType { get; set; }
        [JsonPropertyName("stages")] public List<ConstructionStageData> Stages { get; set; } = [];
    }

    internal sealed class ConstructionStageData
    {
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("constructionTime")] public double ConstructionTime { get; set; }
        [JsonPropertyName("slots")] public int Slots { get; set; }
        [JsonPropertyName("requirements")] public List<ConstructionRequirement> Requirements { get; set; } = [];
        [JsonPropertyName("bonuses")] public List<ConstructionBonus> Bonuses { get; set; } = [];
        [JsonPropertyName("unlockedRecipeIds")] public List<string> UnlockedRecipeIds { get; set; } = [];
        [JsonPropertyName("unlockedRecipeCount")] public int UnlockedRecipeCount { get; set; }
    }

    internal sealed class ConstructionRequirement
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("templateId")] public string? TemplateId { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("foundInRaid")] public bool FoundInRaid { get; set; }
        [JsonPropertyName("areaType")] public int AreaType { get; set; }
        [JsonPropertyName("requiredLevel")] public int RequiredLevel { get; set; }
        [JsonPropertyName("skillName")] public string? SkillName { get; set; }
        [JsonPropertyName("skillLevel")] public int SkillLevel { get; set; }
        [JsonPropertyName("traderId")] public string? TraderId { get; set; }
        [JsonPropertyName("loyaltyLevel")] public int LoyaltyLevel { get; set; }
        [JsonPropertyName("fleaPrice")] public double FleaPrice { get; set; }
    }

    internal sealed class ConstructionBonus
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("value")] public double Value { get; set; }
        [JsonPropertyName("visible")] public bool Visible { get; set; }
    }
}

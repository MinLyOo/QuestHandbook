using EFT.HandBook;
using EFT.InventoryLogic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestHandbook.HandBook
{
    internal static class QuestBookUGUI
    {
        private static QuestBookHost? _host;

        public static void OnHandbookOpened(HandbookScreen screen)
        {
            try
            {
                if (screen is null) return;
                Plugin.LogSource?.LogInfo($"QBUI opened: {screen.name} active={screen.gameObject.activeInHierarchy}");
                if (_host != null)
                {
                    if (_host.IsSameScreen(screen)) { Plugin.LogSource?.LogInfo("QBUI same screen, keep"); return; }
                    _host.DestroyAll(); _host = null;
                }
                if (!QuestBookData.IsLoaded) QuestBookData.TryEnsureLoaded();
                if (!RecipeBookData.IsLoaded) RecipeBookData.TryEnsureLoaded();
                QuestHandbook.Diagnostics.HandbookProbe.ScheduleIfNeeded();
                QuestBookHost? existing = screen.gameObject.GetComponent<QuestBookHost>();
                _host = existing != null ? existing : screen.gameObject.AddComponent<QuestBookHost>();
                _host.Init(screen);
                Plugin.LogSource?.LogInfo(existing != null ? "QBUI host reused" : "QBUI host added");
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI open failed: {ex}"); }
        }

        public static void OnHandbookClosed()
        {
            try { _host?.DestroyAll(); }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI close failed: {ex}"); }
            finally { _host = null; }
        }

        // ---- virtual Handbook nodes -------------------------------------------
        internal const string BookRootId = "QHBook";
        internal const string BookAllId = "QHBook_All";
        internal const string RecipeRootId = "QHRecipe";
        internal const string ConstructionRootId = "QHBuild";
        private const string BookIconHide = "/files/handbook/";
        private const string BookQuestIcon = "/files/handbook/wishlist_quests.png";
        private const string BookRecipeIcon = "/files/handbook/wishlist_hideout.png";
        private const string BookConstructionIcon = "/files/handbook/icon_barter_building.png";
        private static Item? _standIn;
        private static readonly HashSet<Item> NoIconItems = new();
        private static readonly Dictionary<string, QuestBookData.Quest> QuestByNode = new();
        private static readonly Dictionary<string, string> LeafIdByQuestId = new();
        private static readonly Dictionary<string, RecipeBookData.Recipe> RecipeByNode = new();
        private static readonly Dictionary<string, Item> ItemByTemplate = new();
        private static readonly Dictionary<string, HandbookNode> NodeById = new();

        internal static bool IsBookNode(HandbookNode? node)
        { for (HandbookNode? n = node; n != null; n = n.Parent) { if (n.Data != null && n.Data.Id == BookRootId) return true; } return false; }
        internal static bool IsQuestNode(HandbookNode? node) { return node?.Data != null && node.Data.Type == ENodeType.Item && IsBookNode(node); }
        internal static bool IsRecipeBookNode(HandbookNode? node)
        { for (HandbookNode? n = node; n != null; n = n.Parent) { if (n.Data != null && n.Data.Id == RecipeRootId) return true; } return false; }
        internal static bool IsRecipeNode(HandbookNode? node) { return node?.Data != null && node.Data.Type == ENodeType.Item && IsRecipeBookNode(node); }
        internal static bool IsConstructionBookNode(HandbookNode? node)
        { for (HandbookNode? n = node; n != null; n = n.Parent) { if (n.Data != null && n.Data.Id == ConstructionRootId) return true; } return false; }
        internal static bool IsConstructionNode(HandbookNode? node) { return node?.Data != null && node.Data.Type == ENodeType.Item && IsConstructionBookNode(node); }
        internal static bool IsHiddenIconItem(Item? item) { return item != null && NoIconItems.Contains(item); }
        internal static QuestBookData.Quest? FindQuestByNode(HandbookNode node) { if (node?.Data == null) return null; return QuestByNode.TryGetValue(node.Data.Id, out var quest) ? quest : null; }
        internal static RecipeBookData.Recipe? FindRecipeByNode(HandbookNode node) { if (node?.Data == null) return null; return RecipeByNode.TryGetValue(node.Data.Id, out var recipe) ? recipe : null; }

        /// <summary>Handbook node that represents a quest leaf; used to drive native navigation.</summary>
        internal static HandbookNode? FindQuestLeaf(string questId)
        {
            if (string.IsNullOrEmpty(questId) || !LeafIdByQuestId.TryGetValue(questId, out string? leafId)) return null;
            return leafId != null && NodeById.TryGetValue(leafId, out var node) ? node : null;
        }

        private static int CountQuestsOfTrader(string traderId) { int c = 0; foreach (var q in QuestBookData.All) { if (q.TraderId == traderId) c++; } return c; }
        private static HandbookNode CreateBookCategory(string id, string name) => new() { Data = new HandbookData { Id = id, ParentId = null, Name = name, Type = ENodeType.Category, Icon = BookIconHide, Color = "", FromBuild = true }, Depth = 0, Count = 0, ChildrenDict = new(), };

        public static void EnsureBookNodes(EFT.HandBook.Handbook handbook)
        {
            if (handbook?.EncyclopediaNodes is null) return;
            try
            {
                if (!QuestBookData.IsLoaded) QuestBookData.TryEnsureLoaded();
                var enc = handbook.EncyclopediaNodes;
                if (!enc.TryGetValue(BookRootId, out var root) || root?.Data == null || root.Data.Type != ENodeType.Category) { root = CreateBookCategory(BookRootId, "任务书"); enc[BookRootId] = root; }
                root.Data.Icon = BookQuestIcon;
                if (_standIn == null) { _standIn = ResolveStandIn(enc); if (_standIn != null) { NoIconItems.Add(_standIn); Plugin.LogSource?.LogInfo($"QBUI stand-in: {_standIn.TemplateId}"); } }

                var wanted = new List<string>();
                if (QuestBookData.IsLoaded) { wanted.Add(BookAllId); foreach (var tid in QuestBookData.TraderOrder) { if (!string.IsNullOrEmpty(tid) && CountQuestsOfTrader(tid) > 0) wanted.Add(tid); } }
                root.ChildrenDict.Clear(); QuestByNode.Clear(); LeafIdByQuestId.Clear(); NodeById.Clear();
                NodeById[BookRootId] = root;
                if (QuestBookData.IsLoaded) { int gi = 0; foreach (var q in QuestBookData.All) { if (q.Id != null) { LeafIdByQuestId[q.Id] = $"QHq{gi:D5}"; gi++; } } }
                foreach (var id in wanted) { var all = id == BookAllId; var child = CreateBookCategory(id, all ? "全部" : QuestBookData.ResolveTraderName(id)); child.Parent = root; child.Depth = root.Depth + 1; var quests = new List<QuestBookData.Quest>(); foreach (var q in QuestBookData.All) { if (all || q.TraderId == id) quests.Add(q); } if (quests.Count > 0 && _standIn != null) RebuildQuestChildren(child, quests); root.ChildrenDict[id] = child; NodeById[id] = child; }

                var total = QuestBookData.IsLoaded ? QuestBookData.All.Count : 0;
                foreach (var id in wanted) { if (!root.ChildrenDict.TryGetValue(id, out var c) || c == null) continue; int count = id == BookAllId ? total : CountQuestsOfTrader(id); c.SetChildrenCount(Math.Max(1, count)); }
                root.SetChildrenCount(Math.Max(1, wanted.Count));

                EnsureRecipeNodes(enc);
                EnsureConstructionNodes(enc);
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI injection failed: {ex}"); }
        }

        /// <summary>Injects the hideout recipe book (facilities + craftable items) into the native handbook tree.</summary>
        private static void EnsureRecipeNodes(EFT.HandBook.HandbookNodes enc)
        {
            if (!RecipeBookData.IsLoaded) RecipeBookData.TryEnsureLoaded();
            if (!enc.TryGetValue(RecipeRootId, out var root) || root?.Data == null || root.Data.Type != ENodeType.Category)
            {
                root = CreateBookCategory(RecipeRootId, "配方书");
                enc[RecipeRootId] = root;
            }
            root.Data.Icon = BookRecipeIcon;
            root.ChildrenDict.Clear();
            RecipeByNode.Clear();
            NodeById[RecipeRootId] = root;
            if (!RecipeBookData.IsLoaded) { root.SetChildrenCount(1); return; }

            EnsureItemLookup(enc);
            if (_standIn == null && ItemByTemplate.Count > 0) { _standIn = ItemByTemplate.Values.First(); NoIconItems.Add(_standIn); }

            int areaIndex = 0;
            int recipeIndex = 0;
            var areas = RecipeBookData.GroupByArea();
            foreach (var area in areas)
            {
                string areaId = $"QHRa{area.Key:D3}";
                string name = $"{RecipeBookData.ResolveAreaName(area.Key)}（{area.Value.Count}）";
                var child = CreateBookCategory(areaId, name);
                child.Parent = root;
                child.Depth = root.Depth + 1;

                foreach (RecipeBookData.Recipe recipe in area.Value)
                {
                    string leafId = $"QHr{recipeIndex:D5}";
                    recipeIndex++;

                    Item? product = null;
                    if (!string.IsNullOrEmpty(recipe.EndProduct)) ItemByTemplate.TryGetValue(recipe.EndProduct, out product);
                    if (product == null) product = _standIn;

                    var leaf = new HandbookNode
                    {
                        Data = new HandbookData
                        {
                            Id = leafId,
                            ParentId = null,
                            Name = RecipeBookData.ResolveItemName(recipe.EndProduct),
                            Type = ENodeType.Item,
                            Icon = "",
                            Color = "",
                            FromBuild = true,
                            Item = product,
                        },
                        Depth = child.Depth + 1,
                        Count = 0,
                        Category = new[] { RecipeBookData.ResolveAreaName(area.Key) },
                        ChildrenDict = new(),
                    };
                    leaf.Parent = child;
                    child.ChildrenDict[leafId] = leaf;
                    RecipeByNode[leafId] = recipe;
                    NodeById[leafId] = leaf;
                }

                child.SetChildrenCount(Math.Max(1, area.Value.Count));
                root.ChildrenDict[areaId] = child;
                NodeById[areaId] = child;
                areaIndex++;
            }

            root.SetChildrenCount(Math.Max(1, areaIndex));
            Plugin.LogSource?.LogInfo($"QBUI recipe book: {recipeIndex} recipes in {areaIndex} facilities");
        }

        /// <summary>Injects the hideout construction / upgrade book into the native handbook tree.</summary>
        private static void EnsureConstructionNodes(EFT.HandBook.HandbookNodes enc)
        {
            if (!ConstructionBookData.IsLoaded) ConstructionBookData.TryEnsureLoaded();
            if (!enc.TryGetValue(ConstructionRootId, out var root) || root?.Data == null || root.Data.Type != ENodeType.Category)
            {
                root = CreateBookCategory(ConstructionRootId, "建造书");
                enc[ConstructionRootId] = root;
            }
            root.Data.Icon = BookConstructionIcon;
            root.ChildrenDict.Clear();
            NodeById[ConstructionRootId] = root;
            if (!ConstructionBookData.IsLoaded) { root.SetChildrenCount(1); return; }

            var areas = ConstructionBookData.GetOrderedAreas();
            int areaIndex = 0;

            foreach (var area in areas)
            {
                string areaId = $"QHBa{area.AreaType:D3}";
                string name = $"{area.DisplayName}（{area.Stages.Count}）";
                var child = CreateBookCategory(areaId, name);
                child.Parent = root;
                child.Depth = root.Depth + 1;

                foreach (var stage in area.Stages)
                {
                    string leafId = $"QHBs{area.AreaType:D3}_{stage.Level:D2}";
                    string stageName = stage.Level == 1
                        ? $"Lv.0 → Lv.{stage.Level}"
                        : $"Lv.{stage.Level - 1} → Lv.{stage.Level}";

                    var leaf = new HandbookNode
                    {
                        Data = new HandbookData
                        {
                            Id = leafId,
                            ParentId = null,
                            Name = stageName,
                            Type = ENodeType.Item,
                            Icon = "",
                            Color = "",
                            FromBuild = true,
                            Item = _standIn,
                        },
                        Depth = child.Depth + 1,
                        Count = 0,
                        Category = new[] { area.DisplayName },
                        ChildrenDict = new(),
                    };
                    leaf.Parent = child;
                    child.ChildrenDict[leafId] = leaf;
                    NodeById[leafId] = leaf;
                }

                child.SetChildrenCount(Math.Max(1, area.Stages.Count));
                root.ChildrenDict[areaId] = child;
                NodeById[areaId] = child;
                areaIndex++;
            }

            root.SetChildrenCount(Math.Max(1, areaIndex));
            int totalStages = 0;
            foreach (var a in areas) totalStages += a.Stages.Count;
            Plugin.LogSource?.LogInfo($"QBUI construction: {totalStages} stages across {areaIndex} facilities");
        }

        /// <summary>Builds a templateId → Item index from the real handbook tree so recipe items render native icons.</summary>
        private static void EnsureItemLookup(EFT.HandBook.HandbookNodes enc)
        {
            if (ItemByTemplate.Count > 0) return;
            try { foreach (var pair in enc) CollectItems(pair.Value, 0); } catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI item index: {ex.Message}"); }
        }

        private static void CollectItems(HandbookNode? node, int depth)
        {
            if (node?.Data == null || depth > 12) return;
            Item? item = node.Data.Item;
            if (item?.Template != null && !string.IsNullOrEmpty(item.TemplateId) && !ItemByTemplate.ContainsKey(item.TemplateId))
                ItemByTemplate[item.TemplateId] = item;
            foreach (var child in node.Children) CollectItems(child, depth + 1);
        }

        private static void RebuildQuestChildren(HandbookNode parent, List<QuestBookData.Quest> quests)
        {
            foreach (var quest in quests)
            {
                if (quest.Id == null || !LeafIdByQuestId.TryGetValue(quest.Id, out var leafId)) continue;
                string name = QuestBookData.ResolveQuestDisplayName(quest);
                if (QuestBookData.IsQuestCompleted(quest.Id)) name = "<color=#4CAF50>[已完成]</color> " + name;
                var leaf = new HandbookNode { Data = new HandbookData { Id = leafId, ParentId = null, Name = name, Type = ENodeType.Item, Icon = "", Color = "", FromBuild = true, Item = _standIn }, Depth = parent.Depth + 1, Count = 0, Category = new[] { QuestBookData.ResolveTraderName(quest.TraderId ?? "") }, ChildrenDict = new(), };
                leaf.Parent = parent; parent.ChildrenDict[leafId] = leaf;
                QuestByNode[leafId] = quest; NodeById[leafId] = leaf;
            }
        }
        private static Item? ResolveStandIn(EFT.HandBook.HandbookNodes enc) { foreach (var pair in enc) { if (pair.Value?.Data != null && pair.Value.Data.Id != BookRootId) { var i = FindRealItem(pair.Value, 0); if (i != null) return i; } } return null; }
        private static Item? FindRealItem(HandbookNode node, int depth) { if (node == null || depth > 8) return null; foreach (var child in node.Children) { if (child?.Data == null) continue; var item = child.Data.Item; if (child.Data.Type == ENodeType.Item && item != null && !item.QuestItem && item.Template != null) return item; var nested = FindRealItem(child, depth + 1); if (nested != null) return nested; } return null; }

        // ---- native selection hooks --------------------------------------------
        public static bool OnCategorySelected(HandbookNode node) { try { _host?.OnPageCleared(); return true; } catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI cat: {ex}"); return true; } }
        public static bool OnNodeChosen(EntitiesPanel panel, HandbookNode node) { try { if (IsRecipeNode(node)) _host?.PrepareRecipeSelection(); else if (!IsQuestNode(node)) _host?.PrepareNativeSelection(); return true; } catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI chosen: {ex}"); return true; } }
        public static void OnNodeChosenDone(EntitiesPanel panel, HandbookNode node)
        {
            try
            {
                if (IsQuestNode(node)) { var q = FindQuestByNode(node); if (q?.Id != null) _host?.ShowQuestPage(q); }
                else if (IsRecipeNode(node)) { var r = FindRecipeByNode(node); if (r?.Id != null) _host?.ShowRecipePage(r); }
                else if (IsConstructionNode(node)) { HandleConstructionSelection(node); }
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI done: {ex}"); }
        }

        /// <summary>Parses a construction leaf node ID and dispatches to the host to render the stage page.</summary>
        private static void HandleConstructionSelection(HandbookNode node)
        {
            string? id = node?.Data?.Id;
            if (id == null || !id.StartsWith("QHBs")) return;
            var parts = id.Substring(4).Split('_');
            if (parts.Length != 2) return;
            if (!int.TryParse(parts[0], out int areaType)) return;
            if (!int.TryParse(parts[1], out int level)) return;
            var area = ConstructionBookData.AllAreas.FirstOrDefault(a => a.AreaType == areaType);
            var stage = area?.Stages.FirstOrDefault(s => s.Level == level);
            if (area != null && stage != null)
                _host?.ShowConstructionPage(area, stage);
        }
    }

    internal sealed class QuestBookHost : MonoBehaviour
    {
        private const string QC = "#D4BF21", DM = "#A6B0B5", GR = "#4CAF50", BL = "#4FC3F7";
        private RectTransform? _descRoot, _previewStrip, _cover;
        private TextMeshProUGUI? _caption, _descText, _textSource;
        private RectTransform? _itemType, _weight;
        private GameObject? _preview3D, _cameraImage, _loader, _interactionPanel, _characteristics, _compact, _mods;
        private Image? _coverImage;
        private bool _prepared, _destroyed, _pageProbeScheduled, _questPageActive, _recipePageActive, _constructionPageActive, _styleCaptured;
        private int _frames, _waitFrames;
        private string? _selectedQuestId;
        private TMP_FontAsset? _origFont;
        private Material? _origMaterial;
        private float _origSize;
        private Color _origColor;
        private bool _origWrap, _origRich;
        private TextAlignmentOptions _origAlign;
        private HandbookScreen? _screen;
        private string? _navTargetId;
        private int _navAttempts;

        internal bool IsReady => _prepared;
        internal bool IsSameScreen(HandbookScreen screen) => _screen != null && ReferenceEquals(_screen, screen);

        public void Init(HandbookScreen screen) { _screen = screen; _frames = 0; _waitFrames = 0; _prepared = false; _destroyed = false; _selectedQuestId = null; _questPageActive = false; _recipePageActive = false; _constructionPageActive = false; _pageProbeScheduled = false; _navTargetId = null; _navAttempts = 0; }

        private void Update()
        {
            if (_destroyed) return;
            _frames++;
            if (_frames < 15) return;
            if (!_prepared) { TryPrepare(); return; }
            if (_questPageActive || _recipePageActive) EnforceQP();
            CheckCompletion();
            HandleLinkClick();
            if (_navTargetId != null) TryCompleteNav();
        }

        /// <summary>Handles clicks on the quest links rendered inside the quest / recipe description.</summary>
        private void HandleLinkClick()
        {
            if (_descText == null || (!_questPageActive && !_recipePageActive)) return;
            if (!LinkClicked(out Vector2 pos)) return;

            try
            {
                var canvas = _descText.canvas;
                Camera? cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                int linkIndex = TMP_TextUtilities.FindIntersectingLink(_descText, pos, cam);
                if (linkIndex < 0 || linkIndex >= _descText.textInfo.linkCount) return;

                string linkId = _descText.textInfo.linkInfo[linkIndex].GetLinkID();
                if (string.IsNullOrEmpty(linkId) || !linkId.StartsWith("quest:", StringComparison.Ordinal)) return;
                NavigateToQuest(linkId.Substring(6));
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI link click: {ex.Message}"); }
        }

        private static bool LinkClicked(out Vector2 pos)
        {
            pos = Vector2.zero;
            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                pos = touch.position;
                return touch.phase == TouchPhase.Ended;
            }
            if (Input.GetMouseButtonUp(0)) { pos = Input.mousePosition; return true; }
            return false;
        }

        /// <summary>Jumps to another quest inside the book (used by prerequisite and unlock lines).</summary>
        private void NavigateToQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return;
            _navTargetId = questId;
            _navAttempts = 0;
            TryCompleteNav();
        }

        private void TryCompleteNav()
        {
            string questId = _navTargetId!;
            if ((_questPageActive && _selectedQuestId == questId) || _navAttempts++ > 12) { _navTargetId = null; return; }

            try
            {
                var leaf = QuestBookUGUI.FindQuestLeaf(questId);
                var categories = _screen?._categoriesPanel;
                var panel = categories?._entitiesPanel;
                if (leaf?.Parent?.Data == null || categories == null || panel == null)
                {
                    Plugin.LogSource?.LogWarning($"QBUI nav: no handbook node for quest {questId}");
                    _navTargetId = null;
                    return;
                }

                // Replays the native click on the owning category row: expands the tree, loads the entity
                // list and registers the row as the selected one so the tree stays clickable afterwards.
                var category = categories.GetNode(leaf.Parent.Data.Id);
                if (category == null) return; // rows are built lazily, retry on the next frames

                category.OnPointerClick(null!);
                panel.NodeChosen(leaf);                                    // native selection -> our patches render the quest page

                var row = FindEntityRow(panel, leaf);
                if (row != null)
                {
                    row.SelectView();
                    ScrollEntityListTo(panel, row.transform as RectTransform);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QBUI nav failed: {ex.Message}");
                _navTargetId = null;
            }
        }

        /// <summary>Entity list row that renders the given node; matched by display name since the node field is private.</summary>
        private static EntityListElement? FindEntityRow(EntitiesPanel panel, HandbookNode leaf)
        {
            if (panel._entityListContent == null || leaf.Data == null) return null;
            foreach (var row in panel._entityListContent.GetComponentsInChildren<EntityListElement>())
            {
                if (row?._name != null && row._name.text == leaf.Data.Name) return row;
            }
            return null;
        }

        /// <summary>Scrolls the entity list so the given row is aligned with the top of its viewport.</summary>
        private static void ScrollEntityListTo(EntitiesPanel panel, RectTransform? row)
        {
            if (row == null) return;
            var content = panel._entityListContent as RectTransform;
            var scroll = panel._entityListContent?.GetComponentInParent<ScrollRect>();
            if (content == null || scroll == null || !scroll.vertical) return;

            Canvas.ForceUpdateCanvases();
            float scale = content.lossyScale.y > 0f ? content.lossyScale.y : 1f;
            float contentTop = content.TransformPoint(new Vector3(0f, content.rect.height * (1f - content.pivot.y), 0f)).y;
            float rowTop = row.TransformPoint(new Vector3(0f, row.rect.height * (1f - row.pivot.y), 0f)).y;

            float rowOffset = contentTop - rowTop;                       // 行顶到内容顶部的距离
            float pivotOffset = content.rect.height * (1f - content.pivot.y); // 内容顶到 pivot 的距离
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, rowOffset - pivotOffset);
        }

        private void TryPrepare()
        {
            if (++_waitFrames < 5) return;
            _waitFrames = 0;
            try
            {
                if (_screen == null || !_screen.gameObject.activeInHierarchy) return;
                var rr = _screen.transform.Find("Panels/Preview Panel") as RectTransform;
                if (rr == null) return; _descRoot = rr.Find("Description") as RectTransform;
                if (_descRoot == null) return;
                _caption = _descRoot.Find("Caption Panel/Caption")?.GetComponent<TextMeshProUGUI>();
                _itemType = _descRoot.Find("Caption Panel/Item Type") as RectTransform;
                _weight = _descRoot.Find("Caption Panel/Weight") as RectTransform;
                _previewStrip = _descRoot.Find("Contents/Preview Panel") as RectTransform;
                if (_previewStrip != null) { _preview3D = _previewStrip.Find("WP")?.gameObject; _cameraImage = _previewStrip.Find("Camera Image")?.gameObject; _loader = _previewStrip.Find("Loader")?.gameObject; }
                _interactionPanel = FC(_descRoot, "Contents/ItemInfo/InteractionButtonsPanel");
                _characteristics = FC(_descRoot, "Contents/ItemInfo/DescriptionPanel/CharacteristicsPanel");
                _compact = FC(_descRoot, "Contents/ItemInfo/DescriptionPanel/CompactPanel");
                _mods = FC(_descRoot, "Contents/ItemInfo/DescriptionPanel/ModsPanel");
                _descText = FDT(); _textSource ??= FTS();
                if (_descText != null && !_styleCaptured)
                { _origFont = _descText.font; _origMaterial = _descText.fontSharedMaterial; _origSize = _descText.fontSize; _origColor = _descText.color; _origWrap = _descText.enableWordWrapping; _origRich = _descText.richText; _origAlign = _descText.alignment; _styleCaptured = true; }
                if (_caption == null || _previewStrip == null || _descText == null) return;
                _prepared = true; Plugin.LogSource?.LogInfo("QBUI ready");
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI prepare: {ex}"); }
        }

        private static GameObject? FC(Transform r, string p) { var t = r.Find(p); return t != null ? t.gameObject : null; }
        private TextMeshProUGUI? FDT()
        {
            var d = _descRoot != null ? _descRoot.Find("Contents/ItemInfo/DescriptionPanel/DescriptionPanel/Scroll Area/Contents/Description") : null;
            if (d != null) { var t = d.GetComponent<TextMeshProUGUI>(); if (t != null) return t; }
            if (_descRoot != null) foreach (var t in _descRoot.GetComponentsInChildren<TextMeshProUGUI>(true)) if (t != null && t.name == "Description") { Transform? p = t.transform.parent; while (p != null) { if (p.name == "Scroll Area") return t; p = p.parent; } }
            return null;
        }
        private TextMeshProUGUI? FTS()
        {
            if (_screen == null) return null;
            TextMeshProUGUI? fb = null;
            foreach (var t in _screen.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (t == null || string.IsNullOrEmpty(t.text) || t.font == null || !t.gameObject.activeInHierarchy) continue;
                if (Mathf.Abs(t.fontSize - 16f) > 0.5f) continue;
                if (ContainsNonAscii(t.text)) return t;
                fb ??= t;
            }
            return fb;
        }
        private static bool ContainsNonAscii(string s) { foreach (char c in s) if (c > 127) return true; return false; }

        internal void OnPageCleared() { LeavePages(); }
        internal void PrepareNativeSelection() { LeavePages(); }

        /// <summary>Restores the native preview elements before the game rebuilds the item preview for a recipe.</summary>
        internal void PrepareRecipeSelection()
        {
            LeavePages();
            SA(_preview3D, true); SA(_cameraImage, true); SA(_loader, true); SA(_interactionPanel, true);
        }

        internal void ShowQuestPage(QuestBookData.Quest quest)
        {
            if (!_prepared || _descRoot == null || quest.Id == "" || quest.Id == null) return;
            if (_recipePageActive) LeavePages();
            _selectedQuestId = quest.Id; _questPageActive = true;
            if (!_descRoot.gameObject.activeSelf) _descRoot.gameObject.SetActive(true);
            if (_caption != null) _caption.text = QuestBookData.ResolveQuestDisplayName(quest);
            SA(_itemType, false); SA(_weight, false);
            FillQDesc(quest); ShowQCov(quest); EnforceQP();
            if (!_pageProbeScheduled) { _pageProbeScheduled = true; QuestHandbook.Diagnostics.HandbookProbe.SchedulePageProbe(); }
        }

        internal void ShowRecipePage(RecipeBookData.Recipe recipe)
        {
            if (!_prepared || _descRoot == null || string.IsNullOrEmpty(recipe.Id)) return;
            if (_questPageActive) LeavePages();
            _recipePageActive = true;
            if (!_descRoot.gameObject.activeSelf) _descRoot.gameObject.SetActive(true);
            if (_caption != null) _caption.text = RecipeBookData.ResolveItemName(recipe.EndProduct);
            SA(_itemType, false); SA(_weight, false);
            FillRDesc(recipe); SA(_cover, false); EnforceQP();
        }

        private void LeavePages()
        {
            if (!_questPageActive && !_recipePageActive && !_constructionPageActive) return;
            _questPageActive = false; _recipePageActive = false; _constructionPageActive = false; _selectedQuestId = null; RDS();
            SA(_itemType, true); SA(_weight, true); SA(_cover, false);
            SA(_previewStrip, true); SA(_preview3D, true); SA(_cameraImage, true); SA(_loader, true);
            SA(_interactionPanel, true); SA(_characteristics, true); SA(_compact, true); SA(_mods, true);
            if (_caption != null) _caption.text = "";
        }

        private void RDS()
        {
            if (_descText == null || !_styleCaptured) return;
            try
            { if (_origFont != null && _descText.font != _origFont) _descText.font = _origFont; if (_origMaterial != null) _descText.fontSharedMaterial = _origMaterial; _descText.fontSize = _origSize; _descText.color = _origColor; _descText.enableWordWrapping = _origWrap; _descText.richText = _origRich; _descText.alignment = _origAlign; }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI style restore: {ex.Message}"); }
        }

        private void EnforceQP()
        {
            if ((!_questPageActive && !_recipePageActive && !_constructionPageActive) || _descRoot == null || !_descRoot.gameObject.activeInHierarchy) return;
            SA(_itemType, false); SA(_weight, false); SA(_characteristics, false); SA(_compact, false); SA(_mods, false);
            if (_recipePageActive) { SA(_cover, false); return; }
            // 建造书没有物品模型 → 整块隐藏预览区，让下方文本在 VerticalLayoutGroup 里自动上移
            if (_constructionPageActive) { SA(_cover, false); SA(_previewStrip, false); SA(_preview3D, false); SA(_cameraImage, false); SA(_loader, false); SA(_interactionPanel, false); return; }
            SA(_preview3D, false); SA(_cameraImage, false); SA(_loader, false); SA(_interactionPanel, false); SA(_cover, true);
        }

        private void CheckCompletion()
        {
            if (!_questPageActive || _descText == null || _selectedQuestId == null) return;
            bool wasCompleted = QuestBookData.IsQuestCompleted(_selectedQuestId!);
            if (!wasCompleted && QuestBookData.IsQuestCompleted(_selectedQuestId))
            {
                Plugin.LogSource?.LogInfo($"QBUI quest completed: {_selectedQuestId}");
                var q = QuestBookData.All.FirstOrDefault(x => x.Id == _selectedQuestId);
                if (q != null) { var body = BuildQBody(q); if (_descText.text != body) _descText.text = body; }
            }
        }

        private void FillQDesc(QuestBookData.Quest quest)
        {
            if (_descText == null) return;
            if (_textSource != null && _textSource.font != null && _descText.font != _textSource.font) { try { _descText.font = _textSource.font; _descText.fontSharedMaterial = _textSource.fontSharedMaterial; } catch { } }
            _descText.richText = true; _descText.fontSize = 15f; _descText.color = new Color(0.77f, 0.76f, 0.70f, 1f); _descText.enableWordWrapping = true; _descText.alignment = TextAlignmentOptions.TopLeft;
            var body = BuildQBody(quest);
            if (_descText.text != body) _descText.text = body;
        }

        private void FillRDesc(RecipeBookData.Recipe recipe)
        {
            if (_descText == null) return;
            if (_textSource != null && _textSource.font != null && _descText.font != _textSource.font) { try { _descText.font = _textSource.font; _descText.fontSharedMaterial = _textSource.fontSharedMaterial; } catch { } }
            _descText.richText = true; _descText.fontSize = 15f; _descText.color = new Color(0.77f, 0.76f, 0.70f, 1f); _descText.enableWordWrapping = true; _descText.alignment = TextAlignmentOptions.TopLeft;
            _descText.text = BuildRBody(recipe);
        }

        /// Builds the rich-text body displayed for a hideout production recipe.
        private static string BuildRBody(RecipeBookData.Recipe recipe)
        {
            var lines = new List<string>();
            string product = RecipeBookData.ResolveItemName(recipe.EndProduct);
            string countStr = recipe.Count > 1 ? $" × {recipe.Count}" : "";
            lines.Add($"<color={QC}><b>{product}{countStr}</b></color>");

            string areaName = RecipeBookData.ResolveAreaName(recipe.AreaType);
            string levelStr = recipe.AreaLevel > 0 ? $" · 等级 {recipe.AreaLevel}" : "";
            lines.Add(""); lines.Add($"<color={DM}>设施：{areaName}{levelStr}</color>");

            // 跳蚤市场价（服务端 RagfairPriceService 计算）
            if (recipe.FleaPrice > 0)
            {
                string unit = recipe.Count > 1 ? $" <color={DM}>（单价 {recipe.FleaPrice:N0} ₽）</color>" : "";
                lines.Add($"<color={GR}>跳蚤价格：{recipe.ProductTotal:N0} ₽</color>{unit}");
            }

            lines.Add(""); lines.Add($"<color={QC}>制作时间</color>");
            lines.Add($"<color={MainHex}>   · {FormatDuration(recipe.ProductionTime)}</color>");
            if (recipe.Continuous) lines.Add($"<color={DM}>   · 可连续生产</color>");
            if (recipe.NeedFuel) lines.Add($"<color=#FFB74D>   · 生产全程消耗燃料</color>");
            if (recipe.ProductionLimit > 0) lines.Add($"<color={DM}>   · 单次生产上限 {recipe.ProductionLimit}</color>");

            lines.Add(""); lines.Add($"<color={QC}>所需材料</color>");
            if (recipe.Materials.Count == 0) lines.Add($"<color={DM}>无</color>");
            else
            {
                foreach (var m in recipe.Materials) lines.Add(BuildMaterialLine(m));
                if (recipe.MaterialCost > 0) lines.Add($"<color={DM}>   材料合计：{recipe.MaterialCost:N0} ₽</color>");
            }

            if (recipe.Tools.Count > 0)
            {
                lines.Add(""); lines.Add($"<color={QC}>所需工具</color>");
                foreach (var t in recipe.Tools)
                {
                    string price = t.FleaPrice > 0 ? $"  <color={DM}>{t.FleaPrice:N0} ₽</color>" : "";
                    lines.Add($"<color={MainHex}>   · </color><color=#FFB74D>{RecipeBookData.ResolveItemName(t.TemplateId)}</color>{price}");
                }
                lines.Add($"<color={DM}>   · 工具不会被消耗</color>");
            }

            if (recipe.Resources.Count > 0)
            {
                lines.Add(""); lines.Add($"<color={QC}>消耗资源</color>");
                foreach (var r in recipe.Resources)
                {
                    string price = r.FleaPrice > 0 ? $"  <color={DM}>单价 {r.FleaPrice:N0} ₽</color>" : "";
                    lines.Add($"<color={MainHex}>   · {RecipeBookData.ResolveItemName(r.TemplateId)} {r.Count}{price}</color>");
                }
            }

            if (recipe.FleaPrice > 0)
            {
                lines.Add(""); lines.Add($"<color={QC}>成本与收益</color>");
                lines.Add($"<color={MainHex}>   · 材料成本：{recipe.MaterialCost:N0} ₽</color>");
                lines.Add($"<color={MainHex}>   · 成品总价：{recipe.ProductTotal:N0} ₽</color>");
                double profit = recipe.ProductTotal - recipe.MaterialCost;
                lines.Add($"<color={(profit >= 0 ? GR : "#E57373")}>   · 利润：{(profit > 0 ? "+" : "")}{profit:N0} ₽</color>");
            }

            lines.Add(""); lines.Add($"<color={QC}>解锁条件</color>");
            bool hasCond = false;
            if (recipe.AreaLevel > 0)
            {
                lines.Add($"<color={DM}>   · {areaName} 需达到 {recipe.AreaLevel} 级</color>");
                hasCond = true;
            }
            if (!string.IsNullOrEmpty(recipe.QuestId))
            {
                lines.Add(BuildQuestGateLine(recipe.QuestId!));
                hasCond = true;
            }
            if (recipe.Encoded)
            {
                lines.Add($"<color={DM}>   · 需要在设施中先解码</color>");
                hasCond = true;
            }
            if (recipe.Locked && string.IsNullOrEmpty(recipe.QuestId))
            {
                lines.Add($"<color=#E57373>   · 需在游戏中满足额外条件解锁（数据未标注具体任务）</color>");
                hasCond = true;
            }
            if (!hasCond) lines.Add($"<color={DM}>无</color>");

            return string.Join("\n", lines);
        }

        /// <summary>One material line: name × count plus the flea price of the whole stack.</summary>
        private static string BuildMaterialLine(RecipeBookData.Requirement material)
        {
            string name = RecipeBookData.ResolveItemName(material.TemplateId);
            int count = Math.Max(material.Count, 1);
            string countPart = material.Count > 1 ? $" × {material.Count}" : "";
            string tags = (material.FoundInRaid ? " [战局内找到]" : "") + (material.Functional ? " [需可用]" : "");
            string price = material.FleaPrice > 0 ? $"  <color={DM}>{material.FleaPrice * count:N0} ₽</color>" : "";
            return $"<color={MainHex}>   · </color><color={BL}>{name}</color><color={MainHex}>{countPart}{tags}</color>{price}";
        }

        /// <summary>Unlock line for a recipe gated behind a quest, including trader / level / completion state.</summary>
        private static string BuildQuestGateLine(string questId)
        {
            string name = QuestBookData.ResolveQuestNameById(questId);
            QuestBookData.Quest? quest = QuestBookData.FindQuest(questId);

            var details = new List<string>();
            if (quest != null)
            {
                string trader = QuestBookData.ResolveTraderName(quest.TraderId ?? "");
                if (!string.IsNullOrEmpty(trader)) details.Add("商人：" + trader);
                if (quest.MinLevel > 0) details.Add("需等级 " + quest.MinLevel);
            }
            string detailPart = details.Count > 0 ? $"<color={DM}>（{string.Join("，", details)}）</color>" : "";

            bool completed = QuestBookData.IsQuestCompleted(questId);
            string state = completed ? $" <color={GR}>[已完成]</color>" : $" <color=#E57373>[未完成]</color>";

            return $"<color={DM}>   · 需完成任务：</color>{QuestLink(questId, name)}{detailPart}{state}";
        }

        /// <summary>Clickable quest reference; the link id is consumed by the description hit-test.</summary>
        private static string QuestLink(string questId, string name)
        {
            return $"<link=\"quest:{questId}\"><color={BL}><u>{name}</u></color></link>";
        }

        private static string FormatDuration(int seconds)
        {
            if (seconds <= 0) return "瞬间完成";
            TimeSpan span = TimeSpan.FromSeconds(seconds);
            var parts = new List<string>();
            if (span.Hours > 0) parts.Add(span.Hours + " 小时");
            if (span.Minutes > 0) parts.Add(span.Minutes + " 分钟");
            if (span.Seconds > 0 && span.Hours == 0) parts.Add(span.Seconds + " 秒");
            return string.Join(" ", parts);
        }

        /// Builds the rich-text body displayed in the Description panel.
        private static string BuildQBody(QuestBookData.Quest quest)
        {
            var lines = new List<string>();
            bool completed = QuestBookData.IsQuestCompleted(quest.Id!);
            string title = QuestBookData.ResolveQuestDisplayName(quest);
            if (completed)
            {
                lines.Add($"<mark=#2E7D32><color=#E8F5E9><b>　【已完成】　</b></color></mark>");
                lines.Add($"<color={GR}><b>{title}</b></color>");
            }
            else lines.Add($"<color={QC}><b>{title}</b></color>");

            if (quest.MinLevel > 0 || quest.LoyaltyLevel > 0)
            {
                lines.Add(""); lines.Add("<color=#9E9E9E>开放条件</color>");
                if (quest.MinLevel > 0) lines.Add($"<color={DM}>   · 玩家等级 ≥ {quest.MinLevel}</color>");
                if (quest.LoyaltyLevel > 0) { string tn = QuestBookData.ResolveTraderName(quest.TraderId ?? ""); lines.Add($"<color={DM}>   · {tn} 信赖等级 ≥ {quest.LoyaltyLevel}</color>"); }
            }

            string traderName = QuestBookData.ResolveTraderName(quest.TraderId ?? "");
            lines.Add(""); lines.Add($"<color={DM}>商人：{traderName}</color>");

            string? desc = QuestBookData.ResolveQuestDescription(quest.Id);
            if (!string.IsNullOrEmpty(desc)) { lines.Add(""); lines.Add(desc); }

            lines.Add(""); lines.Add($"<color={QC}>任务目标</color>");
            var objs = QuestBookData.GetObjectives(quest.Id!);
            if (objs == null || objs.Count == 0) lines.Add($"<color={DM}>无</color>");
            else foreach (var obj in objs) 
            { 
                var ts = obj.Target.Select(t => QuestBookData.ResolveItemName(t)).ToArray(); 
                string fir = obj.OnlyFoundInRaid ? " [战局内找到]" : ""; 
                string vs = obj.Value > 1 ? $" (x{(int)obj.Value})" : ""; 
                
                // Use locale description if available, otherwise use hardcoded type name
                string description = obj.Locale != null ? obj.Locale : ObjectTypeToChinese(obj.Type);
                string color = obj.Locale != null ? MainHex : ObjectTypeColor(obj.Type);
                
                var itemNames = string.Join(", ", ts);
                // Only color the item names, keep type name and other parts in default color
                lines.Add($"<color={MainHex}>   · {description}</color><color={color}>{itemNames}</color><color={MainHex}>{fir}{vs}</color>"); 
            }

            lines.Add(""); lines.Add($"<color={QC}>前置任务</color>");
            IReadOnlyList<string> prereqs = quest.PreReqs ?? new List<string>();
            if (prereqs.Count == 0) lines.Add($"<color={DM}>无</color>");
            else foreach (string pid in prereqs) lines.Add("▶ " + QuestLink(pid, QuestBookData.ResolveQuestNameById(pid)));

            lines.Add(""); lines.Add($"<color={QC}>解锁奖励</color>");
            bool hasAny = false;
            IReadOnlyList<string> dr = QuestBookData.GetRewardTexts(quest.Id!);
            if (dr.Count > 0) { foreach (string t in dr) { lines.Add($"<color={MainHex}>• {t}</color>"); hasAny = true; } }
            IReadOnlyList<string> unlocks = QuestBookData.GetUnlockTexts(quest.Id!);
            if (unlocks.Count > 0) { lines.Add("<color=#7B7B7E>  └─ 货架解锁:</color>"); foreach (string t in unlocks) lines.Add($"<color={DM}>    • {t}</color>"); hasAny = true; }
            if (!hasAny) lines.Add($"<color={DM}>无</color>");

            return string.Join("\n", lines);
        }

        private static string MainHex = "#C4C2B3";

        // ── Construction Book page rendering ──

        internal void ShowConstructionPage(ConstructionBookData.Area area, ConstructionBookData.Stage stage)
        {
            if (!_prepared || _descRoot == null) return;
            if (_questPageActive || _recipePageActive) LeavePages();
            _constructionPageActive = true;
            if (!_descRoot.gameObject.activeSelf) _descRoot.gameObject.SetActive(true);

            string levelStr = stage.Level == 1 ? "Lv.0 → Lv.1" : $"Lv.{stage.Level - 1} → Lv.{stage.Level}";
            if (_caption != null) _caption.text = $"{area.DisplayName} {levelStr}";

            SA(_itemType, false); SA(_weight, false);
            FillCDesc(area, stage);
            SA(_cover, false);
            EnforceQP();
        }

        private void FillCDesc(ConstructionBookData.Area area, ConstructionBookData.Stage stage)
        {
            if (_descText == null) return;
            if (_textSource != null && _textSource.font != null && _descText.font != _textSource.font)
            { try { _descText.font = _textSource.font; _descText.fontSharedMaterial = _textSource.fontSharedMaterial; } catch { } }
            _descText.richText = true; _descText.fontSize = 15f; _descText.color = new Color(0.77f, 0.76f, 0.70f, 1f); _descText.enableWordWrapping = true; _descText.alignment = TextAlignmentOptions.TopLeft;
            _descText.text = BuildCBody(area, stage);
        }

        private static string BuildCBody(ConstructionBookData.Area area, ConstructionBookData.Stage stage)
        {
            var lines = new List<string>();

            string areaName = area.DisplayName;
            string levelStr = stage.Level == 1 ? "Lv.0 → Lv.1" : $"Lv.{stage.Level - 1} → Lv.{stage.Level}";

            // Header
            lines.Add($"<color={QC}><b>{areaName} {levelStr}</b></color>");

            // Construction time
            if (stage.ConstructionTime > 0)
            {
                lines.Add(""); lines.Add($"<color={QC}>建造时间</color>");
                lines.Add($"<color={MainHex}>   · {FormatDuration((int)stage.ConstructionTime)}</color>");
            }

            // Materials (Item type requirements)
            var materials = stage.Requirements.Where(r => r.Type == "Item").ToList();
            if (materials.Count > 0)
            {
                lines.Add(""); lines.Add($"<color={QC}>所需材料</color>");
                double totalCost = 0;
                foreach (var m in materials)
                {
                    string name = ConstructionBookData.ResolveItemName(m.TemplateId);
                    int count = Math.Max(m.Count, 1);
                    string countPart = count > 1 ? $" ×{count}" : "";
                    string fir = m.FoundInRaid ? " [战局内找到]" : "";
                    string price = m.FleaPrice > 0 ? $"  <color={DM}>{m.FleaPrice * count:N0} ₽</color>" : "";
                    lines.Add($"<color={MainHex}>   · </color><color={BL}>{name}</color><color={MainHex}>{countPart}{fir}</color>{price}");
                    totalCost += m.FleaPrice * count;
                }
                if (totalCost > 0)
                    lines.Add($"<color={DM}>   材料合计：{totalCost:N0} ₽</color>");
            }
            else
            {
                lines.Add(""); lines.Add($"<color={DM}>   · 无需材料</color>");
            }

            // Prerequisites (non-Item)
            var prereqs = stage.Requirements.Where(r => r.Type != "Item").ToList();
            if (prereqs.Count > 0)
            {
                lines.Add(""); lines.Add($"<color={QC}>前置条件</color>");
                foreach (var r in prereqs)
                {
                    switch (r.Type)
                    {
                        case "Area":
                            string prereqArea = ConstructionBookData.ResolveAreaName(r.AreaType);
                            lines.Add($"<color={DM}>   · {prereqArea} ≥ {r.RequiredLevel} 级</color>");
                            break;
                        case "Skill":
                            string skill = ConstructionBookData.ResolveSkillName(r.SkillName);
                            lines.Add($"<color={DM}>   · {skill} ≥ {r.SkillLevel}</color>");
                            break;
                        case "TraderLoyalty":
                            string trader = ConstructionBookData.ResolveTraderName(r.TraderId);
                            lines.Add($"<color={DM}>   · {trader} 信赖 ≥ {r.LoyaltyLevel}</color>");
                            break;
                    }
                }
            }

            // Bonuses
            var visibleBonuses = stage.Bonuses
                .Where(b => b.Visible && (b.Value != 0d || b.Type == "TextBonus"))
                .ToList();
            if (visibleBonuses.Count > 0 || stage.Slots > 0)
            {
                lines.Add(""); lines.Add($"<color={QC}>解锁加成</color>");
                foreach (var b in visibleBonuses)
                    lines.Add($"<color={GR}>   · {FormatBonusLine(b)}</color>");
                if (stage.Slots > 0)
                    lines.Add($"<color={GR}>   · {ConstructionBookData.ResolveBonusName("AdditionalSlots", null)} +{stage.Slots}</color>");
            }

            // Unlocked recipes
            if (stage.UnlockedRecipeCount > 0)
            {
                lines.Add(""); lines.Add($"<color={QC}>解锁配方（{stage.UnlockedRecipeCount} 个）</color>");
                foreach (string prodId in stage.UnlockedRecipeIds)
                {
                    string prodName = ConstructionBookData.ResolveItemName(prodId);
                    lines.Add($"<color={MainHex}>   · </color><color={BL}>{prodName}</color>");
                }
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Renders one bonus as "名称 +数值单位". The name comes from the game locale
        /// (hideout_&lt;Id&gt; then hideout_&lt;Type&gt;); the numeric unit depends on the bonus type.
        /// </summary>
        private static string FormatBonusLine(ConstructionBookData.Bonus bonus)
        {
            string name = ConstructionBookData.ResolveBonusName(bonus.Type, bonus.Id);
            double value = bonus.Value;

            // 自定义文本加成（如荣耀展柜收藏品）：文本自带含义，仅当以冒号结尾时补数值
            if (bonus.Type == "TextBonus")
            {
                if (value != 0d && (name.EndsWith("：", StringComparison.Ordinal) || name.EndsWith(":", StringComparison.Ordinal)))
                    return name + FormatSigned(value);
                return name;
            }

            // 解锁类加成没有可展示的数值
            if (bonus.Type.StartsWith("Unlock", StringComparison.Ordinal))
                return name;

            // 恢复速率类：数值即每小时速率，单位从游戏本地化模板提取
            string unit = ConstructionBookData.ResolveBonusUnit(bonus.Type);
            if (unit.Length > 0) return $"{name} {FormatSigned(value)} {unit}";

            // 百分比类：数据里的 value 已是百分点（如 15 表示 15%），不再乘以 100
            if (IsPercentBonus(bonus.Type)) return $"{name} {FormatSigned(value)}%";

            return $"{name} {FormatSigned(value)}";
        }

        private static string FormatSigned(double value)
        {
            string text = Math.Abs(value % 1d) < 0.001d ? ((int)value).ToString() : value.ToString("0.#");
            return value >= 0d ? "+" + text : text;
        }

        /// <summary>Bonus types whose stored value is expressed directly in percentage points.</summary>
        private static bool IsPercentBonus(string type)
        {
            return type switch
            {
                "ExperienceRate" or "FuelConsumption" or "RagfairCommission" or "ScavCooldownTimer"
                    or "InsuranceReturnTime" or "DebuffEndDelay" or "QuestMoneyReward"
                    or "SkillGroupLevelingBoost" or "SkillLevelingBoost"
                    or "RepairArmorBonus" or "RepairWeaponBonus" or "ReceiveItemBonus"
                    or "MaximumEnergyReserve" => true,
                _ => false,
            };
        }

        /// Translates English objective type to Chinese
        private static string ObjectTypeToChinese(string type)
        {
            return type switch
            {
                "FindItem" => "寻找物品",
                "HandoverItem" => "上交物品",
                "LeaveItem" => "放置物品",
                "Kill" => "击杀目标",
                "Visit" => "访问区域",
                "Location" => "到达地点",
                "Extract" => "成功撤离",
                _ => type
            };
        }

        /// Returns color code for objective type
        private static string ObjectTypeColor(string type)
        {
            return type switch
            {
                "FindItem" => "#4FC3F7",  // Light blue for find items
                "HandoverItem" => "#FF7043",  // Orange for handover items
                "LeaveItem" => "#81C784",  // Green for leave items
                "Kill" => "#E57373",  // Red for kill objectives
                "Visit" => "#BA68C8",  // Purple for visit objectives
                "Location" => "#FFB74D",  // Amber for location objectives
                "Extract" => "#4DB6AC",  // Teal for extract objectives
                _ => MainHex
            };
        }

        private void ShowQCov(QuestBookData.Quest quest)
        { if (_previewStrip == null) return; EC(); if (_cover == null || _coverImage == null) return; SA(_cover, true); _coverImage.sprite = null; LQI(quest, _coverImage); }

        private void EC()
        {
            if (_cover != null || _descRoot == null || _previewStrip == null) return;
            var c = NR("QH Cover", _previewStrip); c.anchorMin = Vector2.zero; c.anchorMax = Vector2.one; c.offsetMin = new(20f, 12f); c.offsetMax = new(-20f, -12f); c.SetAsFirstSibling();
            var img = c.gameObject.AddComponent<Image>(); img.preserveAspect = true; img.raycastTarget = false; img.color = Color.white;
            _cover = c; _coverImage = img;
        }

        private static string? _qid;
        private static readonly Dictionary<string, Sprite> QIC = new();

        private void LQI(QuestBookData.Quest quest, Image image)
        {
            try
            {
                if (quest.Id == null) return; var f = RQIF(quest); if (f == null) return;
                var sp = GQLS(f); if (sp == null || this == null || _destroyed || image == null || _descRoot == null) return;
                if (!_questPageActive || _selectedQuestId != quest.Id) return;
                image.sprite = sp;
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI icon: {ex.Message}"); }
        }

        private static string? RQIF(QuestBookData.Quest quest)
        {
            try
            {
                var dir = QIDir(); if (dir == null) return null; var bn = quest.Id!;
                var img = quest.Image;
                if (!string.IsNullOrEmpty(img) && img.IndexOf("quest/icon/", StringComparison.Ordinal) >= 0) { var cut = img.LastIndexOf('/'); if (cut >= 0) bn = Path.GetFileNameWithoutExtension(img.Substring(cut + 1)); }
                foreach (var ext in new[] { ".jpg", ".png" }) { var c = Path.Combine(dir, bn + ext); if (File.Exists(c)) return c; }
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI img: {ex.Message}"); }
            return null;
        }

        private static string? QIDir()
        {
            if (_qid != null) return _qid.Length == 0 ? null : _qid;
            try { var gr = Path.GetDirectoryName(Application.dataPath); var d = gr == null ? "" : Path.Combine(gr, "SPT_Runtime", "SPT_Data", "images", "quest", "icon"); _qid = !string.IsNullOrEmpty(d) && Directory.Exists(d) ? d : ""; } catch { _qid = ""; }
            return _qid!.Length == 0 ? null : _qid;
        }

        private static Sprite? GQLS(string file)
        {
            if (QIC.TryGetValue(file, out var sp) && sp != null) return sp;
            try
            {
                var data = File.ReadAllBytes(file); var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                if (!ImageConversion.LoadImage(tex, data)) { UnityEngine.Object.Destroy(tex); return null; }
                tex.filterMode = FilterMode.Bilinear; tex.wrapMode = TextureWrapMode.Clamp;
                var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                sprite.hideFlags = HideFlags.HideAndDontSave; QIC[file] = sprite; return sprite;
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI load img: {ex.Message}"); return null; }
        }

        private static void SA(Component? c, bool a) { if (c != null && c.gameObject != null && c.gameObject.activeSelf != a) c.gameObject.SetActive(a); }
        private static void SA(GameObject? g, bool a) { if (g != null && g.activeSelf != a) g.SetActive(a); }
        private static RectTransform NR(string n, Transform p) { var go = new GameObject(n, typeof(RectTransform)); var rt = (RectTransform)go.transform; rt.SetParent(p, false); return rt; }

        public void DestroyAll() { if (_destroyed) return; _destroyed = true; try { LeavePages(); } catch (Exception ex) { Plugin.LogSource?.LogError($"QBUI destroy: {ex}"); } }
    }
}

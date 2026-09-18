using EFT.HandBook;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace QuestHandbook.Diagnostics
{
    // One-shot development helper: dumps the live Handbook UI tree (structure,
    // text/font/colour, button/image sprites) plus an icon inventory to disk so
    // the UGUI version of the quest book can be built against real layout data.
    internal static class HandbookProbe
    {
        private static bool _scheduled;
        private static bool _pageScheduled;

        public static void ScheduleIfNeeded()
        {
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;

            GameObject runner = new("QuestHandbookProbe");
            UnityEngine.Object.DontDestroyOnLoad(runner);
            ProbeTimer timer = runner.AddComponent<ProbeTimer>();
            // Poll every 90 frames until an ACTIVE HandbookScreen appears (the
            // screen can take a while to show after the first Show() calls), then
            // dump once. Gives up after ~30s so a missing screen is not fatal.
            timer.Init(90, 20, TryDumpOnce);
        }

        // Development helper for the native item page rework: scheduled after the
        // first quest page was applied, so the dump captures the LIVE layout of the
        // vanilla "Description" page (title line, preview strip, description scroll
        // area) after the quest overrides were filled in.
        public static void SchedulePageProbe()
        {
            if (_pageScheduled)
            {
                return;
            }

            _pageScheduled = true;

            GameObject runner = new("QuestHandbookPageProbe");
            UnityEngine.Object.DontDestroyOnLoad(runner);
            ProbeTimer timer = runner.AddComponent<ProbeTimer>();
            timer.Init(30, 30, TryDumpOnce);
        }

        private static bool TryDumpOnce()
        {
            try
            {
                HandbookScreen[] all = UnityEngine.Object.FindObjectsOfType<HandbookScreen>(true);
                HandbookScreen? screen = System.Array.Find(all, s => s != null && s.gameObject.activeInHierarchy);
                if (screen is null)
                {
                    if (all.Length > 0)
                    {
                        Plugin.LogSource?.LogInfo(
                            $"QuestHandbook probe: screen exists but inactive total={all.Length} " +
                            $"[0]={all[0].name} activeSelf={all[0].gameObject.activeSelf} parent={all[0].transform.parent}");
                    }
                    else
                    {
                        Plugin.LogSource?.LogInfo("QuestHandbook probe: still no HandbookScreen, retrying");
                    }

                    return false;
                }

                Plugin.LogSource?.LogInfo(
                    $"QuestHandbook probe: ACTIVE screen found total={all.Length} id={screen.gameObject.GetInstanceID()}");
                Dump(screen);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestHandbook probe failed: {ex}");
                return true; // do not loop forever on a hard error
            }
        }

        private static void Dump(HandbookScreen screen)
        {
            try
            {
                DirectoryInfo? outDir = PrepareOutputDir();
                if (outDir is null)
                {
                    return;
                }

                string path = Path.Combine(outDir.FullName, $"HandbookProbe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

                StringBuilder tree = new();
                StringBuilder inventory = new();
                HashSet<string> seenSprites = new();
                int iconDirCount = 0;

                WriteHeader(tree);
                Walk(screen.gameObject, tree, inventory, seenSprites, ref iconDirCount, outDir);

                File.WriteAllText(path, tree.ToString());

                string inventoryPath = Path.Combine(outDir.FullName, "IconInventory.txt");
                File.WriteAllText(inventoryPath, inventory.ToString());

                Plugin.LogSource?.LogInfo($"QuestHandbook probe saved: {path}");
                Plugin.LogSource?.LogInfo($"QuestHandbook probe icons saved: {iconDirCount}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestHandbook probe dump failed: {ex}");
            }
        }

        private sealed class ProbeTimer : MonoBehaviour
        {
            private int _framesToWait;
            private int _attemptsLeft;
            private Func<bool>? _onTry;

            public void Init(int frames, int attempts, Func<bool> onTry)
            {
                _framesToWait = frames;
                _attemptsLeft = attempts;
                _onTry = onTry;
            }

            private void Update()
            {
                if (--_framesToWait > 0)
                {
                    return;
                }

                _framesToWait = 90;
                if (_onTry != null && _onTry())
                {
                    Destroy(gameObject);
                    return;
                }

                if (--_attemptsLeft <= 0)
                {
                    Plugin.LogSource?.LogError("QuestHandbook probe: giving up (screen never appeared active)");
                    Destroy(gameObject);
                }
            }
        }

        private static DirectoryInfo? PrepareOutputDir()
        {
            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                DirectoryInfo dir = Directory.CreateDirectory(Path.Combine(asmDir, "ProbeDump", "icons"));
                return dir.Parent;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestHandbook probe: cannot create output dir: {ex}");
                return null;
            }
        }

        private static void WriteHeader(StringBuilder sb)
        {
            sb.AppendLine($"Quest Handbook UI probe - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Screen: {Screen.width}x{Screen.height}  DPI: {Screen.dpi}");
            sb.AppendLine("Line format: [D<depth>] [active] path <- component summary");
            sb.AppendLine(new string('-', 120));
        }

        private static void Walk(
            GameObject root,
            StringBuilder tree,
            StringBuilder inventory,
            HashSet<string> seenSprites,
            ref int iconDirCount,
            DirectoryInfo outDir)
        {
            // Iterative DFS so pathological trees can't overflow the stack.
            Stack<(GameObject Go, int Depth)> stack = new();
            stack.Push((root, 0));

            int visited = 0;
            const int maxVisited = 20000;

            StringBuilder details = new();

            while (stack.Count > 0 && visited < maxVisited)
            {
                (GameObject go, int depth) = stack.Pop();
                visited++;

                string path = Lineage(go, depth);
                string line = Describe(go, depth, path);
                if (line.Length > 0)
                {
                    tree.AppendLine(line);
                }

                // Detailed structural dump for the areas we plan to mount into.
                if (PathNeedsDetails(path))
                {
                    details.AppendLine($"## {path}  active={go.activeSelf}");
                    details.AppendLine("   comps: " + ComponentList(go));
                    details.AppendLine($"   rect={RectOf(go)}  anchoredPos={AnchoredPos(go)}  pivot={PivotOf(go)}  anchorMin={AnchorMin(go)}  anchorMax={AnchorMax(go)}");
                }

                // Collect sprite inventory while we're here.
                Image? image = go.GetComponent<Image>();
                if (image is not null && image.sprite is not null)
                {
                    Sprite s = image.sprite;
                    if (seenSprites.Add(s.name))
                    {
                        inventory.AppendLine($"'{s.name}'  {s.textureRect.width}x{s.textureRect.height}  packed={s.packed}  <- {path}");
                        MaybeExportIcon(s, path, outDir, ref iconDirCount);
                    }
                }

                // Children (reverse order keeps natural sibling order).
                for (int i = go.transform.childCount - 1; i >= 0; --i)
                {
                    stack.Push((go.transform.GetChild(i).gameObject, depth + 1));
                }
            }

            if (details.Length > 0)
            {
                tree.AppendLine(new string('-', 120));
                tree.AppendLine("=== STRUCTURAL DETAILS ===");
                tree.Append(details);
            }

            tree.AppendLine(new string('-', 120));
            tree.AppendLine($"VISITED NODES: {visited} (cap {maxVisited})");
        }

        private static bool PathNeedsDetails(string path)
        {
            string[] markers =
            {
                "CategoriesPanel",
                "EntitiesView",
                "EntitiesPanel",
                "SubcategoriesPanel",
                "ItemSpecificationPanel",
                "Preview Panel",
                "Scroll Area",
                "ContentArea",
            };

            foreach (string m in markers)
            {
                if (path.Contains(m, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ComponentList(GameObject go)
        {
            string[] skip =
            {
                "Transform", "RectTransform", "CanvasRenderer", "Canvas",
                "GraphicRaycaster", "CanvasGroup",
            };

            Component[] comps;
            try
            {
                comps = go.GetComponents<Component>();
            }
            catch
            {
                return "<error>";
            }

            List<string> names = new();
            foreach (Component c in comps)
            {
                if (c is null)
                {
                    continue;
                }

                string n = c.GetType().Name;
                if (Array.IndexOf(skip, n) >= 0)
                {
                    continue;
                }

                names.Add(n);
            }

            return names.Count == 0 ? "-" : string.Join(", ", names);
        }

        private static string RectOf(GameObject go)
        {
            Rect r = GetRect(go);
            return $"{r.xMin:F1},{r.yMin:F1},{r.xMax:F1},{r.yMax:F1}";
        }

        private static string AnchoredPos(GameObject go)
        {
            return go.transform is RectTransform rt ? rt.anchoredPosition.ToString() : "-";
        }

        private static string PivotOf(GameObject go)
        {
            return go.transform is RectTransform rt ? rt.pivot.ToString() : "-";
        }

        private static string AnchorMin(GameObject go)
        {
            return go.transform is RectTransform rt ? rt.anchorMin.ToString() : "-";
        }

        private static string AnchorMax(GameObject go)
        {
            return go.transform is RectTransform rt ? rt.anchorMax.ToString() : "-";
        }

        private static string Describe(GameObject go, int depth, string path)
        {
            if (go is null)
            {
                return "";
            }

            StringBuilder parts = new();
            Component[] comps;
            try
            {
                comps = go.GetComponents<Component>();
            }
            catch
            {
                return "";
            }

            foreach (Component c in comps)
            {
                if (c is null)
                {
                    continue;
                }

                try
                {
                    if (c is Image img && img.sprite is not null)
                    {
                        parts.Append($"[img:'{img.sprite.name}' {img.color.r:F2},{img.color.g:F2},{img.color.b:F2},{img.color.a:F2}] ");
                    }
                    else if (c is Button || c is Toggle)
                    {
                        parts.Append("[clickable] ");
                    }
                    else if (IsText(c))
                    {
                        string text = GetText(c);
                        float size = GetFontSize(c);
                        Color col = GetColor(c);
                        parts.Append($"[txt {size:F0}:{col.r:F2},{col.g:F2},{col.b:F2}:'{Truncate(text)}'] ");
                    }
                    else
                    {
                        string tName = c.GetType().Name;
                        bool infra = tName is "Transform" or "RectTransform" or "CanvasRenderer" or "CanvasGroup";
                        if (!infra)
                        {
                            parts.Append($"[{tName}] ");
                        }
                    }
                }
                catch
                {
                    // per-component resilience
                }
            }

            if (parts.Length == 0)
            {
                return "";
            }

            Rect rect = GetRect(go);
            string name = go.name;
            return $"D{depth}  {go.activeSelf,5}  {path} ({rect.width:F0}x{rect.height:F0}) <- {parts.ToString().TrimEnd()}";
        }

        private static string Lineage(GameObject go, int depth)
        {
            Transform? t = go.transform;
            List<string> names = new();
            while (t is not null)
            {
                names.Add(t.name);
                t = t.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static bool IsText(Component c)
        {
            string n = c.GetType().Name;
            return n is "Text" or "TextMeshProUGUI" || n.Contains("TextMeshPro") || n.Contains("TMP_Text");
        }

        private static string GetText(Component c)
        {
            PropertyInfo? p = c.GetType().GetProperty("text");
            return p?.GetValue(c) as string ?? "";
        }

        private static float GetFontSize(Component c)
        {
            PropertyInfo? p = c.GetType().GetProperty("fontSize");
            object? v = p?.GetValue(c);
            return v is float f ? f : 0f;
        }

        private static Color GetColor(Component c)
        {
            try
            {
                PropertyInfo? p = c.GetType().GetProperty("color");
                object? v = p?.GetValue(c);
                if (v is Color col)
                {
                    return col;
                }
            }
            catch
            {
                // ignore
            }

            return default;
        }

        private static Rect GetRect(GameObject go)
        {
            if (go.transform is RectTransform rt)
            {
                return rt.rect;
            }

            return default;
        }

        private static string Truncate(string text)
        {
            string flat = text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            return flat.Length <= 48 ? flat : flat[..48] + "…";
        }

        // Saves the sprite region as PNG when the source texture is readable.
        private static void MaybeExportIcon(Sprite s, string ctx, DirectoryInfo outDir, ref int iconDirCount)
        {
            if (IsGenericIconCandidate(s.name) is false)
            {
                return;
            }

            try
            {
                Texture2D tex = s.texture;
                Rect r = s.textureRect;
                int w = Mathf.Max(1, Mathf.RoundToInt(r.width));
                int h = Mathf.Max(1, Mathf.RoundToInt(r.height));
                if (w > 512 || h > 512)
                {
                    return;
                }

                // GetPixels works for readable sources only - guarded by try.
                Color[] px = tex.GetPixels(Mathf.RoundToInt(r.x), Mathf.RoundToInt(r.y), w, h);
                Texture2D copy = new(w, h, TextureFormat.RGBA32, false);
                copy.SetPixels(px);
                copy.Apply();

                string safeName = Sanitize(s.name);
                string file = Path.Combine(outDir.FullName, "icons", $"{safeName}.png");
                File.WriteAllBytes(file, copy.EncodeToPNG());
                UnityEngine.Object.Destroy(copy);
                iconDirCount++;
            }
            catch
            {
                // not readable - inventory line still carries the name
            }
        }

        private static bool IsGenericIconCandidate(string spriteName)
        {
            string n = spriteName.ToLowerInvariant();
            foreach (string kw in new[]
                     {
                         "book", "quest", "task", "note", "handbook", "filter", "list", "category",
                         "search", "refresh", "star", "wish", "arrow", "info", "examine", "map",
                     })
            {
                if (n.Contains(kw))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Sanitize(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new(name.Length);
            foreach (char c in name)
            {
                sb.Append(invalid.Contains(c) ? '_' : c);
            }

            return sb.ToString();
        }
    }
}

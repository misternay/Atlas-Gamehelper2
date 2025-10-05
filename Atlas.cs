namespace Atlas
{
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;

    public sealed class Atlas : PCore<AtlasSettings>
    {
        private const uint CitadelLineColor = 0xFF0000FF; // red
        private const uint TowerLineColor = 0xFFC6C10D; // yellow
        private const uint SearchLineColor = 0xFFFFFFFF; // white
        private const uint ShortestPathColor = 0xFF00FFFF; // cyan highlight

        // Base neighbor threshold kept as a fallback (shouldn't be needed with true graph).
        private const float BaseNeighborThresholdPx = 120f;

        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");
        private string NewGroupName = string.Empty;

        private static readonly Dictionary<string, ContentInfo> MapTags = [];
        private static readonly Dictionary<string, ContentInfo> MapPlain = [];

        public static IntPtr Handle { get; set; }
        private static int _handlePid;

        // Cache: (startNodeIndex, targetNodeIndex) -> node-index path
        private readonly Dictionary<(int start, int target), List<int>> _pathCacheNodes = new();

        private string _lastSearchQuery = null;
        private bool _wasAtlasVisible = false;
        private int _lastStartIdx = -1; // nearest node to mouse last frame

        public override void OnDisable()
        {
            CloseAndResetHandle();
            _pathCacheNodes.Clear();
            _lastSearchQuery = null;
            _wasAtlasVisible = false;
            _lastStartIdx = -1;
        }

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(SettingPathname))
            {
                var content = File.ReadAllText(SettingPathname);
                var serializerSettings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                Settings = JsonConvert.DeserializeObject<AtlasSettings>(content, serializerSettings);
            }

            LoadContentMap();
        }

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var settingsData = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            File.WriteAllText(SettingPathname, settingsData);
        }

        public override void DrawSettings()
        {
            #region SettingsUI
            ImGui.Checkbox("##ControllerMode", ref Settings.ControllerMode);
            ImGui.SameLine(); ImGui.Text("Use Controller Mode");
            ImGui.Separator();

            ImGui.Text("You can search for multiple maps at once. To do this, separate them with a comma ','");
            ImGui.InputText("Search Map##AtlasSearch", ref Settings.SearchQuery, 256);
            ImGui.SameLine();
            ImGui.Checkbox("Draw Lines##DrawLineSearchQuery", ref Settings.DrawLinesSearchQuery);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##AtlasSearchClear"))
                Settings.SearchQuery = string.Empty;
            ImGui.SliderFloat("##DrawSearchInRange", ref Settings.DrawSearchInRange, 1.0f, 10.0f);
            ImGui.SameLine(); ImGui.Text("Draw Lines to Search in range");

            ImGui.Separator();

            // Max path nodes (path length limit in nodes)
            int maxNodes = Settings.MaxPathNodes;
            if (ImGui.InputInt("Max path nodes", ref maxNodes))
            {
                if (maxNodes < 1) maxNodes = 1;
                if (maxNodes > 5000) maxNodes = 5000;
                if (maxNodes != Settings.MaxPathNodes)
                {
                    Settings.MaxPathNodes = maxNodes;
                    _pathCacheNodes.Clear();
                }
            }

            ImGui.Separator();

            ImGui.Checkbox("##ShowMapBadges", ref Settings.ShowMapBadges);
            ImGui.SameLine(); ImGui.Text("Show Map Badges");
            ImGui.Checkbox("##HideCompletedMaps", ref Settings.HideCompletedMaps);
            ImGui.SameLine(); ImGui.Text("Hide Completed Maps");

            ImGui.Checkbox("##HideNotAccessibleMaps", ref Settings.HideNotAccessibleMaps);
            ImGui.SameLine(); ImGui.Text("Hide Not Accessible Maps");

            ImGui.Separator();

            ImGui.SliderFloat("##ScaleMultiplier", ref Settings.ScaleMultiplier, 0.5f, 2.0f);
            ImGui.SameLine(); ImGui.Text("Scale Multiplier");
            ImGui.SliderFloat("##XSlider", ref Settings.XSlider, 0.0f, 3000.0f);
            ImGui.SameLine(); ImGui.Text("Move X Axis");
            ImGui.SliderFloat("##YSlider", ref Settings.YSlider, 0.0f, 3000.0f);
            ImGui.SameLine(); ImGui.Text("Move Y Axis");

            if (ImGui.CollapsingHeader("Badge Settings"))
            {
                foreach (var kv in MapTags.Concat(MapPlain))
                {
                    var key = kv.Key;
                    var info = kv.Value;

                    if (!Settings.ContentOverrides.TryGetValue(key, out var ov))
                    {
                        ov = new ContentOverride();
                        Settings.ContentOverrides[key] = ov;
                    }

                    ImGui.Separator();
                    ImGui.Text(info.Label);

                    bool show = ov.Show ?? info.Show;
                    if (ImGui.Checkbox($"##Show##{key}", ref show))
                    {
                        ov.Show = show;
                        ApplyOverrides();
                    }

                    var bg = ov.BackgroundColor ?? info.BgColor;
                    ImGui.SameLine();
                    ColorSwatch($"Background Color##{key}", ref bg);
                    if (!ColorsEqual(bg, ov.BackgroundColor ?? info.BgColor))
                    {
                        ov.BackgroundColor = bg;
                        ApplyOverrides();
                    }

                    string abbrev = ov.Abbrev ?? info.Abbrev;
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(100);
                    if (ImGui.InputText($"##Abbrev##{key}", ref abbrev, 16))
                    {
                        ov.Abbrev = abbrev;
                        ApplyOverrides();
                    }

                    var fg = ov.FontColor ?? info.FtColor;
                    ImGui.SameLine();
                    ColorSwatch($"Font Color##{key}", ref fg);
                    if (!ColorsEqual(fg, ov.FontColor ?? info.FtColor))
                    {
                        ov.FontColor = fg;
                        ApplyOverrides();
                    }
                }
            }
            ImGui.Separator();

            ImGui.Checkbox("##DrawLinesToCitadel", ref Settings.DrawLinesToCitadel);
            ImGui.SameLine(); ImGui.Text("Draw Lines to Citadels");

            ImGui.Checkbox("##DrawLinesToTowers", ref Settings.DrawLinesToTowers);
            ImGui.SameLine(); ImGui.Text("Draw Lines to Towers in range");
            ImGui.SameLine();
            ImGui.SliderFloat("##DrawTowersInRange", ref Settings.DrawTowersInRange, 1.0f, 10.0f);

            ImGui.Separator();

            ImGui.InputText("##MapGroupName", ref Settings.GroupNameInput, 256);
            ImGui.SameLine();
            if (ImGui.Button("Add new map group"))
            {
                Settings.MapGroups.Add(new MapGroupSettings(Settings.GroupNameInput, Settings.DefaultBackgroundColor, Settings.DefaultFontColor));
                Settings.GroupNameInput = string.Empty;
            }

            for (int i = 0; i < Settings.MapGroups.Count; i++)
            {
                var mapGroup = Settings.MapGroups[i];
                if (ImGui.CollapsingHeader($"{mapGroup.Name}##MapGroup{i}"))
                {
                    float buttonSize = ImGui.GetFrameHeight();
                    if (TriangleButton($"##Up{i}", buttonSize, new Vector4(1, 1, 1, 1), true)) { MoveMapGroup(i, -1); }
                    ImGui.SameLine();
                    if (TriangleButton($"##Down{i}", buttonSize, new Vector4(1, 1, 1, 1), false)) { MoveMapGroup(i, 1); }
                    ImGui.SameLine();
                    if (ImGui.Button($"Rename Group##{i}")) { NewGroupName = mapGroup.Name; ImGui.OpenPopup($"RenamePopup##{i}"); }
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete Group##{i}")) { DeleteMapGroup(i); }
                    ImGui.SameLine();
                    ColorSwatch($"##MapGroupBackgroundColor{i}", ref mapGroup.BackgroundColor);
                    ImGui.SameLine(); ImGui.Text("Background Color");
                    ImGui.SameLine();
                    ColorSwatch($"##MapGroupFontColor{i}", ref mapGroup.FontColor);
                    ImGui.SameLine(); ImGui.Text("Font Color");

                    for (int j = 0; j < mapGroup.Maps.Count; j++)
                    {
                        var mapName = mapGroup.Maps[j];
                        if (ImGui.InputText($"##MapName{i}-{j}", ref mapName, 256))
                            mapGroup.Maps[j] = mapName;

                        ImGui.SameLine();
                        if (ImGui.Button($"Delete##MapNameDelete{i}-{j}"))
                        {
                            mapGroup.Maps.RemoveAt(j);
                            break;
                        }
                    }

                    if (ImGui.Button($"Add new map##AddNewMap{i}"))
                        mapGroup.Maps.Add(string.Empty);

                    if (ImGui.BeginPopupModal($"RenamePopup##{i}", ImGuiWindowFlags.AlwaysAutoResize))
                    {
                        ImGui.InputText("New Name", ref NewGroupName, 256);
                        if (ImGui.Button("OK")) { mapGroup.Name = NewGroupName; ImGui.CloseCurrentPopup(); }
                        ImGui.SameLine();
                        if (ImGui.Button("Cancel")) { ImGui.CloseCurrentPopup(); }
                        ImGui.EndPopup();
                    }
                }
            }
            #endregion
        }

        public override void DrawUI()
        {
            var inventoryPanel = InventoryPanel();
            var isGameHelperForeground = Process.GetCurrentProcess().MainWindowHandle == GetForegroundWindow();
            if (!Core.Process.Foreground && !isGameHelperForeground) return;

            EnsureProcessHandle();

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var _)) return;

            if (!Settings.ControllerMode) if (inventoryPanel) return;

            var drawList = ImGui.GetBackgroundDrawList();

            // Get atlas UiElement + its address (we need the address to read AtlasMapOffsets)
            var (atlasUi, atlasUiAddr) = GetAtlasPanelUiWithAddress();
            var atlasCount = atlasUi.Length;

            // Invalidate cache when atlas closes / invalid
            if (!atlasUi.IsVisible || atlasUi.FirstChild == IntPtr.Zero || atlasCount <= 0 || atlasCount > 10000)
            {
                if (_wasAtlasVisible)
                {
                    _pathCacheNodes.Clear();
                    _lastSearchQuery = null;
                    _wasAtlasVisible = false;
                    _lastStartIdx = -1;
                }
                return;
            }
            _wasAtlasVisible = true;

            // Invalidate cache when search changes
            if (_lastSearchQuery == null || !_lastSearchQuery.Equals(Settings.SearchQuery, StringComparison.Ordinal))
            {
                _pathCacheNodes.Clear();
                _lastSearchQuery = Settings.SearchQuery;
            }

            // Build groups/ranges/search list
            var towers = new HashSet<string>(
                Settings.MapGroups
                    .Where(tower => string.Equals(tower.Name, "Towers", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(tower => tower.Maps)
                    .Select(NormalizeName),
                StringComparer.OrdinalIgnoreCase);

            var boundsTowers = CalculateBounds(Settings.DrawTowersInRange);

            var searchQuery = NormalizeName(Settings.SearchQuery);
            bool doSearch = !string.IsNullOrWhiteSpace(searchQuery);
            List<string> searchList = new();
            if (doSearch)
            {
                searchList = searchQuery
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
            var boundsSearch = CalculateBounds(Settings.DrawSearchInRange);

            // ===== Phase A: collect graph data for ALL nodes (centers & labels) =====
            var nodeCenters = new List<Vector2>(atlasCount);
            var nodeRects = new List<(Vector2 min, Vector2 max)>(atlasCount);
            var nodeNames = new List<string>(atlasCount);
            var nodeCompleted = new List<bool>(atlasCount);
            var nodeAccessible = new List<bool>(atlasCount);

            for (int i = 0; i < atlasCount; i++)
            {
                var atlasNode = atlasUi.GetAtlasNode(i);
                var nameRaw = atlasNode.MapName;
                var name = string.IsNullOrWhiteSpace(nameRaw) ? string.Empty : NormalizeName(nameRaw);

                var center = atlasNode.Position * Settings.ScaleMultiplier + new Vector2(25, 0)
                           + new Vector2(Settings.XSlider - 1500, Settings.YSlider - 1500);

                nodeCenters.Add(center);
                nodeRects.Add((Vector2.Zero, Vector2.Zero));
                nodeNames.Add(name);
                nodeCompleted.Add(atlasNode.IsCompleted);
                nodeAccessible.Add(!atlasNode.IsNotAccessible);
            }

            // Start index from nearest to mouse
            var mousePos = ImGui.GetIO().MousePos;
            int startIdx = ClosestNodeIndex(mousePos, nodeCenters);
            if (startIdx != _lastStartIdx)
            {
                _pathCacheNodes.Clear();
                _lastStartIdx = startIdx;
            }

            // ===== Read the REAL connection graph from memory =====
            List<AtlasNodeEntry> mapNodes = null;
            List<AtlasNodeConnections> mapConns = null;
            List<int>[] connectionGraph = null;

            if (AtlasGraphReader.TryReadAtlasGraph(atlasUiAddr, out mapNodes, out mapConns))
            {
                connectionGraph = AtlasGraphReader.BuildGraphFromConnections(
                    atlasCount,
                    i => atlasUi.GetChildAddress(i),
                    mapNodes,
                    mapConns
                );
            }

            // ===== Phase B: render labels for visible nodes & compute rects for those =====
            for (int i = 0; i < atlasCount; i++)
            {
                var name = nodeNames[i];
                if (string.IsNullOrWhiteSpace(name)) continue;

                var atlasNode = atlasUi.GetAtlasNode(i);
                var nodeUi = atlasUi.GetChild(i);

                if (Settings.HideCompletedMaps && (atlasNode.IsCompleted || (name.EndsWith("Citadel") && AtlasNode.IsFailedAttempt)))
                    continue;
                if (Settings.HideNotAccessibleMaps && atlasNode.IsNotAccessible)
                    continue;

                var rawContents = GetContentName(nodeUi);

                var textSize = ImGui.CalcTextSize(name);
                var padding = new Vector2(5, 2);

                var drawCenter = nodeCenters[i];
                var drawPosition = drawCenter - textSize * 0.5f;
                var bgPos = drawPosition - padding;
                var bgSize = textSize + padding * 2;
                var rectMin = bgPos;
                var rectMax = bgPos + bgSize;

                nodeRects[i] = (rectMin, rectMax);

                // group colors
                var group = Settings.MapGroups.Find(g => g.Maps.Exists(
                    m => NormalizeName(m).Equals(name, StringComparison.OrdinalIgnoreCase)));

                var backgroundColor = group?.BackgroundColor ?? Settings.DefaultBackgroundColor;
                var fontColor = group?.FontColor ?? Settings.DefaultFontColor;

                if (atlasNode.IsCompleted)
                    backgroundColor.W *= 0.6f;

                drawList.AddRect(rectMin, rectMax, ImGuiHelper.Color(fontColor), 3f, ImDrawFlags.RoundCornersAll, 1f);
                drawList.AddRectFilled(rectMin, rectMax, ImGuiHelper.Color(backgroundColor), 3f);
                drawList.AddText(drawPosition, ImGuiHelper.Color(fontColor), name);

                float labelCenterX = drawPosition.X + textSize.X * 0.5f;
                float nextRowTopY = drawPosition.Y + textSize.Y + 4;
                const float rowGap = 4f;

                CategorizeContents(rawContents, MapTags, MapPlain, out var flags, out var contents);

                if (Settings.ShowMapBadges)
                    DrawSquares(drawList, flags, labelCenterX, ref nextRowTopY, rowGap);

                DrawSquares(drawList, contents, labelCenterX, ref nextRowTopY, rowGap);
            }

            // ===== Draw routed paths; track shortest for highlight =====
            var candidatePaths = new List<(List<Vector2> points, float length, uint baseColor)>();

            for (int i = 0; i < atlasCount; i++)
            {
                var name = nodeNames[i];
                if (string.IsNullOrWhiteSpace(name)) continue;

                var center = nodeCenters[i];
                if (center == Vector2.Zero) continue;

                bool isCitadel = name.EndsWith("Citadel", StringComparison.OrdinalIgnoreCase);
                bool isTower = towers.Contains(name);
                bool inTowersRange = boundsTowers.Contains(new PointF(center.X, center.Y));
                bool inSearchRange = boundsSearch.Contains(new PointF(center.X, center.Y));
                bool matchesSearch = doSearch && searchList.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));

                bool FetchAndDraw(uint color, out (List<Vector2> pts, float len) result)
                {
                    result = default;
                    if (startIdx == -1) return false;
                    if (connectionGraph == null) return false;

                    // Cache lookup / compute node path
                    if (!_pathCacheNodes.TryGetValue((startIdx, i), out var nodePath))
                    {
                        if (!TryFindPathBfs(startIdx, i, connectionGraph, out nodePath))
                            return false;

                        // Respect MaxPathNodes (number of nodes on route)
                        if (nodePath.Count > Settings.MaxPathNodes)
                            return false;

                        _pathCacheNodes[(startIdx, i)] = nodePath;
                    }

                    // Build the live polyline (mouse origin for first segment)
                    var poly = BuildPolylineFromNodePath(mousePos, i, nodePath, nodeCenters, nodeRects, out float len);
                    if (poly == null || poly.Count < 2) return false;

                    DrawPolyline(drawList, poly, color);
                    result = (poly, len);
                    return true;
                }

                // Citadels
                if (Settings.DrawLinesToCitadel && isCitadel)
                {
                    if (FetchAndDraw(CitadelLineColor, out var r))
                        candidatePaths.Add((r.pts, r.len, CitadelLineColor));
                }

                // Towers
                if (Settings.DrawLinesToTowers && isTower && inTowersRange && !nodeCompleted[i])
                {
                    if (FetchAndDraw(TowerLineColor, out var r))
                        candidatePaths.Add((r.pts, r.len, TowerLineColor));
                }

                // Search
                if (Settings.DrawLinesSearchQuery && matchesSearch && inSearchRange)
                {
                    if (FetchAndDraw(SearchLineColor, out var r))
                        candidatePaths.Add((r.pts, r.len, SearchLineColor));
                }
            }

            // Highlight the shortest among all candidates (if any)
            if (candidatePaths.Count > 0)
            {
                var shortest = candidatePaths[0];
                for (int k = 1; k < candidatePaths.Count; k++)
                {
                    if (candidatePaths[k].length < shortest.length)
                        shortest = candidatePaths[k];
                }
                DrawPolyline(drawList, shortest.points, ShortestPathColor, 3.0f);
            }
        }

        // ========== Helpers ==========

        // Return both the UiElement and its address (we need the address for AtlasMapOffsets)
        private (UiElement element, IntPtr address) GetAtlasPanelUiWithAddress()
        {
            IntPtr addr = Core.States.InGameStateObject.GameUi.Address;
            var ui = Read<UiElement>(addr);

            if (Settings.ControllerMode)
            {
                addr = ui.GetChildAddress(17); ui = Read<UiElement>(addr);
                addr = ui.GetChildAddress(2); ui = Read<UiElement>(addr);
                addr = ui.GetChildAddress(3); ui = Read<UiElement>(addr);
                addr = ui.GetChildAddress(0); ui = Read<UiElement>(addr);
                addr = ui.GetChildAddress(0); ui = Read<UiElement>(addr);
                addr = ui.GetChildAddress(6); ui = Read<UiElement>(addr);
            }
            else
            {
                addr = ui.GetChildAddress(25); ui = Read<UiElement>(addr);
                addr = ui.GetChildAddress(0); ui = Read<UiElement>(addr);
                addr = ui.GetChildAddress(6); ui = Read<UiElement>(addr);
            }
            return (ui, addr);
        }

        private static void DrawPolyline(ImDrawListPtr drawList, List<Vector2> pts, uint color, float thickness = 1.5f)
        {
            if (pts == null || pts.Count < 2) return;
            for (int i = 0; i < pts.Count - 1; i++)
                drawList.AddLine(pts[i], pts[i + 1], color, thickness);
        }

        private static List<Vector2> BuildPolylineFromNodePath(Vector2 mousePos,
                                                               int targetNodeIndex,
                                                               List<int> nodePath,
                                                               List<Vector2> nodeCenters,
                                                               List<(Vector2 min, Vector2 max)> nodeRects,
                                                               out float length)
        {
            length = 0f;
            if (nodePath == null || nodePath.Count == 0) return null;

            var pts = new List<Vector2>(nodePath.Count + 2)
            {
                mousePos,
                nodeCenters[nodePath[0]]
            };

            for (int k = 0; k < nodePath.Count - 1; k++)
                pts.Add(nodeCenters[nodePath[k + 1]]);

            var lastPoint = nodeCenters[nodePath[^1]];
            var (tmin, tmax) = nodeRects[targetNodeIndex];
            if (tmin != Vector2.Zero || tmax != Vector2.Zero)
            {
                var tcenter = (tmin + tmax) * 0.5f;
                var clipped = GetLineRectangleIntersection(lastPoint, tcenter, tmin, tmax);
                pts.Add(clipped);
            }
            else
            {
                pts.Add(nodeCenters[targetNodeIndex]);
            }

            length = PathLength(pts);
            return pts;
        }

        private static float PathLength(List<Vector2> pts)
        {
            float sum = 0f;
            for (int i = 0; i < pts.Count - 1; i++)
                sum += Vector2.Distance(pts[i], pts[i + 1]);
            return sum;
        }

        private static bool TryFindPathBfs(int start, int goal, List<int>[] graph, out List<int> path)
        {
            path = null;
            if (start < 0 || goal < 0 || graph == null) return false;
            if (start == goal) { path = new List<int> { start }; return true; }

            int n = graph.Length;
            var prev = new int[n];
            for (int i = 0; i < n; i++) prev[i] = -1;

            var q = new Queue<int>();
            var visited = new bool[n];
            visited[start] = true;
            q.Enqueue(start);

            while (q.Count > 0)
            {
                var u = q.Dequeue();
                var adj = graph[u];
                for (int k = 0; k < adj.Count; k++)
                {
                    var v = adj[k];
                    if (visited[v]) continue;
                    visited[v] = true;
                    prev[v] = u;
                    if (v == goal)
                    {
                        var route = new List<int> { v };
                        int cur = v;
                        while (prev[cur] != -1)
                        {
                            cur = prev[cur];
                            route.Add(cur);
                        }
                        route.Reverse();
                        path = route;
                        return true;
                    }
                    q.Enqueue(v);
                }
            }

            return false;
        }

        private static int ClosestNodeIndex(Vector2 pos, List<Vector2> centers)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < centers.Count; i++)
            {
                var c = centers[i];
                if (c == Vector2.Zero) continue;
                float d = Vector2.DistanceSquared(pos, c);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        private void LoadContentMap()
        {
            var path = Path.Join(DllDirectory, "json", "content.json");
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, ContentInfo>>(json);

            MapTags.Clear();
            MapPlain.Clear();

            if (contents is null) return;

            foreach (var content in contents)
            {
                if (content.Key.All(char.IsLetter))
                    MapTags[content.Key] = content.Value;
                else
                    MapPlain[content.Key] = content.Value;
            }

            ApplyOverrides();
        }

        private static void DrawSquares(ImDrawListPtr drawList, List<ContentInfo> infos, float centerX, ref float nextRowTopY, float rowGap)
        {
            if (infos.Count == 0) return;

            const float fixedHeight = 18f;
            const float padding = 6f;

            var widths = new List<float>(infos.Count);
            float totalW = 0f;

            foreach (var info in infos)
            {
                var abbrev = string.IsNullOrWhiteSpace(info.Abbrev) ? info.Label[..1] : info.Abbrev;
                var textSize = ImGui.CalcTextSize(abbrev);
                float w = MathF.Max(fixedHeight, textSize.X + padding);
                widths.Add(w);
                totalW += w;
            }

            var basePos = new Vector2(centerX - totalW * 0.5f, nextRowTopY);

            for (int i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                string abbrev = string.IsNullOrWhiteSpace(info.Abbrev) ? (!string.IsNullOrEmpty(info.Label) ? info.Label.Substring(0, 1) : "?") : info.Abbrev;
                var boxSize = new Vector2(widths[i], fixedHeight);
                var squareMin = basePos;
                var squareMax = squareMin + boxSize;

                drawList.AddRectFilled(squareMin, squareMax, ImGuiHelper.Color(info.BgColor));

                var textSize = ImGui.CalcTextSize(abbrev);
                var textPos = squareMin + (boxSize - textSize) * 0.5f;
                drawList.AddText(textPos, ImGuiHelper.Color(info.FtColor), abbrev);

                basePos.X += boxSize.X;
            }

            nextRowTopY += fixedHeight + rowGap;
        }

        private static Vector2 GetLineRectangleIntersection(Vector2 lineStart, Vector2 rectCenter, Vector2 rectMin, Vector2 rectMax)
        {
            if (lineStart.X >= rectMin.X && lineStart.X <= rectMax.X &&
                lineStart.Y >= rectMin.Y && lineStart.Y <= rectMax.Y)
            {
                return lineStart;
            }

            Vector2 direction = rectCenter - lineStart;

            float dirX = direction.X == 0 ? 1e-6f : direction.X;
            float dirY = direction.Y == 0 ? 1e-6f : direction.Y;

            float tMinX = (rectMin.X - lineStart.X) / dirX;
            float tMaxX = (rectMax.X - lineStart.X) / dirX;
            float tMinY = (rectMin.Y - lineStart.Y) / dirY;
            float tMaxY = (rectMax.Y - lineStart.Y) / dirY;

            if (tMinX > tMaxX) { (tMaxX, tMinX) = (tMinX, tMaxX); }
            if (tMinY > tMaxY) { (tMaxY, tMinY) = (tMinY, tMaxY); }

            float tEnter = Math.Max(tMinX, tMinY);
            float tExit = Math.Min(tMaxX, tMaxY);

            if (tEnter > tExit || tEnter < 0)
                return rectCenter;

            float t = Math.Min(tEnter, 1.0f);
            return lineStart + direction * t;
        }

        private void MoveMapGroup(int index, int direction)
        {
            if (index < 0 || index >= Settings.MapGroups.Count) return;
            int to = index + direction;
            if (to < 0 || to >= Settings.MapGroups.Count) return;

            var item = Settings.MapGroups[index];
            Settings.MapGroups.RemoveAt(index);
            Settings.MapGroups.Insert(to, item);
        }

        private void DeleteMapGroup(int index)
        {
            if (index < 0 || index >= Settings.MapGroups.Count) return;
            Settings.MapGroups.RemoveAt(index);
        }

        private static void ColorSwatch(string label, ref Vector4 color)
        {
            if (ImGui.ColorButton(label, color))
                ImGui.OpenPopup(label);
            if (ImGui.BeginPopup(label))
            {
                ImGui.ColorPicker4(label, ref color);
                ImGui.EndPopup();
            }
        }

        private static bool TriangleButton(string id, float buttonSize, Vector4 color, bool isUp)
        {
            var pressed = ImGui.Button(id, new Vector2(buttonSize, buttonSize));
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetItemRectMin();
            var triSize = buttonSize * 0.5f;
            var center = new Vector2(pos.X + buttonSize * 0.5f, pos.Y + buttonSize * 0.5f);

            Vector2 p1, p2, p3;
            if (isUp)
            {
                p1 = new Vector2(center.X, center.Y - triSize * 0.5f);
                p2 = new Vector2(center.X - triSize * 0.5f, center.Y + triSize * 0.5f);
                p3 = new Vector2(center.X + triSize * 0.5f, center.Y + triSize * 0.5f);
            }
            else
            {
                p1 = new Vector2(center.X - triSize * 0.5f, center.Y - triSize * 0.5f);
                p2 = new Vector2(center.X + triSize * 0.5f, center.Y - triSize * 0.5f);
                p3 = new Vector2(center.X, center.Y + triSize * 0.5f);
            }

            drawList.AddTriangleFilled(p1, p2, p3, ImGuiHelper.Color(color));
            return pressed;
        }

        private static void EnsureProcessHandle()
        {
            int pid = (int)Core.Process.Pid;
            if (Handle == IntPtr.Zero)
            {
                Handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                               ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
                _handlePid = pid;
                return;
            }

            if (_handlePid != pid)
            {
                CloseAndResetHandle();
                Handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                               ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
                _handlePid = pid;
            }
        }

        private static void CloseAndResetHandle()
        {
            if (Handle != IntPtr.Zero)
            {
                CloseHandle(Handle);
                Handle = IntPtr.Zero;
            }
            _handlePid = 0;
        }

        public static T Read<T>(IntPtr address) where T : unmanaged
        {
            if (address == IntPtr.Zero) return default;
            EnsureProcessHandle();
            T result = default;
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemory(Handle, address, ref result);
            return result;
        }

        public static string ReadWideString(nint address, int stringLength)
        {
            if (address == IntPtr.Zero || stringLength <= 0) return string.Empty;
            EnsureProcessHandle();
            byte[] result = new byte[stringLength * 2];
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(Handle, address, result);
            return Encoding.Unicode.GetString(result).Split('\0')[0];
        }

        private static string NormalizeName(string s) =>
            string.IsNullOrWhiteSpace(s) ? s : CollapseWhitespace(s.Replace('\u00A0', ' ').Trim());

        private static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s)
            {
                bool isSpace = char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!prevSpace) sb.Append(' ');
                }
                else
                {
                    sb.Append(ch);
                }
                prevSpace = isSpace;
            }
            return sb.ToString();
        }

        private UiElement GetAtlasPanelUi()
        {
            return GetAtlasPanelUiWithAddress().element;
        }

        private static bool InventoryPanel()
        {
            var uiElement = Read<UiElement>(Core.States.InGameStateObject.GameUi.Address);
            var invetoryPanel = uiElement.GetChild(33);
            return invetoryPanel.IsVisible;
        }

        private static void CategorizeContents(IEnumerable<string> raws,
            Dictionary<string, ContentInfo> tagMap,
            Dictionary<string, ContentInfo> plainMap,
            out List<ContentInfo> flags,
            out List<ContentInfo> contents)
        {
            flags = [];
            contents = [];
            foreach (var raw in raws)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var info = MatchContent(NormalizeName(raw), tagMap, plainMap);
                if (info is null || !info.Show) continue;
                if (info.IsFlag) flags.Add(info); else contents.Add(info);
            }
        }

        public static List<string> GetContentName(UiElement nodeUi)
        {
            const int ContentOffset = 0x290;
            var result = new List<string>();

            nodeUi = nodeUi.GetChild(0);
            nodeUi = nodeUi.GetChild(0);

            var len = nodeUi.Length;
            if (len <= 0) return result;

            for (int i = 0; i < len; i++)
            {
                var childAddr = nodeUi.GetChildAddress(i);
                var contentPtr = Read<IntPtr>(childAddr + ContentOffset);
                if (contentPtr == IntPtr.Zero) continue;

                var contentName = ReadWideString(contentPtr, 64);
                if (string.IsNullOrWhiteSpace(contentName)) continue;

                result.Add(contentName);
            }

            return result;
        }

        private static ContentInfo MatchContent(string contentName,
            Dictionary<string, ContentInfo> tagMap,
            Dictionary<string, ContentInfo> plainMap)
        {
            if (string.IsNullOrWhiteSpace(contentName)) return null;

            var normalized = contentName.Replace("\u00A0", " ").Trim();

            int lb = normalized.IndexOf('[');
            int rb = lb >= 0 ? normalized.IndexOf(']', lb + 1) : -1;
            if (lb >= 0 && rb > lb + 1)
            {
                var inside = normalized[(lb + 1)..rb];
                var pipe = inside.IndexOf('|');
                var tag = (pipe >= 0 ? inside[..pipe] : inside).Trim();

                if (tagMap.TryGetValue(tag, out var tagInfo))
                    return tagInfo;

                if (plainMap.TryGetValue(tag, out var tagAsPlain))
                    return tagAsPlain;
            }

            foreach (var map in plainMap)
                if (normalized.Contains(map.Key, StringComparison.OrdinalIgnoreCase))
                    return map.Value;

            foreach (var tag in tagMap)
                if (normalized.Contains(tag.Key, StringComparison.OrdinalIgnoreCase))
                    return tag.Value;

            return null;
        }

        private void ApplyOverrides()
        {
            foreach (var entry in Settings.ContentOverrides)
            {
                if (MapTags.TryGetValue(entry.Key, out var info) ||
                    MapPlain.TryGetValue(entry.Key, out info))
                {
                    var ov = entry.Value;
                    if (ov.BackgroundColor.HasValue) info.BackgroundColor = [ov.BackgroundColor.Value.X, ov.BackgroundColor.Value.Y, ov.BackgroundColor.Value.Z, ov.BackgroundColor.Value.W];
                    if (ov.FontColor.HasValue) info.FontColor = [ov.FontColor.Value.X, ov.FontColor.Value.Y, ov.FontColor.Value.Z, ov.FontColor.Value.W];
                    if (ov.Show.HasValue) info.Show = ov.Show.Value;
                    if (!string.IsNullOrEmpty(ov.Abbrev)) info.Abbrev = ov.Abbrev;
                }
            }
        }

        private static bool ColorsEqual(Vector4 a, Vector4 b, float eps = 0.001f)
        {
            return Math.Abs(a.X - b.X) < eps &&
                   Math.Abs(a.Y - b.Y) < eps &&
                   Math.Abs(a.Z - b.Z) < eps &&
                   Math.Abs(a.W - b.W) < eps;
        }

        private static RectangleF CalculateBounds(float range)
        {
            var baseBoundsTowers = new RectangleF(0, 0, ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y);
            return RectangleF.Inflate(baseBoundsTowers, baseBoundsTowers.Width * (range - 1.0f), baseBoundsTowers.Height * (range - 1.0f));
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}

namespace Atlas
{
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;

    public sealed class Atlas : PCore<AtlasSettings>
    {
        private const uint CitadelLineColor = 0xFF0000FF;
        private const uint TowerLineColor = 0xFFC6C10D;
        private const uint SearchLineColor = 0xFFFFFFFF;
        private const uint GridLineColor = 0x50FFFFFF;

        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");
        private string NewGroupName = string.Empty;
        private string _exportStatusMessage = string.Empty;
        private Vector4 _exportStatusColor = new(1f, 1f, 1f, 1f);

        private static readonly Dictionary<string, ContentInfo> MapTags = [];
        private static readonly Dictionary<string, ContentInfo> MapPlain = [];

        public static IntPtr Handle { get; set; }
        private static int _handlePid;

        public override void OnDisable() 
        {
            CloseAndResetHandle();
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
            ImGui.SameLine(); 
            ImGui.Text("Use Controller Mode");
            ImGui.Separator();

            ImGui.Checkbox("Draw Atlas Grid", ref Settings.DrawGrid);
            if (Settings.DrawGrid)
                if (ImGui.CollapsingHeader("Atlas Grid Settings"))
                {
                    ImGui.Checkbox("Hide connections to completed maps", ref Settings.GridSkipCompleted);
                }

            ImGui.Separator();

            if (ImGui.Button("Export Atlas Graph##AtlasExport"))
            {
                ExportAtlasGraph();
            }
            if (!string.IsNullOrEmpty(_exportStatusMessage))
            {
                ImGui.SameLine();
                ImGui.TextColored(_exportStatusColor, _exportStatusMessage);
            }

            ImGui.Separator();

            ImGui.Text("You can search for multiple maps at once. To do this, separate them with a comma ','");
            ImGui.InputText("Search Map##AtlasSearch", ref Settings.SearchQuery, 256);
            ImGui.SameLine();
            ImGui.Checkbox("Draw Lines##DrawLineSearchQuery", ref Settings.DrawLinesSearchQuery);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##AtlasSearchClear")) 
                Settings.SearchQuery = string.Empty;
            ImGui.SliderFloat("##DrawSearchInRange", ref Settings.DrawSearchInRange, 1.0f, 10.0f);
            ImGui.SameLine();
            ImGui.Text("Draw Lines to Search in range");
            ImGui.Separator();

            ImGui.Checkbox("##ShowMapBadges", ref Settings.ShowMapBadges);
            ImGui.SameLine(); 
            ImGui.Text("Show Map Content Badges");
            ImGui.Checkbox("##HideCompletedMaps", ref Settings.HideCompletedMaps);
            ImGui.SameLine(); 
            ImGui.Text("Hide Completed Maps");

            ImGui.Checkbox("##HideNotAccessibleMaps", ref Settings.HideNotAccessibleMaps);
            ImGui.SameLine(); ImGui.Text("Hide Not Accessible Maps");

            ImGui.Separator();

            ImGui.Checkbox("Auto Layout", ref Settings.AutoLayout);
            var nudge = Settings.AnchorNudge;
            if (ImGui.InputFloat2("Anchor Nudge (px)", ref nudge))
                Settings.AnchorNudge = nudge;

            ImGui.SliderFloat("##ScaleMultiplier", ref Settings.ScaleMultiplier, 0.5f, 2.0f);
            ImGui.SameLine(); 
            ImGui.Text("Scale Multiplier");

            if (Settings.AutoLayout) 
                ImGui.BeginDisabled();
            ImGui.SliderFloat("##XSlider", ref Settings.XSlider, 0.0f, 3000.0f);
            ImGui.SameLine(); 
            ImGui.Text("Move X Axis");
            ImGui.SliderFloat("##YSlider", ref Settings.YSlider, 0.0f, 3000.0f);
            ImGui.SameLine(); 
            ImGui.Text("Move Y Axis");
            if (Settings.AutoLayout) 
                ImGui.EndDisabled();

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
            ImGui.SameLine();
            ImGui.Text("Draw Lines to Citadels");

            ImGui.Checkbox("##DrawLinesToTowers", ref Settings.DrawLinesToTowers);
            ImGui.SameLine();
            ImGui.Text("Draw Lines to Towers in range");
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
                    if (TriangleButton($"##Up{i}", buttonSize, new Vector4(1, 1, 1, 1), true)) 
                    { 
                        MoveMapGroup(i, -1); 
                    }
                    ImGui.SameLine();
                    if (TriangleButton($"##Down{i}", buttonSize, new Vector4(1, 1, 1, 1), false)) 
                    { 
                        MoveMapGroup(i, 1); 
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"Rename Group##{i}")) 
                    { 
                        NewGroupName = mapGroup.Name; 
                        ImGui.OpenPopup($"RenamePopup##{i}"); 
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete Group##{i}")) 
                    { 
                        DeleteMapGroup(i); 
                    }
                    ImGui.SameLine();
                    ColorSwatch($"##MapGroupBackgroundColor{i}", ref mapGroup.BackgroundColor);
                    ImGui.SameLine(); 
                    ImGui.Text("Background Color");
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
                        if (ImGui.Button("OK")) 
                        { 
                            mapGroup.Name = NewGroupName; 
                            ImGui.CloseCurrentPopup(); 
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Cancel")) 
                        { 
                            ImGui.CloseCurrentPopup(); 
                        }
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
            if (!Core.Process.Foreground && !isGameHelperForeground) 
                return;

            EnsureProcessHandle();

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var playerRender)) 
                return;

            var drawList = ImGui.GetBackgroundDrawList();

            var atlasUi = GetAtlasPanelUi();
            if (!atlasUi.IsVisible)
                return;

            var panelAddr = GetAtlasPanelAddress();
            var atlasMap = panelAddr != IntPtr.Zero 
                ? Read<AtlasMapOffsets>(panelAddr) 
                : default;
            bool useVector = TryVectorCount<AtlasNodeEntry>(atlasMap.AtlasNodes, out int vecCount)
                && vecCount > 0 && vecCount <= 10000;
            var atlasCount = useVector ? vecCount : atlasUi.Length;
            if (atlasCount <= 0 || atlasCount > 10000)
                return;

            var towers = new HashSet<string>(
                Settings.MapGroups
                    .Where(tower => string.Equals(tower.Name, "Towers", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(tower => tower.Maps)
                    .Select(NormalizeName),
                StringComparer.OrdinalIgnoreCase);
            var boundsTowers = CalculateBounds(Settings.DrawTowersInRange);

            var searchQuery = NormalizeName(Settings.SearchQuery);
            bool doSearch = !string.IsNullOrWhiteSpace(searchQuery);
            List<string> searchList = [];
            if (doSearch)
            {
                searchList = searchQuery
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
            var boundsSearch = CalculateBounds(Settings.DrawSearchInRange);

            var playerLocation = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(playerRender.WorldPosition);

            float resScale = ComputeRelativeUiScale(in atlasUi.UiElementBase, Settings.BaseWidth, Settings.BaseHeight);
            float uiScale = Math.Clamp(Settings.ScaleMultiplier * resScale, 0.5f, 4.0f);
            using (new FontScaleScope(uiScale))
            {
                if (!Settings.ControllerMode) 
                    if (inventoryPanel) 
                        return;

                if (Settings.DrawGrid && useVector)
                {
                    var panelTopLeft = GetFinalTopLeft(in atlasUi.UiElementBase);
                    var panelScale = ComputeScalePair(in atlasUi.UiElementBase);
                    var panelSize = new Vector2(
                        atlasUi.UiElementBase.UnscaledSize.X * panelScale.X,
                        atlasUi.UiElementBase.UnscaledSize.Y * panelScale.Y);
                    var panelRect = new RectangleF(panelTopLeft.X, panelTopLeft.Y, panelSize.X, panelSize.Y);

                    var centers = new Dictionary<StdTuple2D<int>, Vector2>();
                    var completed = new HashSet<StdTuple2D<int>>();

                    Vector2 renderOffset = Settings.AutoLayout
                        ? Settings.AnchorNudge
                        : new Vector2(Settings.XSlider - 1500f, Settings.YSlider - 1500f);

                    for (int i = 0; i < vecCount; i++)
                    {
                        var entry = ReadVectorAt<AtlasNodeEntry>(atlasMap.AtlasNodes, i);
                        if (entry.UiElementPtr == IntPtr.Zero)
                            continue;

                        var node = Read<AtlasNode>(entry.UiElementPtr);

                        var nodeTopLeft = GetFinalTopLeft(in node.UiElementBase);
                        var nodeScale = ComputeScalePair(in node.UiElementBase);
                        var nodeSize = new Vector2(
                            node.UiElementBase.UnscaledSize.X * nodeScale.X,
                            node.UiElementBase.UnscaledSize.Y * nodeScale.Y);

                        var nodeCenter = nodeTopLeft + nodeSize * 0.5f;

                        if (!panelRect.Contains(nodeCenter.X, nodeCenter.Y))
                            continue;

                        centers[entry.GridPosition] = nodeCenter;

                        if (node.IsCompleted)
                            completed.Add(entry.GridPosition);
                    }

                    static (int x, int y) XY(StdTuple2D<int> t) => (t.X, t.Y);

                    static bool IsCanonical(StdTuple2D<int> a, StdTuple2D<int> b)
                    {
                        var (ax, ay) = XY(a);
                        var (bx, by) = XY(b);

                        return (ax < bx) || (ax == bx && ay <= by);
                    }

                    if (TryVectorCount<AtlasNodeConnections>(atlasMap.AtlasNodeConnections, out int connCount)
                        && connCount > 0)
                    {
                        float lineTh = MathF.Max(1f, uiScale * 2.5f);

                        for (int i = 0; i < connCount; i++)
                        {
                            var cn = ReadVectorAt<AtlasNodeConnections>(atlasMap.AtlasNodeConnections, i);
                            var src = cn.GridPosition;

                            if (!centers.TryGetValue(src, out var a))
                                continue;

                            var targets = new[]
                            {
                                cn.Connection1, cn.Connection2, cn.Connection3, cn.Connection4
                            };

                            foreach (var dst in targets)
                            {
                                if (dst.Equals(default(StdTuple2D<int>)) || dst.Equals(src))
                                    continue;

                                if (!IsCanonical(src, dst))
                                    continue;

                                if (!centers.TryGetValue(dst, out var b))
                                    continue;

                                if (Settings.GridSkipCompleted && (completed.Contains(src) || completed.Contains(dst)))
                                    continue;

                                drawList.AddLine(a, b, GridLineColor, lineTh);
                            }
                        }
                    }
                }

                for (int i = 0; i < atlasCount; i++)
                {
                    AtlasNode atlasNode;
                    UiElement nodeUi;
                    if (useVector)
                    {
                        var entry = ReadVectorAt<AtlasNodeEntry>(atlasMap.AtlasNodes, i);
                        if (entry.UiElementPtr == IntPtr.Zero)
                            continue;
                        atlasNode = Read<AtlasNode>(entry.UiElementPtr);
                        nodeUi = Read<UiElement>(entry.UiElementPtr);
                    }
                    else
                    {
                        atlasNode = atlasUi.GetAtlasNode(i);
                        nodeUi = atlasUi.GetChild(i);
                    }

                    var mapName = NormalizeName(atlasNode.MapName);
                    if (!IsPrintableUnicode(mapName))
                        continue;

                    if (string.IsNullOrWhiteSpace(mapName))
                        continue;

                    if (doSearch && !searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (Settings.HideCompletedMaps && (atlasNode.IsCompleted || (mapName.EndsWith("Citadel") && AtlasNode.IsFailedAttempt)))
                        continue;

                    if (Settings.HideNotAccessibleMaps && atlasNode.IsNotAccessible)
                        continue;

                    var rawContents = GetContentName(nodeUi);

                    var textSize = ImGui.CalcTextSize(mapName);

                    Vector2 drawPosition;
                    if (Settings.AutoLayout)
                    {
                        var nodeTopLeft = GetFinalTopLeft(in atlasNode.UiElementBase);
                        var nodeScale = ComputeScalePair(in atlasNode.UiElementBase);
                        var nodeSize = new Vector2(
                            atlasNode.UiElementBase.UnscaledSize.X * nodeScale.X,
                            atlasNode.UiElementBase.UnscaledSize.Y * nodeScale.Y);

                        var nodeCenter = nodeTopLeft + nodeSize * 0.5f;
                        drawPosition = nodeCenter - textSize * 0.5f;

                        drawPosition += Settings.AnchorNudge;
                    }
                    else
                    {
                        var nodeTopLeft = GetFinalTopLeft(in atlasNode.UiElementBase);
                        var nodeScale = ComputeScalePair(in atlasNode.UiElementBase);
                        var nodeSize = new Vector2(
                            atlasNode.UiElementBase.UnscaledSize.X * nodeScale.X,
                            atlasNode.UiElementBase.UnscaledSize.Y * nodeScale.Y);

                        var nodeCenter = nodeTopLeft + nodeSize * 0.5f;
                        var posOffset = new Vector2(Settings.XSlider - 1500f, Settings.YSlider - 1500f);
                        drawPosition = nodeCenter - textSize * 0.5f + posOffset;
                    }

                    var group = Settings.MapGroups.Find(g => g.Maps.Exists(
                        m => NormalizeName(m).Equals(mapName, StringComparison.OrdinalIgnoreCase)));

                    var backgroundColor = group?.BackgroundColor ?? Settings.DefaultBackgroundColor;
                    var fontColor = group?.FontColor ?? Settings.DefaultFontColor;

                    if (atlasNode.IsCompleted)
                        backgroundColor.W *= 0.6f;

                    var padding = new Vector2(5, 2) * uiScale;
                    var bgPos = drawPosition - padding;
                    var bgSize = textSize + padding * 2;
                    var rectCenter = (bgPos + (bgPos + bgSize)) * 0.5f;
                    var intersectionPoint = GetLineRectangleIntersection(playerLocation, rectCenter, bgPos, bgPos + bgSize);

                    float rounding = 3f * uiScale;
                    float borderTh = MathF.Max(1f, 1f * uiScale);
                    drawList.AddRect(bgPos, bgPos + bgSize, ImGuiHelper.Color(fontColor), rounding, ImDrawFlags.RoundCornersAll, borderTh);
                    drawList.AddRectFilled(bgPos, bgPos + bgSize, ImGuiHelper.Color(backgroundColor), rounding);
                    drawList.AddText(drawPosition, ImGuiHelper.Color(fontColor), mapName);

                    if (Settings.DrawLinesToCitadel && mapName.EndsWith("Citadel", StringComparison.OrdinalIgnoreCase))
                    {
                        drawList.AddLine(playerLocation, intersectionPoint, CitadelLineColor, borderTh);
                    }

                    if (Settings.DrawLinesToTowers
                        && towers.Contains(mapName)
                        && !atlasNode.IsCompleted
                        && boundsTowers.Contains(new PointF(drawPosition.X, drawPosition.Y)))
                    {
                        drawList.AddLine(playerLocation, intersectionPoint, TowerLineColor, borderTh);
                    }

                    if (Settings.DrawLinesSearchQuery
                        && doSearch && searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        && boundsSearch.Contains(new PointF(drawPosition.X, drawPosition.Y)))
                    {
                        drawList.AddLine(playerLocation, intersectionPoint, SearchLineColor, borderTh);
                    }

                    float labelCenterX = drawPosition.X + textSize.X * 0.5f;
                    float nextRowTopY = drawPosition.Y + textSize.Y + (4f * uiScale);
                    float rowGap = 4f * uiScale;

                    CategorizeContents(rawContents, MapTags, MapPlain, out var flags, out var contents);

                    if (Settings.ShowMapBadges)
                        DrawSquares(drawList, flags, labelCenterX, ref nextRowTopY, rowGap, uiScale);

                    DrawSquares(drawList, contents, labelCenterX, ref nextRowTopY, rowGap, uiScale);
                }
            }
        }

        private void LoadContentMap()
        {
            var path = Path.Join(DllDirectory, "json", "content.json");
            if (!File.Exists(path)) 
                return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, ContentInfo>>(json);

            MapTags.Clear();
            MapPlain.Clear();

            if (contents is null) 
                return;

            foreach (var content in contents)
            {
                if (content.Key.All(char.IsLetter))
                    MapTags[content.Key] = content.Value;
                else
                    MapPlain[content.Key] = content.Value;
            }

            ApplyOverrides();
        }

        private static Vector2 ComputeScalePair(in UiElementBaseOffset uiBase)
        {
            var io = ImGui.GetIO();
            float baseW = (float)UiElementBaseFuncs.BaseResolution.X;
            float baseH = (float)UiElementBaseFuncs.BaseResolution.Y;
            float sx = io.DisplaySize.X / MathF.Max(1f, baseW);
            float sy = io.DisplaySize.Y / MathF.Max(1f, baseH);

            Vector2 pair;
            switch (uiBase.ScaleIndex)
            {
                case 0:
                    pair = new Vector2(sx, sx);
                    break;
                case 1:
                    pair = new Vector2(sy, sy);
                    break;
                case 2:
                    float s = MathF.Min(sx, sy);
                    pair = new Vector2(s, s);
                    break;
                default:
                    pair = new Vector2(sx, sy);
                    break;
            }

            return pair * MathF.Max(0.0001f, uiBase.LocalScaleMultiplier);
        }

        private static float ComputeUniformScale(in UiElementBaseOffset uiBase, float dispW, float dispH)
        {
            float baseW = (float)UiElementBaseFuncs.BaseResolution.X;
            float baseH = (float)UiElementBaseFuncs.BaseResolution.Y;
            float sx = dispW / MathF.Max(1f, baseW);
            float sy = dispH / MathF.Max(1f, baseH);

            float s = uiBase.ScaleIndex switch
            {
                0 => sx,
                1 => sy,
                2 => MathF.Min(sx, sy),
                _ => MathF.Min(sx, sy),
            };

            return s * MathF.Max(0.0001f, uiBase.LocalScaleMultiplier);
        }

        private static float ComputeRelativeUiScale(in UiElementBaseOffset uiBase, float refW, float refH)
        {
            var io = ImGui.GetIO();
            float cur = ComputeUniformScale(in uiBase, io.DisplaySize.X, io.DisplaySize.Y);
            float pref = ComputeUniformScale(in uiBase, refW, refH);

            return pref > 0 ? cur / pref : 1f;
        }

        private static Vector2 GetFinalTopLeft(in UiElementBaseOffset leaf)
        {
            Vector2 pos = Vector2.Zero;
            UiElementBaseOffset cur = leaf;
            int guard = 0;
            IntPtr last = IntPtr.Zero;
            while (true)
            {
                var scale = ComputeScalePair(in cur);
                pos += new Vector2(cur.RelativePosition.X * scale.X,
                    cur.RelativePosition.Y * scale.Y);
                if (UiElementBaseFuncs.ShouldModifyPos(cur.Flags))
                {
                    pos += new Vector2(cur.PositionModifier.X * scale.X,
                        cur.PositionModifier.Y * scale.Y);
                }
                if (cur.ParentPtr == IntPtr.Zero || cur.ParentPtr == last || ++guard > 64)
                    break;
                last = cur.Self;
                cur = Read<UiElementBaseOffset>(cur.ParentPtr);
            }

            return pos;
        }

        private static void DrawSquares(ImDrawListPtr drawList, List<ContentInfo> infos, float centerX, 
            ref float nextRowTopY, float rowGap, float uiScale)
        {
            if (infos.Count == 0) 
                return;

            const float fixedHeightBase = 18f;
            const float paddingBase = 6f;
            float fixedHeight = fixedHeightBase * uiScale;
            float padding = paddingBase * uiScale;

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
                string abbrev;
                if (string.IsNullOrWhiteSpace(info.Abbrev))
                    abbrev = !string.IsNullOrEmpty(info.Label) ? info.Label.Substring(0, 1) : "?";
                else
                    abbrev = info.Abbrev;
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

        private readonly struct FontScaleScope : IDisposable
        {
            private readonly ImFontPtr _font;
            private readonly float _prevScale;
            public FontScaleScope(float scale)
            {
                _font = ImGui.GetFont();
                _prevScale = _font.Scale;
                _font.Scale = _prevScale * scale;
                ImGui.PushFont(_font);
            }
            public void Dispose()
            {
                ImGui.PopFont();
                _font.Scale = _prevScale;
            }
        }

        private static Vector2 GetLineRectangleIntersection(Vector2 lineStart, Vector2 rectCenter, Vector2 rectMin, Vector2 rectMax)
        {
            if (lineStart.X >= rectMin.X && lineStart.X <= rectMax.X &&
                lineStart.Y >= rectMin.Y && lineStart.Y <= rectMax.Y)
                return lineStart;

            Vector2 direction = rectCenter - lineStart;

            float dirX = direction.X == 0 ? 1e-6f : direction.X;
            float dirY = direction.Y == 0 ? 1e-6f : direction.Y;

            float tMinX = (rectMin.X - lineStart.X) / dirX;
            float tMaxX = (rectMax.X - lineStart.X) / dirX;
            float tMinY = (rectMin.Y - lineStart.Y) / dirY;
            float tMaxY = (rectMax.Y - lineStart.Y) / dirY;

            if (tMinX > tMaxX)
                (tMaxX, tMinX) = (tMinX, tMaxX);

            if (tMinY > tMaxY)
                (tMaxY, tMinY) = (tMinY, tMaxY);

            float tEnter = Math.Max(tMinX, tMinY);
            float tExit = Math.Min(tMaxX, tMaxY);

            if (tEnter > tExit || tEnter < 0)
                return rectCenter;

            float t = Math.Min(tEnter, 1.0f);

            return lineStart + direction * t;
        }

        private void MoveMapGroup(int index, int direction)
        {
            if (index < 0 || index >= Settings.MapGroups.Count) 
                return;

            int to = index + direction;
            if (to < 0 || to >= Settings.MapGroups.Count) 
                return;

            var item = Settings.MapGroups[index];
            Settings.MapGroups.RemoveAt(index);
            Settings.MapGroups.Insert(to, item);
        }

        private void DeleteMapGroup(int index)
        {
            if (index < 0 || index >= Settings.MapGroups.Count) 
                return;

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
            if (address == IntPtr.Zero) 
                return default;

            EnsureProcessHandle();
            T result = default;
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemory(Handle, address, ref result);

            return result;
        }

        private static bool TryVectorCount<T>(in StdVector vector, out int count) 
            where T : unmanaged
        {
            count = 0;
            if (vector.First == IntPtr.Zero || vector.Last == IntPtr.Zero) 
                return false;

            long bytes = vector.Last.ToInt64() - vector.First.ToInt64();
            if (bytes <= 0) 
                return false;

            int stride = Marshal.SizeOf<T>();
            if (stride <= 0 || (bytes % stride) != 0) 
                return false;

            long c = bytes / stride;
            if (c <= 0 || c > 10000)
                return false;

            count = (int)c;

            return true;
        }

        private static T ReadVectorAt<T>(in StdVector vector, int index) 
            where T : unmanaged
        {
            int stride = Marshal.SizeOf<T>();
            var addr = IntPtr.Add(vector.First, index * stride);

            return Read<T>(addr);
        }

        public static string ReadWideString(nint address, int stringLength)
        {
            if (address == IntPtr.Zero || stringLength <= 0) 
                return string.Empty;

            EnsureProcessHandle();
            byte[] result = new byte[stringLength * 2];
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(Handle, address, result);

            return Encoding.Unicode.GetString(result).Split('\0')[0];
        }

        static bool IsPrintableUnicode(string str)
        {
            if (string.IsNullOrEmpty(str))
                return false;

            if (str.All(ch => ch == '?' || char.IsWhiteSpace(ch)))
                return false;

            foreach (var rune in str.EnumerateRunes())
            {
                if (rune.Value == 0xFFFD)
                    return false;

                var cat = Rune.GetUnicodeCategory(rune);
                switch (cat)
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.OtherNotAssigned:
                        return false;
                }
            }

            return true;
        }

        private static string NormalizeName(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? s
                : CollapseWhitespace(s.Replace('\u00A0', ' ').Trim());

        private static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) 
                return s;

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
            var uiElement = Read<UiElement>(Core.States.InGameStateObject.GameUi.Address);
            if (Settings.ControllerMode)
            {
                uiElement = uiElement.GetChild(17);
                uiElement = uiElement.GetChild(2);
                uiElement = uiElement.GetChild(3);
                uiElement = uiElement.GetChild(0);
                uiElement = uiElement.GetChild(0);
                uiElement = uiElement.GetChild(6);
            }
            else
            {
                uiElement = uiElement.GetChild(25);
                uiElement = uiElement.GetChild(0);
                uiElement = uiElement.GetChild(6);
            }

            return uiElement;
        }

        private IntPtr GetAtlasPanelAddress()
        {
            IntPtr address = Core.States.InGameStateObject.GameUi.Address;
            var root = Read<UiElement>(address);
            if (Settings.ControllerMode)
            {
                address = root.GetChildAddress(17);
                var uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(2); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(3); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(0); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(0); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(6);
            }
            else
            {
                address = root.GetChildAddress(25);
                var uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(0); uiElement = Read<UiElement>(address);
                address = uiElement.GetChildAddress(6);
            }

            return address;
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
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var info = MatchContent(NormalizeName(raw), tagMap, plainMap);
                if (info is null || !info.Show)
                    continue;
                if (info.IsFlag) flags.Add(info);
                else contents.Add(info);
            }
        }

        private void ExportAtlasGraph()
        {
            if (!TryCollectAtlasGraph(out var nodes, out var edges, out var bounds, out var error))
            {
                _exportStatusMessage = error;
                _exportStatusColor = new Vector4(1f, 0.4f, 0.4f, 1f);
                return;
            }

            var exportPath = Path.Join(DllDirectory, "config", "atlas-export.html");
            var directory = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var html = BuildHtml(nodes, edges, bounds);
            File.WriteAllText(exportPath, html);

            _exportStatusMessage = $"Exported to {exportPath}";
            _exportStatusColor = new Vector4(0.4f, 1f, 0.4f, 1f);
        }

        private bool TryCollectAtlasGraph(out List<NodeExportInfo> nodes,
            out List<EdgeExport> edges,
            out RectangleF bounds,
            out string error)
        {
            nodes = [];
            edges = [];
            bounds = RectangleF.Empty;
            error = string.Empty;

            EnsureProcessHandle();

            var atlasUi = GetAtlasPanelUi();
            if (!atlasUi.IsVisible)
            {
                error = "Atlas panel must be visible before exporting.";
                return false;
            }

            var panelAddr = GetAtlasPanelAddress();
            var atlasMap = panelAddr != IntPtr.Zero
                ? Read<AtlasMapOffsets>(panelAddr)
                : default;

            bool useVector = TryVectorCount<AtlasNodeEntry>(atlasMap.AtlasNodes, out int vecCount)
                && vecCount > 0 && vecCount <= 10000;
            int atlasCount = useVector ? vecCount : atlasUi.Length;
            if (atlasCount <= 0 || atlasCount > 10000)
            {
                error = "Unable to locate atlas nodes.";
                return false;
            }

            var gridLookup = new Dictionary<StdTuple2D<int>, NodeExportInfo>();
            var completed = new HashSet<StdTuple2D<int>>();

            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;

            for (int i = 0; i < atlasCount; i++)
            {
                AtlasNode atlasNode;
                UiElement nodeUi;
                StdTuple2D<int>? gridPosition = null;

                if (useVector)
                {
                    var entry = ReadVectorAt<AtlasNodeEntry>(atlasMap.AtlasNodes, i);
                    if (entry.UiElementPtr == IntPtr.Zero)
                        continue;

                    atlasNode = Read<AtlasNode>(entry.UiElementPtr);
                    nodeUi = Read<UiElement>(entry.UiElementPtr);
                    gridPosition = entry.GridPosition;
                }
                else
                {
                    atlasNode = atlasUi.GetAtlasNode(i);
                    nodeUi = atlasUi.GetChild(i);
                }

                var mapName = NormalizeName(atlasNode.MapName);
                if (!IsPrintableUnicode(mapName))
                    continue;

                if (string.IsNullOrWhiteSpace(mapName))
                    continue;

                if (Settings.HideCompletedMaps && (atlasNode.IsCompleted || (mapName.EndsWith("Citadel") && AtlasNode.IsFailedAttempt)))
                    continue;

                if (Settings.HideNotAccessibleMaps && atlasNode.IsNotAccessible)
                    continue;

                var rawContents = GetContentName(nodeUi);
                CategorizeContents(rawContents, MapTags, MapPlain, out var flags, out var contents);

                var labels = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var info in flags.Concat(contents))
                {
                    var label = !string.IsNullOrWhiteSpace(info.Label) ? info.Label : info.Abbrev;
                    if (string.IsNullOrWhiteSpace(label) || !seen.Add(label))
                        continue;
                    labels.Add(label);
                }

                foreach (var raw in rawContents)
                {
                    var normalized = NormalizeName(raw);
                    if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                        continue;
                    labels.Add(normalized);
                }

                var nodeTopLeft = GetFinalTopLeft(in atlasNode.UiElementBase);
                var nodeScale = ComputeScalePair(in atlasNode.UiElementBase);
                var nodeSize = new Vector2(
                    atlasNode.UiElementBase.UnscaledSize.X * nodeScale.X,
                    atlasNode.UiElementBase.UnscaledSize.Y * nodeScale.Y);

                var nodeCenter = nodeTopLeft + nodeSize * 0.5f;

                if (Settings.AutoLayout)
                    nodeCenter += Settings.AnchorNudge;
                else
                    nodeCenter += new Vector2(Settings.XSlider - 1500f, Settings.YSlider - 1500f);

                var group = Settings.MapGroups.Find(g => g.Maps.Exists(
                    m => NormalizeName(m).Equals(mapName, StringComparison.OrdinalIgnoreCase)));

                var backgroundColor = group?.BackgroundColor ?? Settings.DefaultBackgroundColor;
                var fontColor = group?.FontColor ?? Settings.DefaultFontColor;

                if (atlasNode.IsCompleted)
                    backgroundColor.W *= 0.6f;

                var infoNode = new NodeExportInfo
                {
                    Name = mapName,
                    Position = nodeCenter,
                    Size = nodeSize,
                    IsCompleted = atlasNode.IsCompleted,
                    IsAccessible = atlasNode.IsAccessible,
                    Contents = labels,
                    BackgroundColor = backgroundColor,
                    FontColor = fontColor
                };

                nodes.Add(infoNode);

                if (gridPosition.HasValue)
                {
                    gridLookup[gridPosition.Value] = infoNode;
                    if (atlasNode.IsCompleted)
                        completed.Add(gridPosition.Value);
                }

                float left = nodeCenter.X - (nodeSize.X * 0.5f);
                float top = nodeCenter.Y - (nodeSize.Y * 0.5f);
                float right = nodeCenter.X + (nodeSize.X * 0.5f);
                float bottom = nodeCenter.Y + (nodeSize.Y * 0.5f);

                minX = Math.Min(minX, left);
                minY = Math.Min(minY, top);
                maxX = Math.Max(maxX, right);
                maxY = Math.Max(maxY, bottom);
            }

            if (nodes.Count == 0)
            {
                error = "No atlas nodes available for export.";
                return false;
            }

            bounds = new RectangleF(
                minX,
                minY,
                Math.Max(0f, maxX - minX),
                Math.Max(0f, maxY - minY));

            if (useVector && TryVectorCount<AtlasNodeConnections>(atlasMap.AtlasNodeConnections, out int connCount)
                && connCount > 0)
            {
                static (int x, int y) XY(StdTuple2D<int> t) => (t.X, t.Y);

                static bool IsCanonical(StdTuple2D<int> a, StdTuple2D<int> b)
                {
                    var (ax, ay) = XY(a);
                    var (bx, by) = XY(b);

                    return (ax < bx) || (ax == bx && ay <= by);
                }

                for (int i = 0; i < connCount; i++)
                {
                    var cn = ReadVectorAt<AtlasNodeConnections>(atlasMap.AtlasNodeConnections, i);
                    var src = cn.GridPosition;

                    if (!gridLookup.TryGetValue(src, out var from))
                        continue;

                    var targets = new[] { cn.Connection1, cn.Connection2, cn.Connection3, cn.Connection4 };

                    foreach (var dst in targets)
                    {
                        if (dst.Equals(default(StdTuple2D<int>)) || dst.Equals(src))
                            continue;

                        if (!IsCanonical(src, dst))
                            continue;

                        if (!gridLookup.TryGetValue(dst, out var to))
                            continue;

                        if (Settings.GridSkipCompleted && (completed.Contains(src) || completed.Contains(dst)))
                            continue;

                        edges.Add(new EdgeExport(from.Position, to.Position));
                    }
                }
            }

            return true;
        }

        private static string BuildHtml(IReadOnlyList<NodeExportInfo> nodes,
            IReadOnlyList<EdgeExport> edges,
            RectangleF bounds)
        {
            const float margin = 40f;
            float width = Math.Max(1f, bounds.Width) + margin * 2f;
            float height = Math.Max(1f, bounds.Height) + margin * 2f;

            float leftOffset = bounds.Left - margin;
            float topOffset = bounds.Top - margin;

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"utf-8\">");
            sb.AppendLine("  <title>Atlas Export</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine("    body { background-color: #0f0f0f; color: #f5f5f5; font-family: 'Segoe UI', Tahoma, sans-serif; }");
            sb.AppendLine("    .container { position: relative; margin: 24px; }");
            sb.AppendLine("    .atlas-surface { position: relative; width: 100%; height: 100%; }");
            sb.AppendLine("    .connections { position: absolute; inset: 0; pointer-events: none; }");
            sb.AppendLine("    .connections line { stroke: rgba(200, 200, 200, 0.35); stroke-width: 2; }");
            sb.AppendLine("    .node { position: absolute; transform: translate(-50%, -50%); padding: 6px 10px; border-radius: 6px; border: 1px solid rgba(255,255,255,0.6); min-width: 120px; max-width: 220px; box-shadow: 0 2px 8px rgba(0,0,0,0.45); }");
            sb.AppendLine("    .node strong { display: block; margin-bottom: 4px; font-size: 14px; }");
            sb.AppendLine("    .node ul { margin: 4px 0 0; padding-left: 18px; font-size: 12px; }");
            sb.AppendLine("    .node ul li { margin-bottom: 2px; }");
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine(FormattableString.Invariant($"  <h1>Atlas Export &ndash; {DateTime.Now:G}</h1>"));
            sb.AppendLine(FormattableString.Invariant($"  <div class=\"container\" style=\"width:{width:F2}px;height:{height:F2}px;\">"));
            sb.AppendLine("    <div class=\"atlas-surface\">");
            sb.AppendLine(FormattableString.Invariant($"      <svg class=\"connections\" viewBox=\"0 0 {width:F2} {height:F2}\" xmlns=\"http://www.w3.org/2000/svg\">"));

            foreach (var edge in edges)
            {
                float x1 = edge.From.X - leftOffset;
                float y1 = edge.From.Y - topOffset;
                float x2 = edge.To.X - leftOffset;
                float y2 = edge.To.Y - topOffset;

                sb.AppendLine(FormattableString.Invariant($"        <line x1=\"{x1:F2}\" y1=\"{y1:F2}\" x2=\"{x2:F2}\" y2=\"{y2:F2}\" />"));
            }

            sb.AppendLine("      </svg>");

            foreach (var node in nodes)
            {
                float left = node.Position.X - leftOffset;
                float top = node.Position.Y - topOffset;
                string background = ToCssColor(node.BackgroundColor);
                var solidFontColor = node.FontColor;
                solidFontColor.W = 1f;
                string font = ToCssColor(solidFontColor);
                string border = ToCssColor(solidFontColor);
                string accessibility = node.IsAccessible ? "true" : "false";
                string completed = node.IsCompleted ? "true" : "false";
                string title = WebUtility.HtmlEncode(node.Name);

                sb.AppendLine(FormattableString.Invariant($"      <div class=\"node\" style=\"left:{left:F2}px;top:{top:F2}px;background:{background};color:{font};border-color:{border};\" data-name=\"{title}\" data-accessible=\"{accessibility}\" data-completed=\"{completed}\">"));
                sb.AppendLine(FormattableString.Invariant($"        <strong>{title}</strong>"));

                if (node.Contents.Count > 0)
                {
                    sb.AppendLine("        <ul>");
                    foreach (var content in node.Contents)
                    {
                        var encoded = WebUtility.HtmlEncode(content);
                        sb.AppendLine(FormattableString.Invariant($"          <li>{encoded}</li>"));
                    }
                    sb.AppendLine("        </ul>");
                }

                sb.AppendLine("      </div>");
            }

            sb.AppendLine("    </div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private static string ToCssColor(Vector4 color)
        {
            float r = Math.Clamp(color.X, 0f, 1f) * 255f;
            float g = Math.Clamp(color.Y, 0f, 1f) * 255f;
            float b = Math.Clamp(color.Z, 0f, 1f) * 255f;
            float a = Math.Clamp(color.W, 0f, 1f);

            return FormattableString.Invariant($"rgba({r:F0}, {g:F0}, {b:F0}, {a:F3})");
        }

        private sealed class NodeExportInfo
        {
            public string Name { get; init; }
            public Vector2 Position { get; init; }
            public Vector2 Size { get; init; }
            public bool IsCompleted { get; init; }
            public bool IsAccessible { get; init; }
            public List<string> Contents { get; init; } = [];
            public Vector4 BackgroundColor { get; init; }
            public Vector4 FontColor { get; init; }
        }

        private sealed record EdgeExport(Vector2 From, Vector2 To);

        public static List<string> GetContentName(UiElement nodeUi)
        {
            const int ContentOffset = 0x290;
            var result = new List<string>();

            nodeUi = nodeUi.GetChild(0);
            nodeUi = nodeUi.GetChild(0);

            var len = nodeUi.Length;
            if (len <= 0) 
                return result;

            for (int i = 0; i < len; i++)
            {
                var childAddr = nodeUi.GetChildAddress(i);
                var contentPtr = Read<IntPtr>(childAddr + ContentOffset);
                if (contentPtr == IntPtr.Zero) 
                    continue;

                var contentName = ReadWideString(contentPtr, 64);
                if (string.IsNullOrWhiteSpace(contentName)) 
                    continue;

                result.Add(contentName);
            }

            return result;
        }

        private static ContentInfo MatchContent(string contentName,
            Dictionary<string, ContentInfo> tagMap,
            Dictionary<string, ContentInfo> plainMap)
        {
            if (string.IsNullOrWhiteSpace(contentName)) 
                return null;

            var normalized = contentName.Replace("\u00A0", " ").Trim();

            int lb = normalized.IndexOf('[');
            int rb = lb >= 0 ? normalized.IndexOf(']', lb + 1) : -1;
            if (lb >= 0 && rb > lb + 1)
            {
                var inside = normalized.Substring(lb + 1, rb - lb - 1);
                var pipe = inside.IndexOf('|');
                var tag = (pipe >= 0 ? inside[..pipe] : inside).Trim();

                if (tagMap.TryGetValue(tag, out var tagInfo))
                    return tagInfo;

                if (plainMap.TryGetValue(tag, out var tagAsPlain))
                    return tagAsPlain;
            }

            foreach (var map in plainMap)
            {
                if (normalized.Contains(map.Key, StringComparison.OrdinalIgnoreCase))
                    return map.Value;
            }

            foreach (var tag in tagMap)
            {
                if (normalized.Contains(tag.Key, StringComparison.OrdinalIgnoreCase))
                    return tag.Value;
            }

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
                    if (ov.BackgroundColor.HasValue) 
                        info.BackgroundColor = [ov.BackgroundColor.Value.X, ov.BackgroundColor.Value.Y, ov.BackgroundColor.Value.Z, ov.BackgroundColor.Value.W];
                    if (ov.FontColor.HasValue) 
                        info.FontColor = [ov.FontColor.Value.X, ov.FontColor.Value.Y, ov.FontColor.Value.Z, ov.FontColor.Value.W];
                    if (ov.Show.HasValue) 
                        info.Show = ov.Show.Value;
                    if (!string.IsNullOrEmpty(ov.Abbrev)) 
                        info.Abbrev = ov.Abbrev;
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
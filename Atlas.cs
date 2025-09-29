namespace Atlas
{
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;
    using System;
    using System.Collections;
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
        private const uint CitadelLineColor = 0xFF0000FF;
        private const uint TowerLineColor = 0xFFC6C10D;
        private const uint SearchLineColor = 0xFFFFFFFF;

        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");
        private string NewGroupName = string.Empty;

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
            ImGui.SameLine(); ImGui.Text("Show Map Badges");
            ImGui.Checkbox("##HideCompletedMaps", ref Settings.HideCompletedMaps);
            ImGui.SameLine(); ImGui.Text("Hide Completed Maps");
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
            if (!Core.Process.Foreground && !isGameHelperForeground) return;

            EnsureProcessHandle();

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var playerRender)) return;

            var drawList = ImGui.GetBackgroundDrawList();

            var atlasUi = GetAtlasPanelUi();
            var atlasCount = atlasUi.Length;
            if (!atlasUi.IsVisible || atlasUi.FirstChild == IntPtr.Zero || atlasCount <= 0 || atlasCount > 10000) return;

            var towers = Settings.MapGroups.Where(g => g.Name == "Towers").SelectMany(g => g.Maps).ToList();
            var boundsTowers = calculateBounds(Settings.DrawTowersInRange);

            var searchQuery = NormalizeName(Settings.SearchQuery);
            bool doSearch = !string.IsNullOrWhiteSpace(searchQuery);
            List<string> searchList = new List<string>();
            if (doSearch)
            {
                searchList = searchQuery.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            var boundsSearch = calculateBounds(Settings.DrawSearchInRange);

            var playerLocation = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(playerRender.WorldPosition);

            if (!Settings.ControllerMode) if (inventoryPanel) return;

            for (int i = 0; i < atlasCount; i++)
            {
                var atlasNode = atlasUi.GetAtlasNode(i);
                var nodeUi = atlasUi.GetChild(i);

                var mapName = atlasNode.MapName;
                if (string.IsNullOrWhiteSpace(mapName))
                    continue;

                mapName = NormalizeName(mapName);
                if (doSearch && !searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (Settings.HideCompletedMaps && (atlasNode.IsCompleted || (mapName.EndsWith("Citadel") && atlasNode.IsFailedAttempt))) 
                    continue;

                var rawContents = GetContentName(nodeUi);

                var textSize = ImGui.CalcTextSize(mapName);
                var mapPosition = atlasNode.Position * Settings.ScaleMultiplier + new Vector2(25, 0);
                var positionOffset = new Vector2(Settings.XSlider - 1500, Settings.YSlider - 1500);
                var drawPosition = (mapPosition - textSize / 2) + positionOffset;

                var group = Settings.MapGroups.Find(g => g.Maps.Exists(
                    m => NormalizeName(m).Equals(mapName, StringComparison.OrdinalIgnoreCase)));

                var backgroundColor = group?.BackgroundColor ?? Settings.DefaultBackgroundColor;
                var fontColor = group?.FontColor ?? Settings.DefaultFontColor;

                if (atlasNode.IsCompleted)
                    backgroundColor.W *= 0.6f;

                var padding = new Vector2(5, 2);
                var bgPos = drawPosition - padding;
                var bgSize = textSize + padding * 2;

                drawList.AddRectFilled(bgPos, bgPos + bgSize, ImGuiHelper.Color(backgroundColor));
                drawList.AddText(drawPosition, ImGuiHelper.Color(fontColor), mapName);

                if (Settings.DrawLinesToCitadel && mapName.EndsWith("Citadel", StringComparison.Ordinal))
                {
                    drawList.AddLine(playerLocation, drawPosition, CitadelLineColor);
                    continue;
                }

                if (Settings.DrawLinesToTowers && towers.Contains(mapName) && boundsTowers.Contains(new PointF(drawPosition.X, drawPosition.Y)))
                {
                    drawList.AddLine(playerLocation, drawPosition, TowerLineColor);
                    continue;
                }

                if (Settings.DrawLinesSearchQuery && doSearch && searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) && boundsSearch.Contains(new PointF(drawPosition.X, drawPosition.Y)))
                {
                    drawList.AddLine(playerLocation, drawPosition, SearchLineColor);
                }

                float labelCenterX = drawPosition.X + textSize.X * 0.5f;
                float nextRowTopY = drawPosition.Y + textSize.Y + 4;
                const float rowGap = 4f;

                CategorizeContents(rawContents, MapTags, MapPlain, out var flags, out var contents);

                if (Settings.ShowMapBadges)
                    DrawSquares(drawList, flags, labelCenterX, ref nextRowTopY, rowGap);

                DrawSquares(drawList, contents, labelCenterX, ref nextRowTopY, rowGap);
            }
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
                var abbrev = string.IsNullOrWhiteSpace(info.Abbrev) ? info.Label[..1] : info.Abbrev;
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
            string.IsNullOrEmpty(s) ? s : s.Replace('\u00A0', ' ').Trim();

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
            if (string.IsNullOrEmpty(contentName)) return null;

            var normalized = contentName.Replace("\u00A0", " ").Trim();

            int start = normalized.IndexOf('[');
            int sep = normalized.IndexOf('|');
            if (start >= 0 && sep > start)
            {
                string tag = normalized.Substring(start + 1, sep - start - 1);
                if (tagMap.TryGetValue(tag, out var info)) return info;
            }

            foreach (var kvp in plainMap)
            {
                if (normalized.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)) return kvp.Value;
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

        private static RectangleF calculateBounds(float range)
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
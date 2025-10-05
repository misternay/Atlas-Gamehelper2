using GameHelper.Plugin;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Atlas
{
    public sealed class AtlasSettings : IPSettings
    {
        public Vector4 DefaultBackgroundColor = new(0.0f, 0.0f, 0.0f, 0.4f);
        public Vector4 DefaultFontColor = new(1.0f, 1.0f, 1.0f, 1.0f);
        public bool ControllerMode = false;
        public string SearchQuery = string.Empty;
        public bool DrawLinesSearchQuery = true;
        public float DrawSearchInRange = 1.3f;
        public bool HideCompletedMaps = true;
        public bool HideNotAccessibleMaps = false;
        public bool HideFailedMaps = true;
        public int MaxPathNodes = 24;
        public float ScaleMultiplier = 1.0f;
        public bool DrawLinesToCitadel = false;
        public bool DrawLinesToTowers = false;
        public float DrawTowersInRange = 1.3f;
        public List<MapGroupSettings> MapGroups = [];
        public string GroupNameInput = string.Empty;
        public float XSlider = 1500.0f;
        public float YSlider = 1512.0f;
        public bool ShowMapBadges = true;
        public Dictionary<string, ContentOverride> ContentOverrides = [];

        public AtlasSettings()
        {
            var citadels = new MapGroupSettings("Citadels", new Vector4(1.0f, 0.0f, 0.0f, 0.4f), new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
            citadels.Maps.Add("The Copper Citadel");
            citadels.Maps.Add("The Iron Citadel");
            citadels.Maps.Add("The Stone Citadel");

            var pinnacleBosses = new MapGroupSettings("Pinnacle Boss", new Vector4(1.0f, 0.0f, 0.857f, 0.4f), new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
            pinnacleBosses.Maps.Add("The Burning Monolith");

            var unique = new MapGroupSettings("Unique", new Vector4(1.0f, 0.5f, 0.0f, 0.6f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            unique.Maps.Add("Untainted Paradise");
            unique.Maps.Add("Vaults of Kamasa");
            unique.Maps.Add("Moment of Zen");
            unique.Maps.Add("The Ezomyte Megaliths");
            unique.Maps.Add("Derelict Mansion");
            unique.Maps.Add("The Viridian Wildwood");
            unique.Maps.Add("The Jade Isles");
            unique.Maps.Add("Castaway");
            unique.Maps.Add("The Fractured Lake");
            unique.Maps.Add("Ice Cave");

            var good = new MapGroupSettings("Good", new Vector4(0.0f, 1.0f, 0.0f, 0.4f), new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
            good.Maps.Add("Burial Bog");
            good.Maps.Add("Creek");
            good.Maps.Add("Rustbowl");
            good.Maps.Add("Sandspit");
            good.Maps.Add("Savannah");
            good.Maps.Add("Steaming Springs");
            good.Maps.Add("Steppe");
            good.Maps.Add("Wetlands");
            good.Maps.Add("Willow");

            var towers = new MapGroupSettings("Towers", new Vector4(0.05f, 0.75f, 0.73f, 0.4f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            towers.Maps.Add("Bluff");
            towers.Maps.Add("Lost Towers");
            towers.Maps.Add("Mesa");
            towers.Maps.Add("Sinking Spire");
            towers.Maps.Add("Alpine Ridge");

            MapGroups.Add(citadels);
            MapGroups.Add(towers);
            MapGroups.Add(pinnacleBosses);
            MapGroups.Add(good);
            MapGroups.Add(unique);
        }
    }

    public class MapGroupSettings(string name, Vector4 backgroundColor, Vector4 fontColor)
    {
        public string Name = name;
        public Vector4 BackgroundColor = backgroundColor;
        public Vector4 FontColor = fontColor;
        public List<string> Maps = [];
        public string MapNameInput = string.Empty;
    }

    public class ContentOverride
    {
        public Vector4? BackgroundColor { get; set; }
        public Vector4? FontColor { get; set; }
        public bool? Show { get; set; }
        public string Abbrev { get; set; }
    }
}
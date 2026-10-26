using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RealVRFishingPCMode
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.finalpantasy.realvrfishing.pcmode";
        public const string Name = "Real VR Fishing PC Mode";
        public const string Version = "0.8.5";

        internal static ManualLogSource Log;
        internal static ConfigFile Cfg;
        internal static ConfigEntry<bool> DisableVR, ClickToCast, OneHanded, AllowOnlineCasting, SharpenScenery, ShowTips, CheckForUpdates, CustomCatchCard, CatchScreenMotion, HudSession,
            HudLevel, HudSeason, HudBothSeasons, HudCredits, HudSpecies, HudLifetime, HudBiggest, HudEarned;
        internal static ConfigEntry<float> HeadHeight, MouseSensitivity, FieldOfView, Zoom1FieldOfView, ZoomFieldOfView, WalkSpeed, RodLift,
            HandRight, HandUp, HandForward, PowerFillSeconds, RodFollowLook, LodBias;
        internal static ConfigEntry<string> CastMode, HelpCard, FrameCap, AimDot, StatsCorner, TutorialPanel;
        internal static bool PowerCast => CastMode.Value != "ClickSpot";

        void Awake()
        {
            Log = Logger;
            DisableVR = Config.Bind("General", "DisableVR", true, "true: play on the monitor with mouse and keyboard. false: play in your VR headset as normal (the mod switches itself off).");
            ClickToCast = Config.Bind("Fishing", "ClickToCast", true, "Cast with the mouse instead of the game's swing detection.");
            CastMode = Config.Bind("Fishing", "CastMode", "PowerBar", new ConfigDescription(
                "PowerBar: hold left-click to wind up, release to cast that far toward the crosshair. ClickSpot: click a spot on the water.",
                new AcceptableValueList<string>("PowerBar", "ClickSpot")));
            PowerFillSeconds = Config.Bind("Fishing", "PowerFillSeconds", SettingsPanel.BaseFillSeconds, "Seconds for the power bar to fill (it then drains and refills while held). F10 shows it as Power bar speed: 100% is 1.375 s.");
            // 0.7.1 made the bar 20% slower; a file still holding the old default moves to the new one
            if (Mathf.Approximately(PowerFillSeconds.Value, 1.1f)) PowerFillSeconds.Value = SettingsPanel.BaseFillSeconds;
            AllowOnlineCasting = Config.Bind("Fishing", "AllowOnlineCasting", false, "Allow click-to-cast in online multiplayer rooms.");
            OneHanded = Config.Bind("Fishing", "OneHanded", true, "Forces one-handed reeling: holding the reel button reels. The game's two-handed reeling needs a VR hand turning the crank and does not work with a mouse or controller, so leave this on.");
            RodLift = Config.Bind("Fishing", "RodLift", 70f, "Degrees the rod tip points above level while you look straight ahead.");
            RodFollowLook = Config.Bind("Fishing", "RodFollowLook", 0.3f, "How much the rod tilts with your view when you look up or down (0 = not at all, 1 = fully).");
            HandRight = Config.Bind("Fishing", "HandRight", 0.28f, "Rod hand distance to the side of your eyes, meters.");
            HandUp = Config.Bind("Fishing", "HandUp", -0.18f, "Rod hand height relative to your eyes, meters (negative is below).");
            HandForward = Config.Bind("Fishing", "HandForward", 0.40f, "Rod hand distance in front of your eyes, meters.");
            // the zoom button steps: normal, first zoom, second zoom, back to normal
            Zoom1FieldOfView = Config.Bind("View", "Zoom1FieldOfView", 50f, "Vertical field of view after one press of the zoom button.");
            ZoomFieldOfView = Config.Bind("View", "ZoomFieldOfView", 30f, "Vertical field of view after a second press of the zoom button.");
            SharpenScenery = Config.Bind("Graphics", "SharpenScenery", true, "Give large scenery textures mipmaps and 16x filtering so distant shores and skylines stop shimmering. Uses up to 1 GB of video memory.");
            LodBias = Config.Bind("Graphics", "LodBias", 2f, "Level-of-detail distance multiplier (the game ships 0.7). Higher keeps detailed models farther away.");
            HeadHeight = Config.Bind("View", "HeadHeight", 1.70f, "Eye height above the floor in meters (1.70 m is about 5 ft 7 in).");
            MouseSensitivity = Config.Bind("View", "MouseSensitivity", 0.12f, "Degrees of turn per pixel of mouse movement.");
            FieldOfView = Config.Bind("View", "FieldOfView", 75f, "Vertical field of view in degrees.");
            WalkSpeed = Config.Bind("Movement", "WalkSpeed", 2.0f, "Walking speed in the lodge, meters per second.");
            ShowTips = Config.Bind("General", "ShowTips", true, "Show the three-step tips card when the game starts. It turns itself off once closed.");
            HelpCard = Config.Bind("General", "ControlsCard", "Full", new ConfigDescription(
                "The controls card at the top left: Full, Small (the same card at 70% size) or Hidden. F1 steps through them in game.",
                new AcceptableValueList<string>("Full", "Small", "Hidden")));
            CustomCatchCard = Config.Bind("Fishing", "CustomCatchCard", false, "Show each catch on a full-screen catch screen instead of the game's floating board: your live fish (mouse turns it, wheel zooms), a size scale from the smallest to the biggest of its kind with your catch and your best, your stats, and what Keep and Release earn. Space keeps it, Backspace releases it, H switches to the game's board.");
            CatchScreenMotion = Config.Bind("Fishing", "CatchScreenAnimations", true, "The catch screen slides in, the fish swims off when you release it or drops into your keep when you keep it, and what you earned floats up. false: it opens and closes at once.");
            AimDot = Config.Bind("View", "AimDot", "Auto", new ConfigDescription(
                "The dot in the middle of the screen. Auto shows it for aiming a cast and pointing at menus, and hides it while your line is out.",
                new AcceptableValueList<string>("Auto", "On", "Off")));
            StatsCorner = Config.Bind("View", "StatsCorner", "Always", new ConfigDescription(
                "The stats corner at the top right: level and progress, season score and credits. On change shows it for a few seconds when one of them changes.",
                new AcceptableValueList<string>("Always", "On change", "Off")));
            TutorialPanel = Config.Bind("View", "TutorialPanel", "Left", new ConfigDescription(
                "Where the game's tutorial instructions sit on screen: pinned to the left or right side as you look around, or in front of you.",
                new AcceptableValueList<string>("Left", "Right", "Center")));
            HudLevel = Config.Bind("View", "StatsCornerLevel", true, "The stats corner shows your level and experience: how much you have toward the next level, and past the top at the highest level.");
            HudSeason = Config.Bind("View", "StatsCornerSeason", true, "The stats corner shows your season score for this water (freshwater or saltwater).");
            HudBothSeasons = Config.Bind("View", "StatsCornerBothSeasons", false, "The stats corner shows both season scores, freshwater and saltwater, wherever you fish.");
            HudCredits = Config.Bind("View", "StatsCornerCredits", true, "The stats corner shows your credits.");
            HudSpecies = Config.Bind("View", "StatsCornerSpecies", false, "The stats corner shows how many kinds of fish you have caught, out of all of them.");
            HudLifetime = Config.Bind("View", "StatsCornerLifetime", false, "The stats corner shows how many fish you have caught in all.");
            HudSession = Config.Bind("View", "StatsCornerSession", true, "The stats corner also shows this session: fish caught and time on the water.");
            HudBiggest = Config.Bind("View", "StatsCornerBiggest", true, "This session in the stats corner includes the biggest fish so far.");
            HudEarned = Config.Bind("View", "StatsCornerEarned", false, "This session in the stats corner includes the credits, experience and season points earned so far.");
            FrameCap = Config.Bind("Graphics", "FrameRateCap", "Monitor", new ConfigDescription(
                "Most frames per second. Monitor waits for each screen refresh (V-Sync). The game's line physics runs 100 times a second, and far higher frame rates make the line twitch.",
                new AcceptableValueList<string>("Monitor", "60", "90", "120", "144", "Off")));
            CheckForUpdates = Config.Bind("General", "CheckForUpdates", true, "Ask GitHub once per start whether a newer version is out; F10 then shows a link to it.");
            Pad.Init(Config);
            Keys.Init(Config);
            // the F10 panel changes values every frame while a slider is dragged; it saves once when it closes
            Cfg = Config;
            Config.SaveOnConfigSet = false;

            if (!DisableVR.Value)
            {
                // the patcher reads the same setting and leaves VR alone; play in the headset with the mod idle
                Log.LogInfo($"{Name} {Version}: DisableVR is false, so VR stays on and the mod does nothing");
                return;
            }
            // the OpenXR loader reads this when the game starts XR; a missing file means no runtime, so no SteamVR
            Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", Path.Combine(Paths.PluginPath, "no-vr-runtime.json"));

            var harmony = new Harmony(Guid);
            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception e) { Log.LogError($"patch {type.Name} failed: {e.Message}"); }
            }

            var go = new GameObject("RealVRFishingPCMode");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Driver>().StartCoroutine(Updates.Check());
            go.AddComponent<Scenery>();
#if TRAILER
            go.AddComponent<Director>();
#endif
            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}

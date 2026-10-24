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
        public const string Version = "0.6.3";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> DisableVR, ClickToCast, OneHanded, AllowOnlineCasting, SharpenScenery;
        internal static ConfigEntry<float> HeadHeight, MouseSensitivity, FieldOfView, ZoomFieldOfView, WalkSpeed, RodLift,
            HandRight, HandUp, HandForward, PowerFillSeconds, RodFollowLook, LodBias;
        internal static ConfigEntry<string> CastMode;
        internal static bool PowerCast => CastMode.Value != "ClickSpot";

        void Awake()
        {
            Log = Logger;
            DisableVR = Config.Bind("General", "DisableVR", true, "true: play on the monitor with mouse and keyboard. false: play in your VR headset as normal (the mod switches itself off).");
            ClickToCast = Config.Bind("Fishing", "ClickToCast", true, "Cast with the mouse instead of the game's swing detection.");
            CastMode = Config.Bind("Fishing", "CastMode", "PowerBar", new ConfigDescription(
                "PowerBar: hold left-click to wind up, release to cast that far toward the crosshair. ClickSpot: click a spot on the water.",
                new AcceptableValueList<string>("PowerBar", "ClickSpot")));
            PowerFillSeconds = Config.Bind("Fishing", "PowerFillSeconds", 1.1f, "Seconds for the power bar to fill (it then drains and refills while held).");
            AllowOnlineCasting = Config.Bind("Fishing", "AllowOnlineCasting", false, "Allow click-to-cast in online multiplayer rooms.");
            OneHanded = Config.Bind("Fishing", "OneHanded", true, "Force one-handed mode, so holding the trigger reels.");
            RodLift = Config.Bind("Fishing", "RodLift", 70f, "Degrees the rod tip points above level while you look straight ahead.");
            RodFollowLook = Config.Bind("Fishing", "RodFollowLook", 0.3f, "How much the rod tilts with your view when you look up or down (0 = not at all, 1 = fully).");
            HandRight = Config.Bind("Fishing", "HandRight", 0.28f, "Rod hand distance to the side of your eyes, meters.");
            HandUp = Config.Bind("Fishing", "HandUp", -0.18f, "Rod hand height relative to your eyes, meters (negative is below).");
            HandForward = Config.Bind("Fishing", "HandForward", 0.40f, "Rod hand distance in front of your eyes, meters.");
            ZoomFieldOfView = Config.Bind("View", "ZoomFieldOfView", 30f, "Vertical field of view while holding Z.");
            SharpenScenery = Config.Bind("Graphics", "SharpenScenery", true, "Give large scenery textures mipmaps and 16x filtering so distant shores and skylines stop shimmering. Uses up to 1 GB of video memory.");
            LodBias = Config.Bind("Graphics", "LodBias", 2f, "Level-of-detail distance multiplier (the game ships 0.7). Higher keeps detailed models farther away.");
            HeadHeight = Config.Bind("View", "HeadHeight", 1.70f, "Eye height above the floor in meters (1.70 m is about 5 ft 7 in).");
            MouseSensitivity = Config.Bind("View", "MouseSensitivity", 0.12f, "Degrees of turn per pixel of mouse movement.");
            FieldOfView = Config.Bind("View", "FieldOfView", 75f, "Vertical field of view in degrees.");
            WalkSpeed = Config.Bind("Movement", "WalkSpeed", 2.0f, "Walking speed in the lodge, meters per second.");

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
            go.AddComponent<Driver>();
            go.AddComponent<Scenery>();
#if TRAILER
            go.AddComponent<Director>();
#endif
            Log.LogInfo($"{Name} {Version} loaded");
        }
    }
}

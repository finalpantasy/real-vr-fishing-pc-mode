using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace RealVRFishingPCMode
{
    // BepInEx runs preloader patchers as soon as Mono starts, before Unity's OpenXR plugin probes for a
    // runtime. Pointing the OpenXR loader at a missing runtime file here keeps SteamVR from launching at all.
    // Setting DisableVR = false in the mod's config (or deleting this file) plays in a headset again.
    public static class NoVRPatcher
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Initialize()
        {
            var dir = Path.GetDirectoryName(typeof(NoVRPatcher).Assembly.Location);
            if (!VRDisabled(Path.Combine(dir, Path.Combine("..", Path.Combine("config", "com.finalpantasy.realvrfishing.pcmode.cfg")))))
                return;
            Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", Path.Combine(dir, "no-vr-runtime.json"));
        }

        // the plugin's config isn't loaded yet this early, so read the one line by hand; no file means the default (true)
        static bool VRDisabled(string cfg)
        {
            try
            {
                if (!File.Exists(cfg)) return true;
                foreach (var line in File.ReadAllLines(cfg))
                {
                    var m = Regex.Match(line, @"^\s*DisableVR\s*=\s*(\w+)", RegexOptions.IgnoreCase);
                    if (m.Success) return !m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return true;
        }

        public static void Patch(AssemblyDefinition assembly) { }
    }
}

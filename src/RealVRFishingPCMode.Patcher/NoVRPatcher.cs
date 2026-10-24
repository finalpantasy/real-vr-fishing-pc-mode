using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

namespace RealVRFishingPCMode
{
    // BepInEx runs preloader patchers as soon as Mono starts, before Unity's OpenXR plugin probes for a
    // runtime. Pointing the OpenXR loader at a missing runtime file here keeps SteamVR from launching at all.
    // Delete this file from BepInEx\patchers to play in a headset again.
    public static class NoVRPatcher
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Initialize()
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(NoVRPatcher).Assembly.Location), "no-vr-runtime.json");
            Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", path);
        }

        public static void Patch(AssemblyDefinition assembly) { }
    }
}

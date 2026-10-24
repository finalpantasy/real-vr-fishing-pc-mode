using System;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace RealVRFishingPCMode
{
    // Once per start, asks GitHub for the latest release. If it is newer than this one, F10 shows a line that opens
    // the release page. The request carries nothing but the usual web headers.
    static class Updates
    {
        const string Api = "https://api.github.com/repos/finalpantasy/real-vr-fishing-pc-mode/releases/latest";
        public const string Page = "https://github.com/finalpantasy/real-vr-fishing-pc-mode/releases/latest";

        // the newer version's number, or null
        public static string Newer;

        public static IEnumerator Check()
        {
            if (!Plugin.CheckForUpdates.Value) yield break;
            // let the game finish starting first
            yield return new WaitForSecondsRealtime(10f);
            using (var req = UnityWebRequest.Get(Api))
            {
                req.timeout = 15;
                try { req.SetRequestHeader("User-Agent", "RealVRFishingPCMode/" + Plugin.Version); } catch { }
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogInfo("update check: " + req.error);
                    yield break;
                }
                var m = Regex.Match(req.downloadHandler.text, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
                if (!m.Success) yield break;
                string latest = m.Groups[1].Value;
                if (IsNewer(latest, Plugin.Version)) Newer = latest;
                Plugin.Log.LogInfo($"update check: latest {latest}, this is {Plugin.Version}" + (Newer != null ? ", update available" : ""));
            }
        }

        static bool IsNewer(string a, string b)
        {
            try { return new Version(a) > new Version(b); }
            catch { return false; }
        }
    }
}

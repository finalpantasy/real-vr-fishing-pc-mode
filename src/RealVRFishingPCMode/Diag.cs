using System;
using System.Text;
using UnityEngine;
using XRInputUtility;

namespace RealVRFishingPCMode
{
    // F9: live fishing state on screen, plus a frame-by-frame dump to the BepInEx log, for chasing rod and reel bugs.
    // Records the rod's facing right after the mod sets the hand and again just before LateUpdate, so a game script
    // that moves the rod in between shows up as a difference.
    static class Diag
    {
        public static bool On;
        static int dumpFrames;
        static Main main;
        static float nextFind, nextLine;
        static Vector3 afterApply, prevLate;
        static float peakSpin, peakAt;
        public static string Text;

        public static void Toggle()
        {
            On = !On;
            dumpFrames = On ? 30 : 0;
            Plugin.Log.LogInfo("diag " + (On ? "on" : "off"));
            if (!On) return;
            // who owns the rod: the tackle and each parent, with their components
            for (var t = Tackle(); t != null; t = t.parent)
            {
                var names = new StringBuilder();
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    names.Append(c.GetType().Name);
                    if (c is Rigidbody rb) names.Append($"(kinematic {rb.isKinematic}, interp {rb.interpolation})");
                    if (c is Behaviour b && !b.enabled) names.Append("(off)");
                    names.Append(' ');
                }
                Plugin.Log.LogInfo($"diag chain {t.name}: {names}");
            }
        }

        public static void AfterApply()
        {
            if (!On) return;
            var t = Tackle();
            if (t != null) afterApply = t.forward;
        }

        public static void BeforeLate(Driver d)
        {
            if (!On) return;
            try { Sample(d); }
            catch (Exception e) { Text = "diag error: " + e.Message; }
        }

        static Transform Tackle()
        {
            if (main == null && Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 1f;
                main = UnityEngine.Object.FindFirstObjectByType<Main>();
            }
            return main != null && main.tackleController != null ? main.tackleController.transform : null;
        }

        static void Sample(Driver d)
        {
            var tackle = Tackle();
            var fc = main != null ? main.fishingController : null;
            if (fc == null) { Text = "diag: no fishing controller in this scene"; return; }

            var fwd = tackle.forward;
            var bait = main.baitController != null ? main.baitController.transform.position : Vector3.zero;
            var tip = fc.rodDirectionPos != null ? fc.rodDirectionPos.position : Vector3.zero;
            var trig = InputUtility.Controllers[d.MainSide];
            bool pointer = false;
            var media = UnityEngine.Object.FindFirstObjectByType<MediaController>();
            if (media != null) pointer = media.IsEnterPointer();
            float slack = main.ropeController != null ? main.ropeController.LineSlack : -1f;

            var dm = UnityEngine.Object.FindFirstObjectByType<DataManager>();
            var ud = dm != null ? dm.userData : null;
            var sb = new StringBuilder();
            sb.Append($"state {fc.gameState}  type {ud?.curType}  bothHand {ud?.bothHand}  holder {fc.isHolder}\n");
            sb.Append($"trigger press {trig.Trigger.Press} value {trig.TriggerValue:F2}  fc.isReeling {fc.isReeling}  pointerOnUI {pointer}  slack {slack:F3}  dist {fc.distance:F1}\n");
            sb.Append($"rod fwd {fwd:F2}  moved since mod set it {Vector3.Angle(afterApply, fwd):F1}°  since last frame {Vector3.Angle(prevLate, fwd):F1}°\n");
            sb.Append($"tip {tip:F2}  bait {bait:F2}  bait-tip angle {Vector3.Angle(fwd, bait - tip):F1}°\n");
            // the game twitches the lure when this passes 60
            float spin = InputUtility.HandVelocity(d.MainSide).Angular.sqrMagnitude;
            if (spin > peakSpin || Time.unscaledTime > peakAt + 1f) { peakSpin = spin; peakAt = Time.unscaledTime; }
            sb.Append($"rod hand spin² {spin:F0} (1 s peak {peakSpin:F0}, twitch over 60)  twitching {main.baitController?.isTwitching}");
            Text = sb.ToString();

            if (dumpFrames > 0)
            {
                dumpFrames--;
                Plugin.Log.LogInfo($"diag f{Time.frameCount} " + Text.Replace("\n", " | "));
            }
            else if (Time.unscaledTime >= nextLine)
            {
                nextLine = Time.unscaledTime + 1f;
                Plugin.Log.LogInfo("diag " + Text.Replace("\n", " | "));
            }
            prevLate = fwd;
        }
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealVRFishingPCMode
{
    // The game's fishing tutorial, made playable on a monitor: it switches you to two-handed reeling (which needs a VR
    // hand on the crank), and its instructions open at waist height for a headset wearer to look down at. The steps,
    // checks and rewards are the game's own; nothing here skips or completes any of it.
    static class Tutorial
    {
        // the tutorial's start sets two-handed reeling; with one-handed on, it stays one-handed
        [HarmonyPatch]
        static class OneHand
        {
            static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(TutorialController), "StageTutorialStartRoutine"));
                yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(TutorialController), "StartTutorialDelay"));
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, System.Reflection.MethodBase original)
            {
                var list = new List<CodeInstruction>(code);
                var set = AccessTools.PropertySetter(typeof(UserDataBase), nameof(UserDataBase.bothHand));
                int found = 0;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Calls(set))
                    {
                        list[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(Tutorial), nameof(SetBothHand))).MoveLabelsFrom(list[i]);
                        found++;
                    }
                if (found == 0) Plugin.Log.LogWarning("tutorial: found no two-handed switch in " + original.DeclaringType.Name);
                return list;
            }
        }

        static void SetBothHand(UserDataBase data, bool value)
        {
            if (value && Plugin.OneHanded.Value) { Plugin.Log.LogInfo("tutorial: kept one-handed reeling"); value = false; }
            data.bothHand = value;
        }

        // the instructions panel. Left or Right pins it to that side of the screen, where it stays as you look around
        // (its video, which shows VR hand motions, steps aside); Center puts it in front of you at eye level
        static DUG.UI.DUGUIPanelBase docked;
        static Canvas video;
        static readonly AccessTools.FieldRef<TutorialController, Canvas> VideoCanvas = AccessTools.FieldRefAccess<TutorialController, Canvas>("videoCanvas");
        static readonly Vector3[] corners = new Vector3[4];

        [HarmonyPatch(typeof(DUG.UI.DUGUIPanelBase), nameof(DUG.UI.DUGUIPanelBase.Open))]
        static class Panel
        {
            static void Postfix(DUG.UI.DUGUIPanelBase __instance)
            {
                try
                {
                    var tc = __instance.GetComponentInParent<TutorialController>();
                    if (tc == null) return;
                    if (Plugin.TutorialPanel.Value == "Center") { PlaceInFront(__instance); return; }
                    docked = __instance;
                    video = VideoCanvas(tc);
                    // the panel would drift after your head on its own; it is placed every frame instead
                    __instance.isFollow = __instance.isHeadTracking = __instance.isLookAt = false;
                    Plugin.Log.LogInfo("tutorial: instructions pinned to the " + Plugin.TutorialPanel.Value.ToLower() + " of the screen");
                }
                catch (Exception e) { Plugin.Log.LogWarning("tutorial panel: " + e.Message); }
            }
        }

        // every frame, after the camera is posed
        internal static void Follow()
        {
            if (docked == null) return;
            try
            {
                if (!docked.isOpen || !docked.gameObject.activeInHierarchy || Plugin.TutorialPanel.Value == "Center") { docked = null; return; }
                var cam = Camera.main;
                if (cam == null) return;
                if (video != null && video.enabled) video.enabled = false;

                // the panel's visible size, in its own space
                var t = docked.transform;
                Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
                foreach (var c in docked.GetComponentsInChildren<Canvas>())
                {
                    if (!c.enabled || c == video) continue;
                    ((RectTransform)c.transform).GetWorldCorners(corners);
                    foreach (var w in corners) { var l = t.InverseTransformPoint(w); min = Vector2.Min(min, l); max = Vector2.Max(max, l); }
                }
                if (max.x <= min.x) return;
                float s = t.lossyScale.x, width = (max.x - min.x) * s, height = (max.y - min.y) * s;

                // a distance where it takes 40% of the screen's width (or 40% of its height), at the side, a little above the middle
                float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), tanH = tanV * cam.aspect;
                float d = Mathf.Clamp(Mathf.Max(width / (2f * tanH * 0.4f), height / (2f * tanV * 0.4f)), 0.3f, 5f);
                float side = Plugin.TutorialPanel.Value == "Right" ? 1f : -1f;
                var at = new Vector3(side * (d * tanH * 0.95f - width * 0.5f), d * tanV * 0.12f, d);
                var head = cam.transform;
                t.rotation = head.rotation;
                t.position = head.position + head.rotation * at - t.rotation * Vector3.Scale((min + max) * 0.5f, t.lossyScale);
            }
            catch (Exception e) { Plugin.Log.LogWarning("tutorial panel: " + e.Message); docked = null; }
        }

        // in front of you at eye level, a little below the middle, where it stays in the world
        static void PlaceInFront(DUG.UI.DUGUIPanelBase panel)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var head = cam.transform;
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float d = 1.2f;
            var at = new Vector3(0f, -d * tanV * 0.25f, d);
            var pos = head.position + Quaternion.Euler(0f, head.eulerAngles.y, 0f) * at;
            panel.transform.position = pos;
            var look = pos - head.position;
            look.y = 0f;
            if (look.sqrMagnitude > 0.001f) panel.transform.rotation = Quaternion.LookRotation(look);
            // panels that follow your head keep this place as you turn
            panel.SetOffset(at);
            if (!panel.isFollow && !panel.isHeadTracking) panel.UpdateOffset();
        }
    }
}

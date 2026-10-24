using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealVRFishingPCMode
{
    // An optional full-screen catch screen in place of the game's floating board: the scene dimmed behind, the
    // fish's name and details at the top, a scale from the smallest to the biggest of its kind, the live fish
    // across the middle (mouse or right stick turns it, wheel or triggers zoom), your stats under it, and Keep
    // (credits) and Release (experience) at the bottom. H swaps it for the game's own board.
    static class CatchCard
    {
        const int Layer = 31;
        static ScoreController sc;
        static fishSetScript held;
        static CanvasGroup group;
        static float length, fishGrams, best, lo, hi, bestBefore = -1f;
        static bool chosen, hidden, prevKeep, prevRelease, prevToggle;
        static Texture2D vignette;
        static readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();

        // the live view: a camera drawn over the game's that sees only the fish and a dimming screen behind it
        static Camera view;
        static Renderer[] fishParts;
        static int[] fishLayers;
        static LineRenderer line;
        static Transform anchor;
        static Vector3 centerLocal, lengthLocal, upLocal;
        static float fishLen, fishTall, turn, tilt, zoom = 1f, lastTurn, opened;
        static bool live, dimInWorld, reported;
        static int placed;
#if RESCUETEST
        static long showStart;
        static double showMs;
#endif
        static readonly List<(Camera cam, int mask)> masked = new List<(Camera, int)>();
        static Rect fishRect;

        static readonly Color Green = new Color(0.56f, 0.78f, 0.24f), Yellow = new Color(0.98f, 0.78f, 0.18f),
            Blue = new Color(0.33f, 0.64f, 0.96f), Purple = new Color(0.62f, 0.45f, 0.92f), Orange = new Color(0.96f, 0.55f, 0.25f),
            BestText = new Color(1f, 0.74f, 0.50f), Tile = new Color(0.13f, 0.14f, 0.15f, 0.88f), ChipFill = new Color(0.07f, 0.08f, 0.09f, 0.88f);

        // after Keep or Release: the screen leaves over OutTime, then the game takes the choice while what you
        // earned floats up for PopTime; the game's own board stays hidden until the game closes it
        const float OutTime = 0.35f, PopTime = 1.3f;
        static float chosenAt = -10f;
        static bool released, pending, done, holding;
        static string popText;
        static Color popColor;
        static bool Motion => Plugin.CatchScreenMotion.Value;

        // the screen is up and owns the input (until the key that chose is let go)
        public static bool Showing => sc != null && !PhotoMode.On && (!done || holding);
        public static bool Drawing => sc != null && !PhotoMode.On;
        public static bool Hidden => hidden;

        // achievements and level-ups the game announces around a catch: its own popup floats behind this screen,
        // so they show here too, down the left side
        static readonly List<(string kind, string title, string detail, float at)> awards = new List<(string, string, string, float)>();

        [HarmonyPatch(typeof(AchievementController), "PopUp")]
        static class Achieved
        {
            static void Prefix(AchievementController __instance, int id)
            {
                try
                {
                    var a = __instance.achievements[id];
                    int grade = Singleton<DataManager>.Instance.userData.achievements.grade[id];
                    string name = Miragesoft.Localization.LocalizationManager.GetKeyTranslation(a.nameKey);
                    string reward = grade == __instance.rewardGrade && a.reward.ToString() == "GOLD" ? $", +{a.rewardGold} credits" : "";
                    Award("ACHIEVEMENT", string.IsNullOrEmpty(name) ? a.title : name, $"Grade {grade} of {__instance.rewardGrade}{reward}");
                }
                catch (Exception e) { Plugin.Log.LogWarning("catch card achievement: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(AchievementController), "PopLevelUp")]
        static class LevelUp
        {
            static void Prefix(int lv) => Award("LEVEL UP", "Level " + lv, null);
        }

        static void Award(string kind, string title, string detail)
        {
            awards.Add((kind, title, detail, Time.unscaledTime));
            Plugin.Log.LogInfo($"catch card: {kind.ToLowerInvariant()}: {title} {detail}");
        }

        // before the game records this catch as your best
        [HarmonyPatch(typeof(FishingController), "SelectCatchOrRelease")]
        static class Before
        {
            static void Prefix()
            {
                bestBefore = -1f;
                try { bestBefore = Singleton<DataManager>.Instance.userData.fishes.maxSize[Singleton<Main>.Instance.fishSpawnController.fish.fishInfo.ID]; }
                catch { }
            }
        }

        [HarmonyPatch(typeof(ScoreController), "ScoreShow")]
        static class After
        {
#if RESCUETEST
            static void Prefix() => showStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            static void Postfix(ScoreController __instance, FishItem fish, float fishLength, float fishWeight)
            {
#if RESCUETEST
                showMs = (System.Diagnostics.Stopwatch.GetTimestamp() - showStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
#endif
                if (!Plugin.CustomCatchCard.Value || !Plugin.DisableVR.Value) return;
                try { Open(__instance, fish, fishLength, fishWeight); }
                catch (Exception e) { Plugin.Log.LogWarning("catch card: " + e); Close(); }
            }
        }

        static void Open(ScoreController s, FishItem f, float len, float grams)
        {
#if RESCUETEST
            long openStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            var main = Singleton<Main>.Instance;
            var data = Singleton<DataManager>.Instance;
            var item = data.itemDatabase.fishes.Find(o => o.ID == f.ID) ?? f;
            // the game rolls a size as basicSize times this curve, clamped to minSize below and the curve at 100 above
            var graph = main.fishSpawnController.fishSizeGraph;
            float low = float.MaxValue;
            for (int t = 0; t <= 100; t++) low = Mathf.Min(low, graph.Evaluate(t));
            lo = Mathf.Max(item.minSize, item.basicSize * low);
            hi = Mathf.Max(lo + 0.1f, item.basicSize * graph.Evaluate(100f), len);
            best = bestBefore >= 0f ? bestBefore : data.userData.fishes.maxSize[f.ID];
            length = len;
            fishGrams = grams > 0f ? grams : Grams(len);
            EndView();
            sc = s;
            held = main.fishSpawnController.fish;
            group = s.canvas.GetComponent<CanvasGroup>();
            if (group == null) group = s.canvas.gameObject.AddComponent<CanvasGroup>();
            chosen = hidden = pending = done = holding = false;
            chosenAt = -10f; popText = null;
            turn = tilt = 0f; zoom = 1f; placed = 0; reported = false;
            lastTurn = -10f; opened = Time.unscaledTime;
            // ones the game announced while the fish came in still count
            awards.RemoveAll(w => opened - w.at > 8f);
#if RESCUETEST
            // test build: a sample achievement on arrival and a level-up two seconds later, to see how they look
            if (awards.Count == 0)
            {
                string sample = "Beautiful Sunset";
                try
                {
                    var ac = Singleton<AchievementController>.Instance;
                    string n = Miragesoft.Localization.LocalizationManager.GetKeyTranslation(ac.achievements[0].nameKey);
                    if (!string.IsNullOrEmpty(n)) sample = n;
                }
                catch { }
                awards.Add(("ACHIEVEMENT", sample, "Grade 2 of 5 (example)", opened));
                awards.Add(("LEVEL UP", "Level 5", "(example)", opened + 2f));
            }
#endif
            // a key still held from landing the fish doesn't count
            prevKeep = prevRelease = prevToggle = true;
            try { SetUpView(); }
            catch (Exception e) { Plugin.Log.LogWarning("catch card view: " + e.Message); EndView(); }
            Plugin.Log.LogInfo($"catch card: fish {f.ID} {len:F1} cm, species {lo:F1} to {hi:F1} cm, best before {best:F1} cm, live view {anchor != null}, dim {dimInWorld}");
#if RESCUETEST
            Plugin.Log.LogInfo($"catch card timing: game's board {showMs:F1} ms, this screen {(System.Diagnostics.Stopwatch.GetTimestamp() - openStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms");
#endif
        }

        static void Close()
        {
            if (done) Plugin.Log.LogInfo($"catch card: the game was ready {Time.unscaledTime - chosenAt:F2} s after the choice");
            if (group != null) { group.alpha = 1f; group.interactable = group.blocksRaycasts = true; }
            EndView();
            sc = null; held = null; group = null;
            awards.Clear();
        }

        // once per frame from the driver
        public static void Tick(bool focused)
        {
            if (sc == null)
            {
                if (view == null && Plugin.CustomCatchCard.Value) BuildView();
                return;
            }
#if RESCUETEST
            if (Time.unscaledTime - opened < 2f && Time.unscaledDeltaTime > 0.03f)
                Plugin.Log.LogInfo($"catch card slow frame {Time.unscaledTime - opened:F2} s in: {Time.unscaledDeltaTime * 1000f:F0} ms");
#endif
            // photo mode hides and moves the game's board itself, so the card steps aside until it ends
            bool photo = PhotoMode.On;
            if (!photo && (sc.canvas == null || !sc.isScoreVisible || !Plugin.CustomCatchCard.Value)) { Close(); return; }
            if (pending && Time.unscaledTime - chosenAt >= OutTime) Commit();
            if (done)
            {
                // gone; the game's board stays out of sight until the game puts it away
                group.alpha = 0f;
                group.interactable = group.blocksRaycasts = false;
                holding = focused && (Win.Down(0x01) || Keys.Keep.Down || Keys.Release.Down || Pad.InUse && (Pad.Down(Pad.Button.A) || Pad.Down(Pad.Button.B)));
                return;
            }
            bool own = !photo && !hidden;
            // the game's board is there whenever this screen isn't
            group.alpha = own ? 0f : 1f;
            group.interactable = group.blocksRaycasts = !own;
            bool input = focused && !photo;
            bool k = input && !hidden && (Keys.Keep.Down || Pad.InUse && Pad.Down(Pad.Button.A));
            bool r = input && !hidden && (Keys.Release.Down || Pad.InUse && Pad.Down(Pad.Button.B));
            bool h = input && (Keys.CardToggle.Down || Pad.InUse && Pad.Down(Pad.Button.Y));
            if (!chosen && k && !prevKeep) Choose(false);
            else if (!chosen && r && !prevRelease) Choose(true);
            if (h && !prevToggle && !chosen) hidden = !hidden;
            prevKeep = k; prevRelease = r; prevToggle = h;
            if (own && focused)
            {
                // wheel or triggers zoom the fish
                float z = Win.Wheel() * 0.12f;
                if (Pad.InUse) z += (Pad.Amount(Pad.Button.RT) - Pad.Amount(Pad.Button.LT)) * 1.5f * Time.unscaledDeltaTime;
                zoom = Mathf.Clamp(zoom * (1f + z), 0.6f, 3f);
            }
            Live(own);
            // once per catch, what the view is doing, to find a fish that doesn't show
            if (live && !reported && Time.unscaledTime - opened > 1f)
            {
                reported = true;
                var sb = new System.Text.StringBuilder($"catch card view: anchor {(anchor != null ? anchor.name : "none")}, placed {placed} times, enabled {view.isActiveAndEnabled}, at {view.transform.position:F2} looking {view.transform.forward:F2}, fish at {(anchor != null ? anchor.TransformPoint(centerLocal) : Vector3.zero):F2}, size {fishLen:F2} x {fishTall:F2}, parts");
                foreach (var p in fishParts) if (p != null) sb.Append($" {p.name}[{p.gameObject.layer} {(p.enabled ? "on" : "off")} {p.isVisible}]");
                sb.Append(" | cameras");
                foreach (var c in Camera.allCameras) sb.Append($" {c.name}[depth {c.depth} 31:{(c.cullingMask >> Layer) & 1} {(c.targetTexture != null ? "to texture" : "to screen")}]");
                Plugin.Log.LogInfo(sb.ToString());
            }
        }

        // mouse or right stick movement while the screen is up turns the fish instead of your head
        public static bool TurnFish(float dx, float dy)
        {
            if (sc == null || !live) return false;
            if (dx == 0f && dy == 0f) return true;
            turn += dx * 0.35f;
            tilt = Mathf.Clamp(tilt + dy * 0.35f, -70f, 70f);
            lastTurn = Time.unscaledTime;
            return true;
        }

        static void Choose(bool release)
        {
            // the float tutorial takes the choice only once it has asked for it, as its own buttons do
            var tc = Singleton<Main>.Instance.tutorialController;
            if (Singleton<Main>.Instance.useTutorial && tc.SelectedTutorial == InteractiveTutorialType.Float && !tc.PhaseCheck(FloatFlow.FishCreditAndExp)) return;
            chosen = true;
            released = release;
            chosenAt = Time.unscaledTime;
            popText = release ? ReleaseReward() : KeepReward();
            popColor = release ? Green : Yellow;
            Plugin.Log.LogInfo("catch card: " + (release ? "release" : "keep"));
            // the game lets the fish go as soon as it hears, so it waits until the screen has finished leaving
            if (Motion) pending = true;
            else Commit();
        }

        static void Commit()
        {
            pending = false;
            done = holding = true;
            EndView();
            try
            {
                var t = Traverse.Create(sc);
                // what the game's own Keep and Release buttons do around the choice (ScoreController.UIButtonDown):
                // the championship reward, the tutorial steps, and on release the Mexico release missions
                var main = Singleton<Main>.Instance;
                var tc = main.tutorialController;
                var fish = main.fishSpawnController.fish.fishInfo;
                if (StageInfoManager.CurStageItem != null && StageInfoManager.CurStageItem.regionType == RegionType.Championship && sc.panelPivot != null)
                    EventMgr.Instance.Publish(EventKeys.RewardChampionshipUI, sc.panelPivot.transform.position);
                if (main.useTutorial && tc.SelectedTutorial == InteractiveTutorialType.Float && tc.PhaseCheck(FloatFlow.FishCreditAndExp))
                    tc.CompleteToastPopupForPhase(FloatFlow.FishCreditAndExp);
                if (!released && fish.isMegaShark)
                {
                    t.Method("scoreHide").GetValue();
                    t.Method("InfoTextEnable", true).GetValue();
                }
                t.Method("ReleaseOrKeep", released).GetValue();
                TutorialPhase.InteractiveOpenMenu();
                if (tc.isDemoTutorial)
                {
                    tc.SetDemoPhase(MetaDemoFlow.Ending);
                    sc.StartCoroutine(t.Method("CloseDemoPanel", 9f).GetValue<System.Collections.IEnumerator>());
                }
                if (released) t.Method("CheckMissionProgress", fish).GetValue();
                var dropClosed = t.Field("m_onDropItemClosed");
                if (dropClosed.GetValue() is Action closed) { dropClosed.SetValue(null); closed(); }
                // the board's XP bar is out of sight, so it jumps to the end instead of holding up the next cast
                t.Method("StopExpGaugeAndSnap").GetValue();
            }
            catch (Exception e) { Plugin.Log.LogWarning("catch card choice: " + e.Message); chosen = done = holding = false; popText = null; }
        }

        // the game's pause after Keep or Release, there so its board can show the reward; this screen has already
        // shown it, so a choice made here waits only a moment
        internal static float AfterChoiceWait() => done ? 0.3f : 2f;

        [HarmonyPatch]
        static class ShortWait
        {
            static System.Reflection.MethodBase TargetMethod() =>
                AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ScoreController), "doKeepOrRelease"));

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var list = new List<CodeInstruction>(code);
                var wait = AccessTools.Constructor(typeof(WaitForSeconds), new[] { typeof(float) });
                int found = 0;
                for (int i = 0; i + 1 < list.Count; i++)
                    if (list[i].opcode == System.Reflection.Emit.OpCodes.Ldc_R4 && list[i].operand is float f && f == 2f
                        && list[i + 1].opcode == System.Reflection.Emit.OpCodes.Newobj && Equals(list[i + 1].operand, wait))
                    {
                        list[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(CatchCard), nameof(AfterChoiceWait))).MoveLabelsFrom(list[i]);
                        found++;
                    }
                if (found != 1) Plugin.Log.LogWarning($"catch card: found {found} waits after Keep or Release, expected 1");
                return list;
            }
        }

        static float Ease(float t) { t = Mathf.Clamp01(t); return 1f - (1f - t) * (1f - t) * (1f - t); }

        // eases out past the end and settles back, for things that pop in
        static float EaseBack(float t)
        {
            t = Mathf.Clamp01(t) - 1f;
            return 1f + 2.7f * t * t * t + 1.7f * t * t;
        }

        static float Leaving => chosen && Motion ? Mathf.Clamp01((Time.unscaledTime - chosenAt) / OutTime) : 0f;

        // The fish's own model, as it moves in your hand, drawn by a second camera over the game's. Its parts move
        // to a layer only that camera draws, so the game's camera stops drawing it where your hand is.
        static void SetUpView()
        {
            if (held == null) return;
            fishParts = Array.FindAll(held.GetComponentsInChildren<Renderer>(), r => (r is SkinnedMeshRenderer || r is MeshRenderer) && r.enabled && !IsHand(r.transform));
            Renderer body = null;
            float vol = 0f;
            foreach (var r in fishParts)
            {
                var sz = r.bounds.size;
                if (sz.x * sz.y * sz.z > vol) { vol = sz.x * sz.y * sz.z; body = r; }
            }
            if (body == null) return;
            var mesh = body is SkinnedMeshRenderer smr ? smr.sharedMesh : body.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) return;

            // the model's longest side is its length and its shortest is its thickness; the middle one is up
            var size = Vector3.Scale(mesh.bounds.size, body.transform.lossyScale);
            size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            int along = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            int thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            if (thin == along) thin = (along + 1) % 3;
            int mid = 3 - along - thin;
            Vector3 Axis(int i) => body.transform.TransformDirection(i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward);
            var lengthDir = Axis(along);
            var center = body.bounds.center;
            // it hangs by the mouth, so the head is the end nearer the lure
            var lure = Singleton<Main>.Instance.baitController;
            if (lure != null && Vector3.Dot(lure.transform.position - center, lengthDir) < 0f) lengthDir = -lengthDir;
            // follow the bone the model bends from, so the view stays on the fish as it moves in your hand
            anchor = (body as SkinnedMeshRenderer)?.rootBone ?? body.transform;
            centerLocal = anchor.InverseTransformPoint(center);
            lengthLocal = anchor.InverseTransformDirection(lengthDir);
            upLocal = anchor.InverseTransformDirection(Axis(mid));
            fishLen = size[along]; fishTall = size[mid];

            BuildView();
            line = Traverse.Create(held).Field("line").GetValue<LineRenderer>();
            fishLayers = new int[fishParts.Length];
            for (int i = 0; i < fishParts.Length; i++) fishLayers[i] = fishParts[i].gameObject.layer;
            Camera.onPreCull += Place;
        }

        // the view's camera, light and backdrop are made once, before the first catch, and kept switched off
        // between catches, so opening the screen doesn't stop the game to build them
        static void BuildView()
        {
            if (view != null) return;
            var go = new GameObject("CatchCardView") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            view = go.AddComponent<Camera>();
            go.SetActive(false);
            view.clearFlags = CameraClearFlags.Depth;
            view.cullingMask = 1 << Layer;
            view.depth = 100f;
            view.fieldOfView = 20f;
            view.allowHDR = false;
            var lg = new GameObject("CatchCardLight") { hideFlags = HideFlags.HideAndDontSave };
            lg.transform.SetParent(go.transform, false);
            lg.transform.localRotation = Quaternion.Euler(35f, -25f, 0f);
            var lamp = lg.AddComponent<Light>();
            lamp.type = LightType.Directional;
            lamp.cullingMask = 1 << Layer;
            lamp.intensity = 0.8f;
            lamp.shadows = LightShadows.None;

            // the dimmed scene: two screens behind the fish, a flat shade and a darker rim
            var shader = Shader.Find("Sprites/Default");
            dimInWorld = shader != null;
            if (dimInWorld)
            {
                Backdrop(go.transform, "CatchCardDim", shader, Texture2D.whiteTexture, new Color(0f, 0f, 0f, 0.8f), 0);
                Backdrop(go.transform, "CatchCardRim", shader, Vignette(), Color.black, 1);
            }
        }

        static bool IsHand(Transform t)
        {
            for (; t != null && t != held.transform; t = t.parent)
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("glove") || n.Contains("hand")) return true;
            }
            return false;
        }

        static void Backdrop(Transform parent, string name, Shader shader, Texture tex, Color c, int order)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(q.GetComponent<Collider>());
            q.name = name;
            q.hideFlags = HideFlags.HideAndDontSave;
            q.layer = Layer;
            q.transform.SetParent(parent, false);
            var mat = new Material(shader) { mainTexture = tex, color = c, renderQueue = 1000 + order };
            var mr = q.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // moves the fish onto the view's layer (and back) as the screen shows and hides
        static void Live(bool on)
        {
            on &= view != null && anchor != null && fishParts != null;
            if (on == live) return;
            live = on;
            if (view != null) view.gameObject.SetActive(on);
            if (fishParts != null)
                for (int i = 0; i < fishParts.Length; i++)
                    if (fishParts[i] != null) fishParts[i].gameObject.layer = on ? Layer : fishLayers[i];
            if (line != null) line.enabled = !on;
            if (on)
            {
                masked.Clear();
                foreach (var c in Camera.allCameras)
                    if (c != view && (c.cullingMask & (1 << Layer)) != 0) { masked.Add((c, c.cullingMask)); c.cullingMask &= ~(1 << Layer); }
            }
            else
            {
                foreach (var (c, mask) in masked) if (c != null) c.cullingMask = mask;
                masked.Clear();
            }
        }

        static void EndView()
        {
            Live(false);
            Camera.onPreCull -= Place;
            anchor = null; fishParts = null; line = null;
        }

        // just before the view draws, put it beside the fish wherever the hand has it now
        static void Place(Camera c)
        {
            if (c != view || anchor == null) return;
            placed++;
            float sw = Screen.width, sh = Screen.height;
            if (fishRect.width < 8f) Layout(sw, sh);
            var lengthDir = anchor.TransformDirection(lengthLocal).normalized;
            var up = Vector3.ProjectOnPlane(anchor.TransformDirection(upLocal), lengthDir).normalized;
            var right = -lengthDir;
            var fwd = Vector3.Cross(right, up);
            var center = anchor.TransformPoint(centerLocal);
            // a slow sway until it's turned by hand
            float idle = Mathf.Clamp01((Time.unscaledTime - lastTurn - 2f) / 2f);
            float sway = Mathf.Sin((Time.unscaledTime - opened) * 0.5f) * 14f * idle;
            // coming in it grows and turns to face you; going out a released fish swims off headfirst and a kept one drops away
            float z = zoom, spin = 0f, dx = 0f, dy = 0f, dim = 1f;
            if (Motion)
            {
                float t = Time.unscaledTime - opened, e = Leaving;
                z *= Mathf.Lerp(0.55f, 1f, EaseBack(t / 0.6f));
                spin = (1f - Ease(t / 0.6f)) * 110f;
                dim = Ease(t / 0.3f) * (1f - e);
                if (released) dx = -e * e * sw * 0.9f;
                else { dy = e * e * sh * 0.55f; z *= 1f - 0.45f * e; }
            }
            var rot = Quaternion.AngleAxis(turn + sway + spin, up) * Quaternion.AngleAxis(tilt, right);
            var look = rot * fwd;
            var camUp = rot * up;

            // size it so the fish spans the middle of the screen, then zoom
            float wpp = Mathf.Max(fishLen * 1.1f / fishRect.width, fishTall * 1.3f / fishRect.height) / z;
            float tan = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float dist = wpp * sh / (2f * tan);
            var camRight = Vector3.Cross(camUp, look);
            view.transform.SetPositionAndRotation(center - look * dist + camUp * ((fishRect.center.y + dy - sh / 2f) * wpp) - camRight * ((fishRect.center.x + dx - sw / 2f) * wpp),
                Quaternion.LookRotation(look, camUp));
            float back = dist + fishLen * 2f;
            view.nearClipPlane = Mathf.Max(0.01f, dist - fishLen * 2f);
            view.farClipPlane = back + 1f;
            float h = 2f * back * tan * 1.2f;
            foreach (Transform q in view.transform)
            {
                var mr = q.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                bool rim = q.name == "CatchCardRim";
                q.localPosition = new Vector3(0f, 0f, back - (rim ? 0.01f : 0f));
                q.localRotation = Quaternion.identity;
                q.localScale = new Vector3(h * view.aspect, h, 1f);
                mr.sharedMaterial.color = new Color(0f, 0f, 0f, (rim ? 1f : 0.8f) * dim);
            }
        }

        // where the fish goes: across the middle, between the scale and the tiles
        static void Layout(float sw, float sh)
        {
            float top = Ui.S(200f), bottom = sh - Ui.S(270f);
            float fw = sw * 0.62f, fh = fw / 2.6f;
            if (fh > bottom - top) { fh = Mathf.Max(Ui.S(120f), bottom - top); fw = fh * 2.6f; }
            fishRect = new Rect((sw - fw) / 2f, top + (bottom - top - fh) / 2f, fw, fh);
        }

        public static void Draw()
        {
            var m = GUI.matrix;
            try
            {
                baseMatrix = m;
                DrawCard();
                Pop();
            }
            finally { Ui.Alpha = 1f; GUI.matrix = m; }
        }

        static Matrix4x4 baseMatrix;
        static float sinceOpen, fade;

        // one part of the screen arriving: it fades in and slides the last dy pixels into place, starting delay seconds in
        static void Stage(float delay, float dur, float dy)
        {
            float k = Ease((sinceOpen - delay) / dur);
            Ui.Alpha = fade * k;
            GUI.matrix = baseMatrix * Matrix4x4.Translate(new Vector3(0f, Ui.S(dy) * (1f - k), 0f));
        }

        // what Keep or Release earned, floating up from the fish as the screen leaves
        static void Pop()
        {
            float t = Time.unscaledTime - chosenAt;
            if (popText == null || !Motion || t > PopTime) return;
            float a = Mathf.Clamp01(t / 0.12f) * (1f - Mathf.Clamp01((t - PopTime + 0.45f) / 0.45f));
            float size = 40f * (1f + 0.3f * (1f - Ease(t / 0.25f)));
            float y = fishRect.center.y - Ui.S(30f) - Ease(t / PopTime) * Ui.S(80f);
            GUI.matrix = baseMatrix;
            GUIUtility.ScaleAroundPivot(Vector2.one * (size / 40f), new Vector2(Screen.width / 2f, y + Ui.S(30f)));
            var st = Ui.Style(40, true, TextAnchor.MiddleCenter);
            Ui.Alpha = a * 0.6f;
            Ui.Label(new Rect(Ui.S(2f), y + Ui.S(3f), Screen.width, Ui.S(60f)), popText, st, Color.black);
            Ui.Alpha = a;
            Ui.Label(new Rect(0f, y, Screen.width, Ui.S(60f)), popText, st, popColor);
        }

        static void DrawCard()
        {
            float sw = Screen.width, sh = Screen.height;
            if (done) return;
            sinceOpen = Motion ? Time.unscaledTime - opened : 99f;
            fade = 1f - Ease(Leaving);
            if (hidden)
            {
                // the game's board is up; just a way back
                var st = Ui.Style(15, false);
                string key = KeyName(Keys.CardToggle, Pad.Button.Y);
                float w = Ui.ChipWidth(key, 26f) + Ui.S(10f) + Ui.Width("Show the catch screen", st) + Ui.S(24f);
                var r = new Rect((sw - w) / 2f, sh - Ui.S(70f), w, Ui.S(40f));
                Ui.Box(r, ChipFill, Ui.S(4f));
                float cx = r.x + Ui.S(12f);
                cx += Ui.Chip(cx, r.y + Ui.S(7f), key, 26f) + Ui.S(10f);
                Ui.Label(new Rect(cx, r.y, r.width, r.height), "Show the catch screen", st, Ui.Text);
                return;
            }
            Layout(sw, sh);

            // the scene, dimmed and darker toward the edges (drawn behind the fish by the view when it can be)
            if (Ui.Repaint && !(live && dimInWorld))
            {
                float a = fade * Ease(sinceOpen / 0.3f);
                GUI.DrawTexture(new Rect(0f, 0f, sw, sh), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0f, 0f, 0f, 0.6f * a), 0f, 0f);
                GUI.DrawTexture(new Rect(0f, 0f, sw, sh), Vignette(), ScaleMode.StretchToFill, true, 0f, new Color(0f, 0f, 0f, a), 0f, 0f);
            }

            // name and details
            Stage(0f, 0.35f, -24f);
            float y = Ui.S(40f);
            Ui.Label(new Rect(0f, y, sw, Ui.S(56f)), Text(sc.textFishName), Ui.Style(44, false, TextAnchor.MiddleCenter), Ui.Text);
            y += Ui.S(62f);
            var chips = new List<(string icon, string text, Color fill)>
            {
                ("weight", Text(sc.textFishWeight).Trim('(', ')', ' '), ChipFill),
                ("ruler", Text(sc.textFishLength), ChipFill),
            };
            bool record = best <= 0f || length > best;
            if (record) chips.Add(("check", best > 0f ? "New best" : "First catch", new Color(0.36f, 0.62f, 0.20f, 0.95f)));
            if (sc.textFishRare != null && sc.textFishRare.gameObject.activeSelf)
            {
                var rc = sc.textFishRare.color;
                chips.Add(("star", Text(sc.textFishRare), new Color(rc.r * 0.75f, rc.g * 0.75f, rc.b * 0.75f, 0.95f)));
            }
            string epic = Text(sc.textEpicBoost);
            if (epic != "") chips.Add(("star", epic, new Color(Purple.r, Purple.g, Purple.b, 0.95f)));
            string boost = Text(sc.txt_boostEvent);
            if (boost != "") chips.Add(("coin", (boost + " " + Text(sc.txt_boostEventNum)).Trim(), new Color(0.75f, 0.5f, 0.1f, 0.95f)));
            DrawChips(sw / 2f, y, chips);
            y += Ui.S(56f);

            Stage(0.15f, 0.35f, -12f);
            Scale(sw * 0.08f, sw * 0.92f, y, record);

            // your stats
            var tiles = new List<(string glyph, Color color, string value, string caption)>();
            string score = sc.txt_seasonScore != null && sc.txt_seasonScore.enabled ? Text(sc.txt_seasonScoreNum) : "";
            if (score != "") tiles.Add(("★", Yellow, SeasonTotal().ToString("N0"), Text(sc.txt_seasonScore).ToUpperInvariant()));
            string count = Text(sc.fishCountText);
            if (count != "") tiles.Add(("#", Blue, count, "CAUGHT"));
            tiles.Add(("♦", Orange, record ? "New" : Weight(best), "YOUR BEST"));
            string lv = Text(sc.userLv);
            if (lv != "") tiles.Add(("LV", Green, lv, "LEVEL"));
            string credits = Text(sc.userGold);
            // the board writes 1618; the stats corner writes 1,618
            if (int.TryParse(credits.Trim(), out int gold)) credits = gold.ToString("N0");
            if (credits != "") tiles.Add(("C", Yellow, credits, "CREDITS"));
            float tw = Ui.S(104f), th = Ui.S(96f), gap = Ui.S(10f);
            float tx = (sw - tiles.Count * tw - (tiles.Count - 1) * gap) / 2f, ty = sh - Ui.S(246f);
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                Stage(0.35f + 0.07f * i, 0.35f, 28f);
                var tr = new Rect(tx, ty, tw, th);
                DrawTile(tr, t.glyph, t.color, t.value, t.caption, t.glyph == "★" ? score : null);
                // progress toward the next level along the level tile's foot
                if (t.caption == "LEVEL" && sc.lvSlider != null && sc.lvSlider.gameObject.activeInHierarchy)
                {
                    var bar = new Rect(tr.x + Ui.S(10f), tr.yMax - Ui.S(10f), tr.width - Ui.S(20f), Ui.S(4f));
                    Ui.Box(bar, new Color(1f, 1f, 1f, 0.12f), Ui.S(2f));
                    Ui.Box(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(sc.lvSlider.value), bar.height), Green, Ui.S(2f));
                }
                tx += tw + gap;
            }

            // achievements and level-ups, sliding in from the left as they arrive
            float ay = Ui.S(290f);
            for (int i = 0; i < awards.Count; i++)
            {
                var w = awards[i];
                float k = Ease((sinceOpen - Mathf.Max(0.5f + 0.1f * i, w.at - opened)) / 0.4f);
                Ui.Alpha = fade * k;
                GUI.matrix = baseMatrix * Matrix4x4.Translate(new Vector3(-Ui.S(40f) * (1f - k), 0f, 0f));
                ay += DrawAward(sw * 0.08f, ay, w.kind, w.title, w.detail) + Ui.S(10f);
            }

            // keep for credits, release for experience
            float bw = Ui.S(230f), bh = Ui.S(62f), by = sh - Ui.S(130f);
            float bx = (sw - 2f * bw - gap) / 2f;
            Stage(0.6f, 0.35f, 28f);
            if (ChoiceButton(new Rect(bx, by, bw, bh), "Keep", KeepReward(), Yellow, KeyName(Keys.Keep, Pad.Button.A), chosen && !released) && !chosen) Choose(false);
            if (ChoiceButton(new Rect(bx + bw + gap, by, bw, bh), "Release", ReleaseReward(), Green, KeyName(Keys.Release, Pad.Button.B), chosen && released) && !chosen) Choose(true);

            Stage(0.8f, 0.4f, 0f);
            Hints(sw, sh - Ui.S(46f));
        }

        static string KeepReward()
        {
            var t = Traverse.Create(sc);
            if (Singleton<DataManager>.Instance.userData.userGold >= t.Field("goldMaximum").GetValue<int>()) return "Credits are full";
            return "+" + t.Field("finalGold").GetValue<int>() + " credits";
        }

        // the same cases the game's board shows: no experience in events and fixed-level areas, none past the top level
        static string ReleaseReward()
        {
            if (sc.infoExp != null && sc.infoExp.color == Color.red) return "No XP here";
            if (sc.textMaxLv != null && sc.textMaxLv.activeSelf) return "Top level";
            return "+" + Traverse.Create(sc).Field("finalExp").GetValue<int>() + " XP";
        }

        // the keys for this screen along the bottom
        static void Hints(float sw, float y)
        {
            var items = new List<(string key, string what)>();
            if (Pad.InUse)
            {
                items.Add(("R-stick", "Turn the fish"));
                items.Add(("LT / RT", "Zoom"));
            }
            else
            {
                items.Add(("Mouse", "Turn the fish"));
                items.Add(("Wheel", "Zoom"));
                items.Add((Ui.Key(Keys.FreeCursor), "Free the cursor"));
            }
            items.Add((KeyName(Keys.CardToggle, Pad.Button.Y), "Game's board"));
            if (!Pad.InUse) items.Add((Ui.Key(Keys.Photo), "Photo mode"));
            var st = Ui.Style(14, false);
            float total = 0f;
            foreach (var it in items) total += Ui.ChipWidth(it.key, 24f) + Ui.S(8f) + Ui.Width(it.what, st) + Ui.S(22f);
            float x = (sw - total + Ui.S(22f)) / 2f;
            foreach (var it in items)
            {
                x += Ui.Chip(x, y, it.key, 24f) + Ui.S(8f);
                float w = Ui.Width(it.what, st);
                Ui.Label(new Rect(x, y, w + Ui.S(4f), Ui.S(24f)), it.what, st, Ui.Dim);
                x += w + Ui.S(22f);
            }
        }

        // a line from the smallest to the biggest of the kind, filled up to this fish, with round pins under it
        // for this fish and your best
        static void Scale(float x0, float x1, float y, bool record)
        {
            // spaced by weight, which grows with the cube of length
            float At(float v) => Mathf.Lerp(x0, x1, Mathf.Clamp01((Grams(v) - Grams(lo)) / (Grams(hi) - Grams(lo))));
            float lineH = Mathf.Max(2f, Ui.S(3f));
            Ui.Box(new Rect(x0, y, x1 - x0, lineH), new Color(1f, 1f, 1f, 0.25f), 0f);
            // the fill runs out to this fish as the screen arrives
            float shown = Mathf.Lerp(lo, length, Ease((sinceOpen - 0.25f) / 0.7f));
            Ui.Box(new Rect(x0, y, At(shown) - x0, lineH), Ui.Accent, 0f);
            for (int i = 0; i <= 50; i++)
            {
                float x = Mathf.Lerp(x0, x1, i / 50f);
                float th = i % 5 == 0 ? Ui.S(7f) : Ui.S(3.5f);
                Ui.Box(new Rect(x - 0.5f, y - th, Mathf.Max(1f, Ui.S(1f)), th), new Color(1f, 1f, 1f, i % 5 == 0 ? 0.5f : 0.28f), 0f);
            }
            // the ends, labeled
            Ui.Label(new Rect(x0, y - Ui.S(46f), Ui.S(200f), Ui.S(18f)), "SMALLEST", Ui.Style(12, true), Ui.Text);
            Ui.Label(new Rect(x0, y - Ui.S(29f), Ui.S(200f), Ui.S(20f)), Weight(lo), Ui.Style(16, true), Ui.Text);
            Ui.Label(new Rect(x1 - Ui.S(200f), y - Ui.S(46f), Ui.S(200f), Ui.S(18f)), "BIGGEST", Ui.Style(12, true, TextAnchor.MiddleRight), Ui.Text);
            Ui.Label(new Rect(x1 - Ui.S(200f), y - Ui.S(29f), Ui.S(200f), Ui.S(20f)), Weight(hi), Ui.Style(16, true, TextAnchor.MiddleRight), Ui.Text);

            // your best (the one this catch beat, when it did) hangs on a short pin and this fish on a long one, so the
            // two never cover each other; their labels share a row and step apart when they would touch
            float mx = At(shown), ly = y + Ui.S(64f), lh = Ui.S(18f);
            var fishSt = Ui.Style(13, true, TextAnchor.MiddleCenter);
            string fishText = "This fish " + WeightOfGrams(fishGrams) + (length <= lo + 0.01f ? ", the smallest" : length >= hi - 0.01f ? ", the biggest" : "");
            float fw = Ui.Width(fishText, fishSt) + Ui.S(4f);
            float fx = Mathf.Clamp(mx, x0 + fw / 2f, x1 - fw / 2f);
            if (best > 0f)
            {
                float bx = At(best);
                var bestSt = Ui.Style(13, true, TextAnchor.MiddleCenter);
                string bestText = (record ? "Old best " : "Your best ") + Weight(best);
                float bw = Ui.Width(bestText, bestSt) + Ui.S(4f);
                float lx = Mathf.Clamp(bx, x0 + bw / 2f, x1 - bw / 2f);
                float need = (fw + bw) / 2f + Ui.S(16f);
                if (Mathf.Abs(fx - lx) < need)
                {
                    // the bigger one's label goes right, then both slide back inside the scale if that pushed one out
                    float mid = (fx + lx) / 2f, side = length >= best ? 1f : -1f;
                    fx = mid + side * need / 2f;
                    lx = mid - side * need / 2f;
                    float left = Mathf.Min(fx - fw / 2f, lx - bw / 2f), right = Mathf.Max(fx + fw / 2f, lx + bw / 2f);
                    float shift = left < x0 ? x0 - left : right > x1 ? x1 - right : 0f;
                    fx += shift; lx += shift;
                }
                Ui.Label(new Rect(lx - bw / 2f, ly, bw, lh), bestText, bestSt, BestText);
            }
            Pin(mx, y, Ui.S(34f), Ui.S(24f), Color.white, "fish", true);
            Ui.Label(new Rect(fx - fw / 2f, ly, fw, lh), fishText, fishSt, Ui.Text);
            // the star goes on top, so it still shows when your best is the same size as this fish
            if (best > 0f) Pin(At(best), y, Ui.S(12f), Ui.S(18f), Orange, "star", false);
        }

        // a round pin hanging from the scale
        static void Pin(float x, float lineY, float stem, float size, Color c, string icon, bool ring)
        {
            Ui.Box(new Rect(x - Mathf.Max(1f, Ui.S(1f)), lineY, Mathf.Max(2f, Ui.S(2f)), stem), c, 0f);
            var r = new Rect(x - size / 2f, lineY + stem, size, size);
            if (ring)
            {
                Ui.Box(r, new Color(0.06f, 0.07f, 0.08f, 0.95f), size / 2f);
                Ui.Box(r, c, size / 2f, Mathf.Max(2f, Ui.S(3f)));
                Icon(new Rect(r.x + size * 0.25f, r.y + size * 0.25f, size * 0.5f, size * 0.5f), icon, c);
            }
            else
            {
                Ui.Box(r, c, size / 2f);
                Icon(new Rect(r.x + size * 0.22f, r.y + size * 0.22f, size * 0.56f, size * 0.56f), icon, new Color(0.06f, 0.07f, 0.08f));
            }
        }

        static void DrawChips(float cx, float y, List<(string icon, string text, Color fill)> chips)
        {
            var st = Ui.Style(14, true);
            float h = Ui.S(26f), gap = Ui.S(8f), total = -gap;
            var widths = new List<float>();
            foreach (var c in chips)
            {
                float w = Ui.Width(c.text, st) + Ui.S(38f);
                widths.Add(w); total += w + gap;
            }
            float x = cx - total / 2f;
            for (int i = 0; i < chips.Count; i++)
            {
                var r = new Rect(x, y, widths[i], h);
                Ui.Box(r, chips[i].fill, Ui.S(3f));
                Icon(new Rect(r.x + Ui.S(9f), r.y + Ui.S(6f), Ui.S(14f), Ui.S(14f)), chips[i].icon, Ui.Text);
                Ui.Label(new Rect(r.x + Ui.S(28f), r.y, r.width, h), chips[i].text, st, Ui.Text);
                x += widths[i] + gap;
            }
        }

        // the season's running total, freshwater or saltwater as the game keeps them; the game adds this catch a
        // moment after the screen opens, once its server answers
        internal static int SeasonTotal()
        {
            var u = Singleton<DataManager>.Instance.userData;
            return (StageInfoManager.CurStageItem?.stageType ?? 0) < 2 ? u.totalScoreFresh : u.totalScoreSalt;
        }

        // one achievement or level-up; returns its height
        static float DrawAward(float x, float y, string kind, string title, string detail)
        {
            var ks = Ui.Style(12, true);
            var ts = Ui.Style(19, true);
            var ds = Ui.Style(14, false);
            float w = Mathf.Min(Screen.width * 0.3f, Mathf.Max(Ui.S(240f), Ui.Width(title, ts), detail != null ? Ui.Width(detail, ds) : 0f) + Ui.S(34f));
            var r = new Rect(x, y, w, Ui.S(detail != null ? 76f : 56f));
            Ui.Box(r, Tile, Ui.S(6f));
            Ui.Box(new Rect(r.x, r.y, Ui.S(4f), r.height), Yellow, Ui.S(2f));
            float tx = r.x + Ui.S(18f), iw = r.width - Ui.S(26f);
            Ui.Label(new Rect(tx, r.y + Ui.S(8f), iw, Ui.S(16f)), kind, ks, Yellow);
            Ui.Label(new Rect(tx, r.y + Ui.S(24f), iw, Ui.S(26f)), title, ts, Ui.Text);
            if (detail != null) Ui.Label(new Rect(tx, r.y + Ui.S(50f), iw, Ui.S(18f)), detail, ds, Ui.Dim);
            return r.height;
        }

        static void DrawTile(Rect r, string glyph, Color c, string value, string caption, string gain = null)
        {
            Ui.Box(r, Tile, Ui.S(3f));
            float d = Ui.S(32f);
            var badge = new Rect(r.center.x - d / 2f, r.y - d * 0.4f, d, d);
            Ui.Box(badge, new Color(0.10f, 0.11f, 0.12f, 1f), d / 2f);
            Ui.Box(badge, c, d / 2f, Mathf.Max(1.5f, Ui.S(2f)));
            Ui.Label(badge, glyph, Ui.Style(glyph.Length > 1 ? 11 : 14, true, TextAnchor.MiddleCenter), c);
            Ui.Label(new Rect(r.x + Ui.S(4f), r.y + Ui.S(24f), r.width - Ui.S(8f), Ui.S(30f)), value, Fit(value, r.width - Ui.S(8f), 20), Ui.Text);
            Ui.Label(new Rect(r.x + Ui.S(6f), r.y + Ui.S(54f), r.width - Ui.S(12f), Ui.S(30f)), caption, Ui.Style(10, true, TextAnchor.UpperCenter, true), c);
            if (gain != null) Ui.Label(new Rect(r.x, r.y + Ui.S(4f), r.width - Ui.S(7f), Ui.S(16f)), gain, Ui.Style(11, true, TextAnchor.MiddleRight), c);
        }

        // white outline; the key on the left, the action and what it earns beside it
        static bool ChoiceButton(Rect r, string text, string reward, Color rewardColor, string key, bool picked)
        {
            bool hover = Ui.Hover(r) && !chosen;
            // the one picked lights up in its reward's color as the screen leaves
            Ui.Box(r, picked ? new Color(rewardColor.r * 0.45f, rewardColor.g * 0.45f, rewardColor.b * 0.45f, 0.85f) : new Color(0f, 0f, 0f, hover ? 0.6f : 0.45f), Ui.S(2f));
            Ui.Box(r, picked ? rewardColor : new Color(1f, 1f, 1f, hover ? 1f : 0.85f), Ui.S(2f), Mathf.Max(1f, Ui.S(picked ? 2.5f : 1.5f)));
            float x = r.x + Ui.S(14f);
            x += Ui.Chip(x, r.center.y - Ui.S(13f), key, 26f) + Ui.S(14f);
            Ui.Label(new Rect(x, r.y + Ui.S(8f), r.xMax - x, Ui.S(24f)), text, Ui.Style(18, false), Ui.Text);
            Ui.Label(new Rect(x, r.y + Ui.S(32f), r.xMax - x, Ui.S(22f)), reward, Ui.Style(15, true), rewardColor);
            return Ui.Click(r);
        }

        // the key's name in words, or the controller button's when the controller was used last
        static string KeyName(Keys.Bind b, Pad.Button padDefault)
        {
            if (Pad.InUse) return Pad.Display(b.PadButton != Pad.Button.None ? b.PadButton : padDefault);
            return Ui.Key(b);
        }

        // the biggest bold size, from max down to 11, at which the text fits the width
        static GUIStyle Fit(string text, float width, int max)
        {
            for (int size = max; size > 11; size--)
                if (Ui.Width(text, Ui.Style(size, true, TextAnchor.MiddleCenter)) <= width) return Ui.Style(size, true, TextAnchor.MiddleCenter);
            return Ui.Style(11, true, TextAnchor.MiddleCenter);
        }

        static string Text(UnityEngine.UI.Text t) => t != null && t.gameObject.activeInHierarchy ? t.text : "";

        // the game's weight for a length, grams from centimeters (its FishSpawnController.GetWeight)
        internal static float Grams(float cm)
        {
            float a = cm * 0.333f;
            return cm * a * (a * 0.5f) / 3f;
        }

        internal static string Weight(float cm) => WeightOfGrams(Grams(cm));

        // written as the game writes it (ScoreController.ScoreShow: GM.ChangeUnitWeight, 2.2046 lb to the kg, N3), so a
        // caught fish's weight here is the board's to the last digit
        internal static string WeightOfGrams(float g)
        {
            bool pounds = Singleton<DataManager>.Instance.userData.unit == 0;
            return pounds ? (GM.ChangeUnitWeight(g) * 0.001f).ToString("N3") + " lb" : GM.ChangeUnitKg(g).ToString("N3") + " kg";
        }

        static Texture2D Vignette()
        {
            if (vignette != null) return vignette;
            const int n = 128;
            vignette = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Mathf.Sqrt(dx * dx + dy * dy) - 0.45f) / 0.95f)) * 0.8f;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            vignette.SetPixels32(px);
            vignette.Apply();
            return vignette;
        }

        static void Icon(Rect r, string name, Color c)
        {
            if (!Ui.Repaint) return;
            c.a *= Ui.Alpha;
            GUI.DrawTexture(r, IconTex(name), ScaleMode.StretchToFill, true, 0f, c, 0f, 0f);
        }

        // small white shapes drawn once from polygons, 4x4 supersampled
        static Texture2D IconTex(string name)
        {
            if (icons.TryGetValue(name, out var tex)) return tex;
            Vector2[][] polys;
            switch (name)
            {
                case "star":
                {
                    var p = new Vector2[10];
                    for (int i = 0; i < 10; i++)
                    {
                        float a = Mathf.PI / 2f + i * Mathf.PI / 5f, rad = i % 2 == 0 ? 0.5f : 0.21f;
                        p[i] = new Vector2(0.5f + Mathf.Cos(a) * rad, 0.53f + Mathf.Sin(a) * rad);
                    }
                    polys = new[] { p };
                    break;
                }
                case "check":
                    polys = new[] { new[] { new Vector2(0.05f, 0.52f), new Vector2(0.2f, 0.66f), new Vector2(0.4f, 0.45f), new Vector2(0.82f, 0.88f),
                        new Vector2(0.96f, 0.74f), new Vector2(0.4f, 0.16f) } };
                    break;
                case "weight":
                    polys = new[] { new[] { new Vector2(0.12f, 0.05f), new Vector2(0.88f, 0.05f), new Vector2(0.74f, 0.66f), new Vector2(0.26f, 0.66f) },
                        Ellipse(0.5f, 0.8f, 0.16f, 0.16f), Ellipse(0.5f, 0.8f, 0.08f, 0.08f) };
                    break;
                case "ruler":
                    polys = new[] { new[] { new Vector2(0.02f, 0.3f), new Vector2(0.98f, 0.3f), new Vector2(0.98f, 0.7f), new Vector2(0.02f, 0.7f) } };
                    break;
                case "fish":
                    polys = new[] { Ellipse(0.42f, 0.5f, 0.36f, 0.22f), new[] { new Vector2(0.7f, 0.5f), new Vector2(0.98f, 0.78f), new Vector2(0.98f, 0.22f) } };
                    break;
                default: // coin
                    polys = new[] { Ellipse(0.5f, 0.5f, 0.48f, 0.48f) };
                    break;
            }
            const int n = 48, ss = 4;
            tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int hit = 0;
                    for (int sy = 0; sy < ss; sy++)
                        for (int sx = 0; sx < ss; sx++)
                        {
                            var p = new Vector2((x + (sx + 0.5f) / ss) / n, (y + (sy + 0.5f) / ss) / n);
                            bool inside = false;
                            // overlapping shapes cancel, which makes the weight's ring
                            foreach (var poly in polys) inside ^= Inside(poly, p);
                            if (inside) hit++;
                        }
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(255 * hit / (ss * ss)));
                }
            tex.SetPixels32(px);
            tex.Apply();
            icons[name] = tex;
            return tex;
        }

        static Vector2[] Ellipse(float cx, float cy, float rx, float ry)
        {
            var p = new Vector2[32];
            for (int i = 0; i < 32; i++) p[i] = new Vector2(cx + Mathf.Cos(i * Mathf.PI / 16f) * rx, cy + Mathf.Sin(i * Mathf.PI / 16f) * ry);
            return p;
        }

        static bool Inside(Vector2[] poly, Vector2 p)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    c = !c;
            return c;
        }
    }
}

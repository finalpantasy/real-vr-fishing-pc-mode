using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace RealVRFishingPCMode
{
    // P: photo mode. Hides every menu and the mod's text, leaves the hands and rod where they are, and lets the camera
    // fly (WASD, E or Space up, Q down, Shift faster, wheel zooms). A landed fish is held up in front of the camera
    // and turns with it, so any background works; F puts it back so you can walk around it. H hides the hands and rod.
    // Enter saves a picture to Pictures\RealVRFishing, next to the game's own phone-camera shots.
    // In or out of photo mode, holding the middle mouse button turns a landed fish instead of your head.
    static class PhotoMode
    {
        const int VK_MBUTTON = 0x04, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_SPACE = 0x20,
            VK_HOME = 0x24, VK_LEFT = 0x25, VK_RIGHT = 0x27;
        const float MaxFly = 8f, MinEyeHeight = 0.3f;

        public static bool On;
        public static Vector3 CamOffset;
        public static float Fov;
        static bool holdFish = true, hideHands, showBoard;

        // the catch board (length, weight, value): shown beside the held fish without its Keep / Release buttons
        static Transform boardPivot;
        static Vector3 boardPos, boardScale;
        static Quaternion boardRot;
        static float boardWidth;
        static GameObject boardKeep, boardRelease;
        static bool boardMoved, keepWasActive, releaseWasActive;
        static float holdDistance = 0.7f;

        static fishSetScript fish;
        static float spin, tilt;
        static Transform bait, baitParent;
        static Vector3 baitLocalPos;
        static Quaternion baitLocalRot;
        static bool baitMoved;

        static readonly List<Canvas> hiddenCanvases = new List<Canvas>();
        static readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        static readonly Dictionary<int, bool> prevKeys = new Dictionary<int, bool>();
        static float nextCanvasScan, nextFind, savedUntil;
        static int shotFrame = -10;
        static string savedName;
        static Main main;

        public static bool FishInHand => fish != null;
        // nothing of the mod is drawn on the frame a picture is taken
        public static bool Capturing => Time.frameCount <= shotFrame + 1;

        static bool Pressed(bool focused, int vk)
        {
            bool down = focused && Win.Down(vk);
            prevKeys.TryGetValue(vk, out bool was);
            prevKeys[vk] = down;
            return down && !was;
        }

        public static void Tick(Driver d, bool focused, Quaternion yawRot)
        {
            FindFish();
            if (Pressed(focused, 'P')) Toggle(d);

            float dt = Time.unscaledDeltaTime;
            if (fish != null)
            {
                float turn = (focused && Win.Down(VK_RIGHT) ? 1f : 0f) - (focused && Win.Down(VK_LEFT) ? 1f : 0f);
                spin += 120f * dt * turn;
            }
            if (!On) { RestoreBait(); return; }

            if (Pressed(focused, VK_RETURN)) Capture();
            if (Pressed(focused, 'F')) holdFish = !holdFish;
            if (Pressed(focused, 'H')) { hideHands = !hideHands; HideHands(d, hideHands, log: true); }
            if (Pressed(focused, 'B')) { showBoard = !showBoard; if (!showBoard) RestoreBoard(); }
            if (focused && Win.Down(VK_HOME)) { CamOffset = Vector3.zero; Fov = 0f; }

            float wheel = 0f;
            wheel = Win.Wheel();
            if (wheel != 0f)
            {
                if (focused && Win.Down(VK_MBUTTON) && fish != null) holdDistance = Mathf.Clamp(holdDistance - wheel * 0.05f, 0.35f, 2.5f);
                else Fov = Mathf.Clamp((Fov > 0f ? Fov : Plugin.FieldOfView.Value) - wheel * 4f, 10f, 100f);
            }

            if (focused)
            {
                float f = (Win.Down('W') ? 1f : 0f) - (Win.Down('S') ? 1f : 0f);
                float s = (Win.Down('D') ? 1f : 0f) - (Win.Down('A') ? 1f : 0f);
                float u = (Win.Down('E') || Win.Down(VK_SPACE) ? 1f : 0f) - (Win.Down('Q') ? 1f : 0f);
                var move = yawRot * new Vector3(s, 0f, f) + Vector3.up * u;
                if (move.sqrMagnitude > 0f)
                {
                    CamOffset = Vector3.ClampMagnitude(CamOffset + move.normalized * 1.5f * (Win.Down(VK_SHIFT) ? 3f : 1f) * dt, MaxFly);
                    // never below the ground you stand on
                    CamOffset.y = Mathf.Max(CamOffset.y, MinEyeHeight - Plugin.HeadHeight.Value);
                }
            }

            // the keep / release panel and other menus can open while in photo mode, and the game turns hands back on
            if (Time.unscaledTime >= nextCanvasScan)
            {
                nextCanvasScan = Time.unscaledTime + 0.5f;
                HideCanvases();
                if (hideHands) HideHands(d, true);
            }
            if (!holdFish || fish == null) RestoreBait();
        }

        static void Toggle(Driver d)
        {
            On = !On;
            if (On)
            {
                holdFish = true;
                HideCanvases();
            }
            else
            {
                foreach (var c in hiddenCanvases) if (c != null) c.enabled = true;
                hiddenCanvases.Clear();
                hideHands = false;
                HideHands(d, false);
                showBoard = false;
                RestoreBoard();
                RestoreBait();
                CamOffset = Vector3.zero;
                Fov = 0f;
            }
            Plugin.Log.LogInfo("photo mode " + (On ? "on" : "off"));
        }

        // middle mouse held: the mouse turns the fish (sideways spins it, up and down tilts it) instead of the head
        public static bool TurnFish(bool focused, float dx, float dy)
        {
            if (fish == null || !focused || !Win.Down(VK_MBUTTON)) return false;
            spin -= dx * 0.6f;
            tilt = Mathf.Clamp(tilt + dy * 0.6f, -85f, 85f);
            return true;
        }

        static void FindFish()
        {
            if (main == null && Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 1f;
                main = UnityEngine.Object.FindFirstObjectByType<Main>();
            }
            fishSetScript f = null;
            try
            {
                var fc = main != null ? main.fishingController : null;
                var cand = main != null && main.fishSpawnController != null ? main.fishSpawnController.fish : null;
                if (fc != null && fc.gameState == FISHINGSTATE.CATCHING && cand != null && cand.isCatched && cand.fishPivot != null
                    && cand.fishInfo != null && cand.fishInfo.size != FishItem.FishSize.Huge)
                    f = cand;
            }
            catch { }
            if (f != fish)
            {
                // once the game moves on from the catch it resets the lure itself, so leave it alone
                if (f == null) { baitMoved = false; bait = null; }
                else RestoreBait();
                RestoreBoard();
                fish = f;
                if (f != null)
                {
                    spin = f.fishPivot.localEulerAngles.z;
                    tilt = 0f;
                }
            }
        }

        // called right after the driver places the head, every time it does, so the game's own updates never win
        public static void Apply(Transform head)
        {
            if (fish == null || fish.fishPivot == null) return;
            if (On && holdFish && head != null && main != null && main.baitController != null)
            {
                var b = main.baitController.transform;
                if (!baitMoved)
                {
                    bait = b; baitParent = b.parent; baitLocalPos = b.localPosition; baitLocalRot = b.localRotation;
                    baitMoved = true;
                }
                // the fish hangs below the lure, so the lure sits a little above eye level
                var fwd = head.forward;
                var flat = new Vector3(fwd.x, 0f, fwd.z);
                b.position = head.position + fwd * holdDistance + Vector3.up * 0.12f;
                b.rotation = Quaternion.LookRotation(flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward, Vector3.up);
            }
            fish.fishPivot.localRotation = Quaternion.Euler(tilt, 0f, spin);
            if (On && showBoard) PlaceBoard(head);
        }

        static Canvas BoardCanvas() => main != null && main.scoreController != null ? main.scoreController.canvas : null;

        static void PlaceBoard(Transform head)
        {
            var canvas = BoardCanvas();
            var root = canvas != null ? canvas.rootCanvas : null;
            // only while the game itself is showing the board for this catch
            if (root == null || !canvas.gameObject.activeInHierarchy || !hiddenCanvases.Contains(root) || (canvas != root && !canvas.enabled)) return;
            root.enabled = true;
            canvas.enabled = true;
            var sc = main.scoreController;
            if (!boardMoved)
            {
                boardPivot = sc.panelPivot != null ? sc.panelPivot.transform : canvas.transform;
                boardPos = boardPivot.position; boardRot = boardPivot.rotation; boardScale = boardPivot.localScale;
                var corners = new Vector3[4];
                ((RectTransform)canvas.transform).GetWorldCorners(corners);
                boardWidth = Mathf.Max(0.05f, Vector3.Distance(corners[0], corners[3]));
                boardKeep = sc.buttonKeep; boardRelease = sc.buttonRelease;
                keepWasActive = boardKeep != null && boardKeep.activeSelf;
                releaseWasActive = boardRelease != null && boardRelease.activeSelf;
                boardMoved = true;
            }
            if (boardKeep != null) boardKeep.SetActive(false);
            if (boardRelease != null) boardRelease.SetActive(false);
            if (!holdFish || head == null) { boardPivot.position = boardPos; boardPivot.rotation = boardRot; boardPivot.localScale = boardScale; return; }

            // same distance as the fish, just to its right, sized to about a third of the view
            var fwd = head.forward;
            var flat = new Vector3(fwd.x, 0f, fwd.z);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, flat);
            float width = 0.55f * holdDistance;
            boardPivot.localScale = boardScale * (width / boardWidth);
            boardPivot.rotation = Quaternion.LookRotation(flat, Vector3.up);
            boardPivot.position = head.position + fwd * holdDistance + right * (0.12f * holdDistance + width * 0.5f) - Vector3.up * 0.1f * holdDistance;
        }

        static void RestoreBoard()
        {
            if (!boardMoved) return;
            boardMoved = false;
            if (boardPivot != null) { boardPivot.position = boardPos; boardPivot.rotation = boardRot; boardPivot.localScale = boardScale; }
            if (boardKeep != null) boardKeep.SetActive(keepWasActive);
            if (boardRelease != null) boardRelease.SetActive(releaseWasActive);
            // back to hidden like every other menu while photo mode stays on
            var canvas = BoardCanvas();
            if (On && canvas != null && canvas.rootCanvas != null) canvas.rootCanvas.enabled = false;
            boardPivot = null;
        }

        static void RestoreBait()
        {
            if (!baitMoved) return;
            baitMoved = false;
            if (bait != null && bait.parent == baitParent)
            {
                bait.localPosition = baitLocalPos;
                bait.localRotation = baitLocalRot;
            }
            bait = null;
        }

        static void HideCanvases()
        {
            foreach (var c in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (c == null || !c.enabled || !c.isRootCanvas) continue;
                var board = BoardCanvas();
                if (showBoard && board != null && board.rootCanvas == c)
                {
                    if (!hiddenCanvases.Contains(c)) hiddenCanvases.Add(c);
                    continue;
                }
                c.enabled = false;
                hiddenCanvases.Add(c);
            }
        }

        static void HideHands(Driver d, bool hide, bool log = false)
        {
            if (!hide)
            {
                foreach (var r in hiddenRenderers) if (r != null) r.enabled = true;
                hiddenRenderers.Clear();
                return;
            }
            var keep = new List<Transform>();
            if (fish != null) keep.Add(fish.transform);
            if (main != null && main.baitController != null) keep.Add(main.baitController.transform);
            // the gloves live on the game's own hand objects, not under the tracking anchors
            var roots = new List<Transform> { d.LeftHand, d.RightHand };
            if (main != null)
            {
                if (main.tackleController != null) roots.Add(main.tackleController.transform);
                if (main.ropeController != null) roots.Add(main.ropeController.transform);
                if (main.handController != null)
                    foreach (var h in new[] { main.handController.handComponentLeft, main.handController.handComponentRight })
                        if (h != null) roots.Add(h.transform);
                if (main.vrFishingPlayerController != null)
                    foreach (var h in new[] { main.vrFishingPlayerController.LeftHandAnchor, main.vrFishingPlayerController.RightHandAnchor })
                        if (h != null) roots.Add(h.transform);
            }
            var found = new HashSet<Renderer>();
            foreach (var root in roots)
                if (root != null) found.UnionWith(root.GetComponentsInChildren<Renderer>());
            // and anything else named like a glove or hand, or sitting where a hand is, wherever the game parents it
            var handPoints = new List<Vector3>();
            foreach (var root in roots)
                if (root != null && (main == null || main.ropeController == null || root != main.ropeController.transform))
                    handPoints.Add(root.position);
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (LooksLikeHand(r.transform)) { found.Add(r); continue; }
                var b = r.bounds;
                if (b.size.magnitude < 0.6f && handPoints.Exists(p => (b.center - p).sqrMagnitude < 0.3f * 0.3f)) found.Add(r);
            }
            // the short line the fish hangs from
            var fishLines = new HashSet<Renderer>();
            if (fish != null)
            {
                fishLines.UnionWith(fish.GetComponentsInChildren<LineRenderer>());
                var line = Traverse.Create(fish).Field("line").GetValue<LineRenderer>();
                if (line != null) fishLines.Add(line);
            }
            found.UnionWith(fishLines);
            // the float goes too, even where it rides on the lure; only the fish stays
            var floats = new HashSet<Renderer>();
            if (main != null)
                foreach (var root in new Component[] { main.tackleController != null ? main.tackleController.CurrentBobber : null, main.bobberController })
                    if (root != null)
                        // the bobber controller can sit on the lure itself, so never count a fish under it as float
                        floats.UnionWith(root.GetComponentsInChildren<Renderer>().Where(r => r.GetComponentInParent<fishSetScript>() == null));
            found.UnionWith(floats);
            var names = log ? new List<string>() : null;
            foreach (var r in found)
            {
                if (r == null || !r.enabled) continue;
                if (!fishLines.Contains(r))
                {
                    if (fish != null && r.transform.IsChildOf(fish.transform)) continue;
                    if (!floats.Contains(r) && keep.Exists(k => k != null && r.transform.IsChildOf(k))) continue;
                }
                r.enabled = false;
                hiddenRenderers.Add(r);
                names?.Add((r.transform.parent != null ? r.transform.parent.name + "/" : "") + r.name);
            }
            if (log) Plugin.Log.LogInfo($"photo: hid {names.Count}: {string.Join(", ", names.ToArray())}");
        }

        static bool LooksLikeHand(Transform t)
        {
            for (int i = 0; i < 3 && t != null; i++, t = t.parent)
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("glove") || n.StartsWith("hand") || n.Contains("_hand") || n.Contains("hand_")) return true;
            }
            return false;
        }

        static void Capture()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RealVRFishing");
                Directory.CreateDirectory(dir);
                savedName = "PCMode_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png";
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, savedName));
                shotFrame = Time.frameCount;
                savedUntil = Time.unscaledTime + 2.5f;
                Plugin.Log.LogInfo("photo saved: " + savedName);
            }
            catch (Exception e) { Plugin.Log.LogError("photo failed: " + e.Message); }
        }

        public static void Draw(float scale, GUIStyle style, bool showHelp)
        {
            if (Capturing) return;
            if (Time.unscaledTime < savedUntil)
                GUI.Label(new Rect(20 * scale, 20 * scale, 1200 * scale, 30 * scale), "Saved to Pictures\\RealVRFishing\\" + savedName, style);
            if (!showHelp) return;
            GUI.Label(new Rect(20 * scale, Screen.height - 60 * scale, Screen.width - 40 * scale, 50 * scale),
                "<b>Photo mode</b>  Enter: save   WASD: move   E / Q: up / down   Wheel: zoom   Home: reset   H: hide hands, rod, line and float   B: catch board" +
                (fish != null ? "   Hold middle mouse: turn the fish (with wheel: nearer / farther)   F: " + (holdFish ? "put the fish back" : "hold the fish up") : "") +
                "   P: exit   F1: hide this", style);
        }
    }
}

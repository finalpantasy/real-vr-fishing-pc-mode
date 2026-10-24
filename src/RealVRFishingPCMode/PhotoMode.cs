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
        const int VK_MBUTTON = 0x04, VK_CONTROL = 0x11, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_SPACE = 0x20, VK_HOME = 0x24, VK_OEM_4 = 0xDB, VK_OEM_6 = 0xDD;
        const float MaxHold = 6f;
        const float MaxFly = 8f, MinEyeHeight = 0.3f;

        public static bool On;
        public static Vector3 CamOffset;
        public static float Fov;
        static bool holdFish = true, hideHands;
        // the catch card: 0 hidden, 1 right of the fish, 2 left of it; [ and ] size it
        static int boardMode;
        static float boardSize = 1f;
        static bool showBoard => boardMode != 0;

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
        static float nextCanvasScan, nextHandScan, nextFind, savedUntil;
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

        static int sceneHandle = -1;

        public static void Tick(Driver d, bool focused, Quaternion yawRot)
        {
            // a new scene starts outside photo mode, with the old scene's hidden things forgotten
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != sceneHandle)
            {
                sceneHandle = scene;
                if (On) Toggle(d);
                main = null;
            }
            FindFish();
            if (Pressed(focused, Keys.Photo.Vk)) Toggle(d);

            float dt = Time.unscaledDeltaTime;
            if (fish != null)
            {
                float turn = (focused && Keys.StickRight.Down ? 1f : 0f) - (focused && Keys.StickLeft.Down ? 1f : 0f);
                spin += 120f * dt * turn;
            }
            if (!On) { RestoreBait(); return; }

            if (Pressed(focused, VK_RETURN)) Capture();
            if (Pressed(focused, 'F')) holdFish = !holdFish;
            if (Pressed(focused, 'H')) { hideHands = !hideHands; HideHands(d, hideHands, log: true); }
            if (Pressed(focused, 'B')) { boardMode = (boardMode + 1) % 3; if (!showBoard) RestoreBoard(); }
            if (focused && Win.Down(VK_OEM_4)) boardSize = Mathf.Max(0.4f, boardSize - dt);
            if (focused && Win.Down(VK_OEM_6)) boardSize = Mathf.Min(2.5f, boardSize + dt);
            if (focused && Win.Down(VK_HOME)) { CamOffset = Vector3.zero; Fov = 0f; }

            float wheel = focused ? Win.Wheel() : 0f;
            if (wheel != 0f)
            {
                if (focused && (Win.Down(VK_CONTROL) || Win.Down(VK_MBUTTON)) && fish != null) holdDistance = Mathf.Clamp(holdDistance - wheel * 0.05f * Mathf.Max(1f, holdDistance), 0.35f, MaxHold);
                else Fov = Mathf.Clamp((Fov > 0f ? Fov : Plugin.FieldOfView.Value) - wheel * 4f, 10f, 120f);
            }

            if (focused)
            {
                float f = (Keys.Forward.Down ? 1f : 0f) - (Keys.Back.Down ? 1f : 0f);
                float s = (Keys.Right.Down ? 1f : 0f) - (Keys.Left.Down ? 1f : 0f);
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
                if (hideHands)
                {
                    // the game turns hands back on now and then: re-hide what was found, and look for new ones less often
                    if (Time.unscaledTime >= nextHandScan) HideHands(d, true);
                    else foreach (var r in hiddenRenderers) if (r != null && r.enabled) r.enabled = false;
                }
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
                boardMode = 0;
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
                    FitFish(f);
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

        // back far enough that the whole fish fits in about 60% of the view's height, so a big catch is all in frame
        static void FitFish(fishSetScript f)
        {
            var rs = f.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            float size = Mathf.Max(b.size.x, b.size.y, b.size.z);
            float fov = Fov > 0f ? Fov : Plugin.FieldOfView.Value;
            float fit = size / (0.6f * 2f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
            holdDistance = Mathf.Clamp(fit, 0.7f, MaxHold);
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

            // same distance as the fish, beside it, flat to the screen however you look
            float width = 0.55f * holdDistance * boardSize;
            float side = boardMode == 2 ? -1f : 1f;
            boardPivot.localScale = boardScale * (width / boardWidth);
            boardPivot.rotation = Quaternion.LookRotation(head.forward, head.up);
            boardPivot.position = head.position + head.forward * holdDistance + head.right * side * (0.12f * holdDistance + width * 0.5f) - head.up * 0.1f * holdDistance;
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

        // the game switches gloves back on in its own updates, so hidden ones are switched off again just before each frame draws
        static bool cullHooked;
        static void BeforeCull(Camera c)
        {
            if (!hideHands || !On) return;
            foreach (var r in hiddenRenderers) if (r != null && r.enabled) r.enabled = false;
        }

        static void HideHands(Driver d, bool hide, bool log = false)
        {
            if (!cullHooked) { Camera.onPreCull += BeforeCull; cullHooked = true; }
            if (!hide)
            {
                foreach (var r in hiddenRenderers) if (r != null) r.enabled = true;
                hiddenRenderers.Clear();
                return;
            }
            nextHandScan = Time.unscaledTime + 1f;
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
                if (LooksLikeHand(r.transform) || MeshLooksLikeHand(r)) { found.Add(r); continue; }
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
            if (log)
            {
                Plugin.Log.LogInfo($"photo: hid {names.Count}: {string.Join(", ", names.ToArray())}");
                // anything still drawn close to the camera, in case the game shows a hand we don't recognize
                var cam = Camera.main;
                if (cam != null)
                {
                    var near = new List<string>();
                    foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                        if (r.enabled && r.isVisible && !hiddenRenderers.Contains(r) && (r.bounds.center - cam.transform.position).sqrMagnitude < 2.5f * 2.5f)
                            near.Add(NamePath(r.transform));
                    Plugin.Log.LogInfo($"photo: still drawn near the camera: {string.Join(", ", near.ToArray())}");
                }
            }
        }

        static string NamePath(Transform t)
        {
            string p = t.name;
            for (int i = 0; i < 3 && t.parent != null; i++) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        static bool MeshLooksLikeHand(Renderer r)
        {
            Mesh m = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            string n = m != null ? m.name.ToLowerInvariant() : "";
            if (n.Contains("glove") || (n.Contains("hand") && !n.Contains("handle"))) return true;
            var mat = r.sharedMaterial;
            return mat != null && mat.name.ToLowerInvariant().Contains("glove");
        }

        static bool LooksLikeHand(Transform t)
        {
            for (int i = 0; i < 3 && t != null; i++, t = t.parent)
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("glove") || (n.Contains("hand") && !n.Contains("handle"))) return true;
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

        public static void Draw(bool showHelp)
        {
            if (Capturing) return;
            if (Time.unscaledTime < savedUntil)
            {
                string text = "<color=#45C7DB><b>Saved</b></color>   Pictures\\RealVRFishing\\" + savedName;
                var st = Ui.Style(15, false, TextAnchor.MiddleCenter);
                float pw = Ui.Width(text, st) + Ui.S(32f);
                var pill = new Rect((Screen.width - pw) / 2f, Ui.S(24f), pw, Ui.S(38f));
                Ui.Panel(pill, Ui.Card, 19f);
                Ui.Label(pill, text, st, Ui.Text);
            }
            if (!showHelp) return;

            // one card along the bottom; the items wrap onto a second line on narrow screens
            var items = new List<KeyValuePair<string[], string>>
            {
                Item("Save picture", "Enter"),
                Item("Move", Ui.Key(Keys.Forward), Ui.Key(Keys.Left), Ui.Key(Keys.Back), Ui.Key(Keys.Right)),
                Item("Up, down", "E", "Q"),
                Item("Zoom", "Wheel"),
                Item("Hide hands", "H"),
                Item(boardMode == 0 ? "Catch card" : boardMode == 1 ? "Card left" : "Hide card", "B"),
            };
            if (fish != null)
            {
                items.Add(Item("Turn fish", "Middle mouse"));
                items.Add(Item("Fish nearer, farther", "Ctrl + wheel"));
                if (showBoard) items.Add(Item("Card size", "[", "]"));
                items.Add(Item(holdFish ? "Put fish back" : "Hold fish up", "F"));
            }
            items.Add(Item("Reset", "Home"));
            items.Add(Item("Exit", Ui.Key(Keys.Photo)));

            const float chip = 24f;
            float pad = Ui.S(16f), spacing = Ui.S(22f), lineH = Ui.S(32f), maxW = Screen.width - Ui.S(80f);
            var titleStyle = Ui.Style(13, true);
            float titleW = Ui.Width("PHOTO MODE", titleStyle) + spacing;
            var lines = new List<List<int>> { new List<int>() };
            var widths = new List<float> { titleW };
            var itemW = new float[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                itemW[i] = Ui.KeyLine(0f, 0f, items[i].Key, items[i].Value, chip, 14, true);
                int l = lines.Count - 1;
                if (lines[l].Count > 0 && widths[l] + itemW[i] > maxW - 2f * pad) { lines.Add(new List<int>()); widths.Add(0f); l++; }
                lines[l].Add(i);
                widths[l] += itemW[i] + spacing;
            }
            float w = 0f;
            foreach (var lw in widths) w = Mathf.Max(w, lw - spacing);
            w += 2f * pad;
            float h = 2f * pad + lines.Count * lineH - Ui.S(8f);
            var card = new Rect((Screen.width - w) / 2f, Screen.height - h - Ui.S(24f), w, h);
            Ui.Panel(card, Ui.Card, 14f);
            for (int l = 0; l < lines.Count; l++)
            {
                float x = card.x + pad, y = card.y + pad + l * lineH;
                if (l == 0) { Ui.Label(new Rect(x, y, titleW, Ui.S(chip)), "PHOTO MODE", titleStyle, Ui.Accent); x += titleW; }
                foreach (int i in lines[l]) { Ui.KeyLine(x, y, items[i].Key, items[i].Value, chip, 14); x += itemW[i] + spacing; }
            }
        }

        static KeyValuePair<string[], string> Item(string text, params string[] keys) => new KeyValuePair<string[], string>(keys, text);
    }
}

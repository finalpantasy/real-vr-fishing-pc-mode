using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using XRInputUtility;

namespace RealVRFishingPCMode
{
    static class Win
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        public struct POINT { public int X, Y; }
        public struct RECT { public int Left, Top, Right, Bottom; }

        // while the trailer director runs it presses every key and moves the mouse; the real ones are ignored
        public static bool Scripted;
        public static readonly HashSet<int> Virtual = new HashSet<int>();
        public static Vector2 VirtualMouse;
        public static float VirtualWheel;

        public static bool Down(int vk) => Scripted ? Virtual.Contains(vk) : (GetAsyncKeyState(vk) & 0x8000) != 0;

        public static float Wheel()
        {
            if (Scripted) return VirtualWheel;
            try { return Input.mouseScrollDelta.y; } catch { return 0f; }
        }

        static readonly uint Pid = (uint)Process.GetCurrentProcess().Id;
        public static IntPtr GameWindow()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return IntPtr.Zero;
            GetWindowThreadProcessId(fg, out uint pid);
            return pid == Pid ? fg : IntPtr.Zero;
        }
    }

    // Plays the part of the headset and both controllers: mouse turns the head, keys press buttons,
    // and the main hand points wherever the crosshair (or the free cursor) points.
    public class Driver : MonoBehaviour
    {
        public static Driver I;

        const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_TAB = 0x09, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_LALT = 0xA4,
            VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_F1 = 0x70, VK_F9 = 0x78;

        public FakeController Left, Right;
        public FakeTracking Tracking;
        public HAND_SIDE MainSide = HAND_SIDE.RIGHT;
        public Ray AimRay;
        public bool HasAim;
        public float HookPressedAt = -10f, SwingLeftAt = -10f, SwingRightAt = -10f, CastAnimAt = -10f;
        // power-bar cast: set by CastPatch while left-click is held, cleared on release
        public bool Charging, CastCancelled;
        public float CastPower, ChargeSeenAt, CastReleaseLift;

        float yaw, pitch = 10f, heightOffset, zoom;
        bool focused, wasLocked, showHelp = true, prevF1, prevF9, prevRmb, prevPgUp, prevPgDn, prevA, prevD;
        Transform head, headParent, leftHand, rightHand;
        Camera cam;
        float nextScan;
        Pose headPose, leftPose, rightPose;
        float wheelPulse;

        void Awake()
        {
            I = this;
            Left = new FakeController(HAND_SIDE.LEFT);
            Right = new FakeController(HAND_SIDE.RIGHT);
            Tracking = new FakeTracking();
            Application.onBeforeRender += ApplyPoses;
        }

        void OnDestroy() => Application.onBeforeRender -= ApplyPoses;

        public float Yaw { get => yaw; set => yaw = value; }
        public float Pitch { get => pitch; set => pitch = Mathf.Clamp(value, -85f, 85f); }
        public Transform Head => head;
        public bool ShowHelp { get => showHelp; set => showHelp = value; }
        public Transform LeftHand => leftHand;
        public Transform RightHand => rightHand;
        public FakeController Main => MainSide == HAND_SIDE.RIGHT ? Right : Left;
        public FakeController Off => MainSide == HAND_SIDE.RIGHT ? Left : Right;

        static readonly FieldInfo bySideField = AccessTools.Field(typeof(InputUtility), "m_ControllerStateBySide"),
            statesField = AccessTools.Field(typeof(InputUtility), "m_ControllerStates"),
            hapticsField = AccessTools.Field(typeof(InputUtility), "m_HapticDevicesBySide"),
            trackingField = AccessTools.Field(typeof(InputUtility), "m_InputTrackingState");
        bool installFailed;

        void InstallDevices()
        {
            var bySide = (Dictionary<HAND_SIDE, IControllerState>)bySideField.GetValue(null);
            if (bySide.TryGetValue(HAND_SIDE.RIGHT, out var cur) && cur == Right) return;
            var list = (List<ControllerState>)statesField.GetValue(null);
            var haptics = (Dictionary<HAND_SIDE, IHapticDevice>)hapticsField.GetValue(null);
            bySide.Clear(); list.Clear(); haptics.Clear();
            bySide[HAND_SIDE.LEFT] = Left; bySide[HAND_SIDE.RIGHT] = Right;
            list.Add(Left); list.Add(Right);
            haptics[HAND_SIDE.LEFT] = new FakeHaptics(HAND_SIDE.LEFT);
            haptics[HAND_SIDE.RIGHT] = new FakeHaptics(HAND_SIDE.RIGHT);
            trackingField.SetValue(null, Tracking);
            Plugin.Log.LogInfo("fake controllers installed");
        }

        // scene objects the driver reads often, looked up at most once a second
        DataManager dataManager;
        Main stageMain;
        VRPlayerController player;
        float nextLookup;

        void LookUp()
        {
            if (Time.unscaledTime < nextLookup) return;
            nextLookup = Time.unscaledTime + 1f;
            if (dataManager == null) dataManager = UnityEngine.Object.FindFirstObjectByType<DataManager>();
            if (stageMain == null) stageMain = UnityEngine.Object.FindFirstObjectByType<Main>();
            if (player == null) player = UnityEngine.Object.FindFirstObjectByType<VRPlayerController>();
        }

        void Update()
        {
            if (!installFailed)
            {
                try { InstallDevices(); }
                catch (Exception e) { installFailed = true; Plugin.Log.LogError("install devices: " + e); }
            }
            LookUp();

            // while the rig is missing (loading), look for it often but not every frame
            if (Time.unscaledTime >= nextScan || (head == null && Time.frameCount % 15 == 0)) FindRig();

            var hwnd = Win.GameWindow();
            focused = Win.Scripted || hwnd != IntPtr.Zero;
            bool freeCursor = focused && Win.Down(VK_LALT);
            try { PhotoMode.Tick(this, focused, Quaternion.Euler(0f, yaw, 0f)); }
            catch (Exception e) { Plugin.Log.LogError("photo mode: " + e.Message); }
            ReadMouse(hwnd, freeCursor);
            ReadButtons();
            ComputePoses(freeCursor, hwnd);
            ApplyPoses();
            Diag.AfterApply();
            Tracking.Head = headPose; Tracking.Left = leftPose; Tracking.Right = rightPose;
            Tracking.Update(Time.deltaTime);
            Left.Update();
            Right.Update();
            Walk();
            KeepOneHanded();
            // a scripted wheel notch counts for one frame
            if (Win.Scripted) Win.VirtualWheel = 0f;
        }

        void LateUpdate()
        {
            Diag.BeforeLate(this);
            ApplyPoses();
        }

        void ReadMouse(IntPtr hwnd, bool freeCursor)
        {
            if (Win.Scripted)
            {
                Cursor.visible = false;
                var m = Win.VirtualMouse;
                if (!PhotoMode.TurnFish(true, m.x, m.y))
                {
                    yaw += m.x * Plugin.MouseSensitivity.Value;
                    pitch = Mathf.Clamp(pitch + m.y * Plugin.MouseSensitivity.Value, -85f, 85f);
                }
                Win.VirtualMouse = Vector2.zero;
                return;
            }
            bool locked = focused && !freeCursor;
            Cursor.visible = !locked;
            if (!locked) { wasLocked = false; return; }
            Win.GetClientRect(hwnd, out var rc);
            var c = new Win.POINT { X = (rc.Right - rc.Left) / 2, Y = (rc.Bottom - rc.Top) / 2 };
            Win.ClientToScreen(hwnd, ref c);
            Win.GetCursorPos(out var p);
            if (wasLocked && !PhotoMode.TurnFish(focused, p.X - c.X, p.Y - c.Y))
            {
                // slower turning while zoomed, so aiming stays steady
                float s = Plugin.MouseSensitivity.Value * (cam != null ? cam.fieldOfView / Plugin.FieldOfView.Value : 1f);
                yaw += (p.X - c.X) * s;
                pitch = Mathf.Clamp(pitch + (p.Y - c.Y) * s, -85f, 85f);
            }
            Win.SetCursorPos(c.X, c.Y);
            wasLocked = true;
        }

        void ReadButtons()
        {
            MainSide = MainIsRight() ? HAND_SIDE.RIGHT : HAND_SIDE.LEFT;
            var m = Main; var o = Off;
            bool on = focused;
            bool f1 = on && Win.Down(VK_F1);
            if (f1 && !prevF1) showHelp = !showHelp;
            prevF1 = f1;
            bool f9 = on && Win.Down(VK_F9);
            if (f9 && !prevF9) Diag.Toggle();
            prevF9 = f9;
            if (PhotoMode.On)
            {
                // photo mode owns the keyboard; the game sees both controllers at rest
                foreach (var h in new[] { m, o })
                {
                    h.Trigger = h.Grip = h.Primary = h.Secondary = h.Menu = h.StickClick = false;
                    h.Stick = Vector2.zero;
                }
                prevRmb = true; prevA = prevD = true;
                zoom = 0f;
                return;
            }
            bool rmb = on && Win.Down(VK_RBUTTON);
            if (rmb && !prevRmb) HookPressedAt = Time.time;
            prevRmb = rmb;
            bool pgUp = on && Win.Down(VK_PRIOR), pgDn = on && Win.Down(VK_NEXT);
            if (pgUp && !prevPgUp) heightOffset += 0.05f;
            if (pgDn && !prevPgDn) heightOffset -= 0.05f;
            prevPgUp = pgUp; prevPgDn = pgDn;
            if (on && Win.Down(VK_HOME)) { pitch = 10f; heightOffset = 0f; }
            // A / D swing the rod left / right (fish jumps); in the lodge they also walk
            bool a = on && Win.Down('A'), dk = on && Win.Down('D');
            if (a && !prevA) SwingLeftAt = Time.time;
            if (dk && !prevD) SwingRightAt = Time.time;
            prevA = a; prevD = dk;
            zoom = Mathf.MoveTowards(zoom, on && Win.Down('Z') ? 1f : 0f, Time.unscaledDeltaTime * 6f);

            m.Trigger = on && Win.Down(VK_LBUTTON);
            m.TriggerAmount = on && Win.Down(VK_CONTROL) ? 0.4f : 1f;
            m.Grip = on && Win.Down('F');
            m.Primary = on && Win.Down('E');
            m.Secondary = on && Win.Down('R');
            m.StickClick = false;
            m.Menu = false;
            // Windows sends the wheel to the window under the cursor even when another app has focus
            float wheel = on ? Win.Wheel() : 0f;
            if (wheel != 0f) wheelPulse = Mathf.Sign(wheel) * 0.15f;
            float wy = wheelPulse > 0f ? 1f : wheelPulse < 0f ? -1f : 0f;
            wheelPulse = Mathf.MoveTowards(wheelPulse, 0f, Time.unscaledDeltaTime);
            m.Stick = on ? new Vector2((Win.Down(VK_RIGHT) ? 1f : 0f) - (Win.Down(VK_LEFT) ? 1f : 0f),
                                       Mathf.Clamp((Win.Down(VK_UP) ? 1f : 0f) - (Win.Down(VK_DOWN) ? 1f : 0f) + wy, -1f, 1f)) : Vector2.zero;

            o.Trigger = on && Win.Down('C');
            o.TriggerAmount = 1f;
            o.Grip = on && Win.Down('V');
            o.Primary = on && Win.Down('Q');
            o.Secondary = on && Win.Down('G');
            o.Menu = on && Win.Down(VK_TAB);
            o.StickClick = false;
            o.Stick = Vector2.zero;
        }

        bool MainIsRight()
        {
            try
            {
                if (dataManager != null && dataManager.userData != null) return dataManager.userData.rightHand;
            }
            catch { }
            return true;
        }

        void FindRig()
        {
            nextScan = Time.unscaledTime + 1f;
            Transform h = null, l = null, r = null;
            var rig = UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>();
            if (rig != null && rig.isActiveAndEnabled && rig.centerEyeAnchor != null)
            {
                h = rig.centerEyeAnchor; l = rig.leftHandAnchor; r = rig.rightHandAnchor;
            }
            else
            {
                var c = Camera.main;
                if (c != null)
                {
                    h = c.transform;
                    if (h.parent != null)
                        foreach (Transform s in h.parent)
                        {
                            if (s == h) continue;
                            string n = s.name.ToLowerInvariant();
                            if (!(n.Contains("hand") || n.Contains("controller"))) continue;
                            if (n.Contains("left") && l == null) l = s;
                            else if (n.Contains("right") && r == null) r = s;
                        }
                }
            }
            if (h == null) return;
            bool changed = h != head;
            head = h; headParent = h.parent; leftHand = l; rightHand = r;
            cam = h.GetComponent<Camera>() ?? Camera.main;
            if (!changed) return;
            foreach (var t in new[] { h, l, r })
            {
                if (t == null) continue;
                foreach (var b in t.GetComponents<Behaviour>())
                {
                    string tn = b.GetType().Name;
                    if (tn.Contains("TrackedPoseDriver") || tn.Contains("ActionBasedController") || tn == "XRController")
                    {
                        b.enabled = false;
                        Plugin.Log.LogInfo($"disabled {tn} on {t.name}");
                    }
                }
            }
            Plugin.Log.LogInfo($"rig: head={Path(h)} left={Path(l)} right={Path(r)} camera={(cam != null ? cam.name : "none")}");
            DumpTree(h.root, 0, 5);
        }

        static string Path(Transform t)
        {
            if (t == null) return "none";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static void DumpTree(Transform t, int depth, int max)
        {
            if (depth > max) return;
            var comps = new StringBuilder();
            foreach (var c in t.GetComponents<Component>())
                if (c != null && !(c is Transform)) comps.Append(c.GetType().Name).Append(' ');
            Plugin.Log.LogDebug(new string(' ', depth * 2) + t.name + (t.gameObject.activeSelf ? "" : " (off)") + "  [" + comps + "] @" + t.localPosition);
            foreach (Transform c in t) DumpTree(c, depth + 1, max);
        }

        void ComputePoses(bool freeCursor, IntPtr hwnd)
        {
            HasAim = false;
            var yawRot = Quaternion.Euler(0f, yaw, 0f);
            var headPos = new Vector3(0f, Plugin.HeadHeight.Value + heightOffset, 0f);
            var headRot = Quaternion.Euler(pitch, yaw, 0f);
            // photo mode: the camera flies off and the hands stay where they were
            headPose = new Pose(headPos + (PhotoMode.On ? PhotoMode.CamOffset : Vector3.zero), headRot);
            if (PhotoMode.On) return;

            Vector3 aimLocal = headRot * Vector3.forward;
            if (cam != null && head != null)
            {
                if (freeCursor && hwnd != IntPtr.Zero)
                {
                    Win.GetCursorPos(out var p);
                    Win.ScreenToClient(hwnd, ref p);
                    Win.GetClientRect(hwnd, out var rc);
                    float sx = p.X * (float)cam.pixelWidth / Mathf.Max(1, rc.Right - rc.Left);
                    float sy = cam.pixelHeight - p.Y * (float)cam.pixelHeight / Mathf.Max(1, rc.Bottom - rc.Top);
                    AimRay = cam.ScreenPointToRay(new Vector3(sx, sy, 0f));
                }
                else AimRay = new Ray(head.position, head.forward);
                HasAim = true;
                if (headParent != null) aimLocal = headParent.InverseTransformDirection(AimRay.direction);
            }

            float side = MainSide == HAND_SIDE.RIGHT ? 1f : -1f;
            var mainPos = headPos + yawRot * new Vector3(Plugin.HandRight.Value * side, Plugin.HandUp.Value, Plugin.HandForward.Value);
            // the rod turns with the aim but only partly follows looking up and down, so it stays held up like a real rod
            var aimDir = aimLocal.sqrMagnitude > 0f ? aimLocal.normalized : Vector3.forward;
            var aimFlat = new Vector3(aimDir.x, 0f, aimDir.z);
            var aimYaw = Quaternion.LookRotation(aimFlat.sqrMagnitude > 1e-6f ? aimFlat : Vector3.forward, Vector3.up);
            float aimDown = -Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float lift = Plugin.RodLift.Value + RodAnimLift();
            float swing = RodAnimSwing();
            var mainRot = Quaternion.Euler(0f, swing, 0f) * aimYaw * Quaternion.Euler(aimDown * Plugin.RodFollowLook.Value - lift, 0f, 0f);
            var offPos = headPos + yawRot * new Vector3(-0.20f * side, -0.38f, 0.28f);
            var offRot = yawRot * Quaternion.Euler(20f, 0f, 0f);
            var mainPose = new Pose(mainPos, mainRot);
            var offPose = new Pose(offPos, offRot);
            rightPose = MainSide == HAND_SIDE.RIGHT ? mainPose : offPose;
            leftPose = MainSide == HAND_SIDE.RIGHT ? offPose : mainPose;
        }

        public void ApplyPoses()
        {
            if (head == null) return;
            head.localPosition = headPose.position;
            head.localRotation = headPose.rotation;
            if (leftHand != null) { leftHand.localPosition = leftPose.position; leftHand.localRotation = leftPose.rotation; }
            if (rightHand != null) { rightHand.localPosition = rightPose.position; rightHand.localRotation = rightPose.rotation; }
            PhotoMode.Apply(head);
            float fov = PhotoMode.On && PhotoMode.Fov > 0f ? PhotoMode.Fov
                : Mathf.Lerp(Plugin.FieldOfView.Value, Plugin.ZoomFieldOfView.Value, zoom * zoom * (3f - 2f * zoom));
            if (cam != null && Plugin.DisableVR.Value && Mathf.Abs(cam.fieldOfView - fov) > 0.01f)
                cam.fieldOfView = fov;
        }

        // the lodge has its own stick walking; WASD moves the player directly, including sideways
        void Walk()
        {
            if (!focused || head == null || PhotoMode.On) return;
            float f =(Win.Down('W') ? 1f : 0f) - (Win.Down('S') ? 1f : 0f);
            float s = (Win.Down('D') ? 1f : 0f) - (Win.Down('A') ? 1f : 0f);
            if (f == 0f && s == 0f) return;
            if (player == null || !player.isActiveAndEnabled) return;
            // A / D also swing the rod: once a line is out, they must not move you
            try
            {
                var fc = stageMain != null ? stageMain.fishingController : null;
                if (fc != null && fc.gameState != FISHINGSTATE.READY) return;
            }
            catch { }
            if (Charging) return;
            var fwd = head.forward; fwd.y = 0f; fwd.Normalize();
            var right = head.right; right.y = 0f; right.Normalize();
            float speed = Plugin.WalkSpeed.Value * (Win.Down(VK_SHIFT) ? 2f : 1f);
            player.transform.position += (fwd * f + right * s).normalized * speed * Time.deltaTime;
        }

        float nextOneHandCheck;
        void KeepOneHanded()
        {
            if (!Plugin.OneHanded.Value || Time.unscaledTime < nextOneHandCheck) return;
            nextOneHandCheck = Time.unscaledTime + 2f;
            try
            {
                var dm = UnityEngine.Object.FindFirstObjectByType<DataManager>();
                if (dm != null && dm.userData != null && dm.userData.bothHand) { dm.userData.bothHand = false; Plugin.Log.LogInfo("switched to one-handed reeling"); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("one-handed: " + e.Message); nextOneHandCheck = float.MaxValue; }
        }

        static float Bump(float t, float length) => t >= 0f && t < length ? Mathf.Sin(t / length * Mathf.PI) : 0f;

        // right-click: a sharp upward wrist flick, then a slow return. The game counts a lure twitch when the rod hand
        // turns faster than about 440 degrees a second; the snap is well over that (even at 30 fps), the return well under
        static float Flick(float t)
        {
            const float Up = 0.08f, Down = 0.3f, Angle = 45f;
            if (t < 0f || t >= Up + Down) return 0f;
            return t < Up ? Mathf.SmoothStep(0f, Angle, t / Up) : Mathf.SmoothStep(Angle, 0f, (t - Up) / Down);
        }

        // extra rod pitch in degrees: the cast winds back over the shoulder then whips forward, the hook flicks up
        float RodAnimLift()
        {
            float lift = Flick(Time.time - HookPressedAt);
            // the game stops asking about casting once the rod isn't ready, so drop a charge it forgot
            if (Charging && Time.time - ChargeSeenAt > 0.25f) Charging = false;
            if (Charging) return lift + CastPower * 45f;
            float t = Time.time - CastAnimAt;
            if (CastCancelled)
            {
                // cancelled: just lower the rod back to rest
                if (t >= 0f && t < 0.3f) lift += Mathf.SmoothStep(CastReleaseLift, 0f, t / 0.3f);
            }
            else if (CastReleaseLift > 0f)
            {
                // power cast: the wind-up happened while holding, so only the forward whip plays
                if (t >= 0f && t < 0.14f) lift += Mathf.SmoothStep(CastReleaseLift, -20f, t / 0.14f);
                else if (t >= 0.14f && t < 0.42f) lift += Mathf.SmoothStep(-20f, 0f, (t - 0.14f) / 0.28f);
            }
            else if (t >= 0f && t < 0.6f)
            {
                if (t < 0.18f) lift += Mathf.SmoothStep(0f, 55f, t / 0.18f);
                else if (t < 0.32f) lift += Mathf.SmoothStep(55f, -20f, (t - 0.18f) / 0.14f);
                else lift += Mathf.SmoothStep(-20f, 0f, (t - 0.32f) / 0.28f);
            }
            return lift;
        }

        float RodAnimSwing() => (Bump(Time.time - SwingRightAt, 0.35f) - Bump(Time.time - SwingLeftAt, 0.35f)) * 40f;

        // tall bar on the right edge that fills from the bottom: green, then orange, then red at the top
        void DrawPowerBar(float scale)
        {
            float w = 26 * scale, h = Screen.height * 0.45f, pad = 3 * scale;
            var bar = new Rect(Screen.width - 90 * scale - w, (Screen.height - h) / 2f, w, h);
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(bar.x - pad, bar.y - pad, bar.width + 2 * pad, bar.height + 2 * pad), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = CastPower < 0.5f ? Color.Lerp(new Color(0.3f, 0.9f, 0.3f), new Color(1f, 0.75f, 0.2f), CastPower * 2f)
                                         : Color.Lerp(new Color(1f, 0.75f, 0.2f), new Color(1f, 0.25f, 0.2f), CastPower * 2f - 1f);
            float fill = bar.height * CastPower;
            GUI.DrawTexture(new Rect(bar.x, bar.yMax - fill, bar.width, fill), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, 0.5f);
            for (int i = 1; i < 4; i++)
                GUI.DrawTexture(new Rect(bar.x, bar.yMax - bar.height * i / 4f, bar.width * 0.35f, Mathf.Max(1f, scale)), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(bar.center.x - 100 * scale, bar.yMax + 6 * scale, 200 * scale, 30 * scale),
                "<size=" + Mathf.RoundToInt(14 * scale) + ">right-click: cancel</size>", bigStyle);
        }

        GUIStyle style, bigStyle;
        float nextPlan;
        string castText;

        void OnGUI()
        {
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            if (style == null || Math.Abs(style.fontSize - 18 * scale) > 0.5f)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(18 * scale), richText = true };
                bigStyle = new GUIStyle(style) { fontSize = Mathf.RoundToInt(24 * scale), alignment = TextAnchor.MiddleCenter };
            }
            if (PhotoMode.On) { PhotoMode.Draw(scale, style, showHelp); return; }
            var c = new Vector2(Screen.width / 2f, Screen.height / 2f);
            if (focused && !Win.Down(VK_LALT))
                GUI.Label(new Rect(c.x - 40 * scale, c.y - 20 * scale, 80 * scale, 40 * scale), "<b>+</b>", bigStyle);

            if (Time.unscaledTime >= nextPlan)
            {
                nextPlan = Time.unscaledTime + 0.1f;
                castText = CastPatch.Readout(this);
            }
            if (!string.IsNullOrEmpty(castText))
                GUI.Label(new Rect(c.x - 200 * scale, c.y + 18 * scale, 400 * scale, 36 * scale), castText, bigStyle);

            if (Charging) DrawPowerBar(scale);
            if (Diag.On && Diag.Text != null)
                GUI.Label(new Rect(20 * scale, Screen.height - 170 * scale, Screen.width - 40 * scale, 160 * scale), Diag.Text, style);

            if (!showHelp) return;
            GUI.Label(new Rect(20 * scale, 20 * scale, 1400 * scale, 400 * scale),
                "<b>Real VR Fishing PC Mode</b>  (F1 hides this)\n" +
                "Mouse: look   Z: zoom   Hold Alt: free cursor   Left-click: click menus, grab the rod\n" +
                (Plugin.PowerCast ? "Cast: aim, hold left-click to wind up, let go when the bar is where you want (right-click cancels)\n"
                                  : "Cast: left-click the spot on the water\n") +
                "Hold left-click: reel (with Ctrl: slow)   Right-click: set the hook / land the fish   A / D: swing the rod when a fish jumps\n" +
                "Arrows or wheel: right stick (depth, bait)   Tab: menu   F: grab   E / R: A / B   Q / G: X / Y\n" +
                "WASD: walk in the lodge (Shift runs)   Page Up / Page Down: taller / shorter   Home: reset view\n" +
                "Landed fish: hold middle mouse and move to turn it (left / right arrows spin it)   P: photo mode", style);
        }
    }
}

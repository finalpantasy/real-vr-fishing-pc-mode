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

        public FakeController Left, Right;
        public FakeTracking Tracking;
        public HAND_SIDE MainSide = HAND_SIDE.RIGHT;
        public Ray AimRay;
        public bool HasAim;
        public float HookPressedAt = -10f, SwingLeftAt = -10f, SwingRightAt = -10f, CastAnimAt = -10f;
        // power-bar cast: set by CastPatch while left-click is held, cleared on release
        public bool Charging, CastCancelled;
        public float CastPower, ChargeSeenAt, CastReleaseLift;

        float yaw, pitch = 10f, heightOffset, zoom = 1f, zoomFrom;
        float hookStrength = 1f, hookHold, prevPull;
        bool focused, wasLocked, prevF1, prevF9, prevRmb, prevPgUp, prevPgDn, prevA, prevD, prevZoom, padHelpFired;
        float padHelpHeld;
        public const float HoldForSettings = 0.8f;
        int zoomStep;
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
            helpMode = Mathf.Max(0, Array.IndexOf(HelpModes, Plugin.HelpCard.Value));
        }

        void OnDestroy() => Application.onBeforeRender -= ApplyPoses;

        public float Yaw { get => yaw; set => yaw = value; }
        public float Pitch { get => pitch; set => pitch = Mathf.Clamp(value, -85f, 85f); }
        public Transform Head => head;
        // the controls card: full, small (the same card at 70%) or hidden; F1 (View on a controller) steps through them
        public static readonly string[] HelpModes = { "Full", "Small", "Hidden" };
        int helpMode;
        public int HelpMode
        {
            get => helpMode;
            set { helpMode = value; Plugin.HelpCard.Value = HelpModes[value]; SettingsPanel.Changed(); }
        }
        public bool ShowHelp { get => helpMode != 2; set => HelpMode = value ? 0 : 2; }
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
            CapFrameRate();

            // while the rig is missing (loading), look for it often but not every frame
            if (Time.unscaledTime >= nextScan || (head == null && Time.frameCount % 15 == 0)) FindRig();

            var hwnd = Win.GameWindow();
            focused = Win.Scripted || hwnd != IntPtr.Zero;
            Pad.Tick(focused);
            SettingsPanel.Tick(focused);
            Tips.Tick(focused, head != null);
            // the settings panel and the tips card have the mouse and keyboard while they are open
            bool modal = SettingsPanel.Open || Tips.Open;
            bool input = focused && !modal;
            bool freeCursor = input && Keys.FreeCursor.Down;
            try { CatchCard.Tick(input); } catch (Exception e) { Plugin.Log.LogWarning("catch card tick: " + e.Message); }
            try { PhotoMode.Tick(this, input, Quaternion.Euler(0f, yaw, 0f)); }
            catch (Exception e) { Plugin.Log.LogError("photo mode: " + e.Message); }
            ReadMouse(hwnd, freeCursor || modal);
            ReadButtons(input);
            ComputePoses(freeCursor, hwnd);
            ApplyPoses();
            Diag.AfterApply();
            Tracking.Head = headPose; Tracking.Left = leftPose; Tracking.Right = rightPose;
            Tracking.Update(Time.deltaTime);
            Left.Update();
            Right.Update();
            Walk(input);
            KeepOneHanded();
            // a scripted wheel notch counts for one frame
            if (Win.Scripted) Win.VirtualWheel = 0f;
        }

        void OnApplicationQuit()
        {
            SettingsPanel.Save();
            Pad.StopRumble();
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
                if (!PhotoMode.TurnFish(true, m.x, m.y) && !CatchCard.TurnFish(m.x, m.y))
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
            // slower turning while zoomed, so aiming stays steady
            float zoomScale = cam != null ? cam.fieldOfView / Plugin.FieldOfView.Value : 1f;
            if (wasLocked && !PhotoMode.TurnFish(focused, p.X - c.X, p.Y - c.Y) && !CatchCard.TurnFish(p.X - c.X, p.Y - c.Y))
            {
                float s = Plugin.MouseSensitivity.Value * zoomScale;
                yaw += (p.X - c.X) * s;
                pitch = Mathf.Clamp(pitch + (p.Y - c.Y) * s, -85f, 85f);
                if (p.X != c.X || p.Y != c.Y) Pad.InUse = false;
            }
            // the right stick turns the view, gently near the middle for fine aiming
            var rs = Pad.RightStick;
            // on the catch screen it turns the fish instead
            if (rs != Vector2.zero && !CatchCard.TurnFish(rs.x * Mathf.Abs(rs.x) * 500f * Time.unscaledDeltaTime, -rs.y * Mathf.Abs(rs.y) * 500f * Time.unscaledDeltaTime))
            {
                float turn = Pad.LookSpeed.Value * zoomScale * Time.unscaledDeltaTime;
                yaw += rs.x * Mathf.Abs(rs.x) * turn;
                pitch = Mathf.Clamp(pitch - rs.y * Mathf.Abs(rs.y) * turn * (Pad.InvertLook.Value ? -1f : 1f), -85f, 85f);
            }
            Win.SetCursorPos(c.X, c.Y);
            wasLocked = true;
        }

        void ReadButtons(bool on)
        {
            MainSide = MainIsRight() ? HAND_SIDE.RIGHT : HAND_SIDE.LEFT;
            var m = Main; var o = Off;
            bool f1 = on && Win.Down(Keys.Help.Vk);
            if (f1 && !prevF1) HelpMode = (helpMode + 1) % HelpModes.Length;
            prevF1 = f1;
            // on a controller a tap switches the list when let go, and a hold opens the settings
            bool padHelp = on && Pad.Down(Keys.Help.PadButton);
            if (padHelp)
            {
                padHelpHeld += Time.unscaledDeltaTime;
                if (padHelpHeld >= HoldForSettings && !padHelpFired) { padHelpFired = true; SettingsPanel.Toggle(); }
            }
            else
            {
                if (padHelpHeld > 0f && !padHelpFired) HelpMode = (helpMode + 1) % HelpModes.Length;
                padHelpHeld = 0f; padHelpFired = false;
            }
            bool f9 = on && Win.Down(Keys.F9);
            if (f9 && !prevF9) Diag.Toggle();
            prevF9 = f9;
            if (on)
                foreach (var b in Keys.All)
                    if (Win.Down(b.Vk)) { Pad.InUse = false; break; }
            // the controller button that closed the settings or the tips (B, A) reaches the game only once it's let go,
            // or B would open the game menu straight after
            if (SettingsPanel.Open || Tips.Open) padAfterModal = true;
            else if (padAfterModal && Pad.FirstDown(sticks: true) == Pad.Button.None) padAfterModal = false;
            if (PhotoMode.On || SettingsPanel.Open || Tips.Open || padAfterModal || CatchCard.Showing && !CatchCard.Hidden)
            {
                // photo mode, the settings panel or the catch screen owns the keyboard; the game sees both controllers at rest
                foreach (var h in new[] { m, o })
                {
                    h.Trigger = h.Grip = h.Primary = h.Secondary = h.Menu = h.StickClick = false;
                    h.Stick = Vector2.zero;
                }
                prevRmb = true; prevA = prevD = true; prevMenu = true;
                holdSide = 0f;
                zoom = 1f; zoomStep = 0;
                return;
            }
            bool rmb = on && Keys.Hook.Down;
            // on a controller trigger, how fast it's squeezed sets how sharp the flick is (a slow squeeze makes no
            // twitch), and how far it's held raises the rod while the line is out
            var hb = Keys.Hook.PadButton;
            float pull = on && !Win.Down(Keys.Hook.Vk) && (hb == Pad.Button.LT || hb == Pad.Button.RT) ? Pad.Amount(hb) : 0f;
            bool hookFull = Pad.HookTrigger.Value == "Full";
            float sharp = hookFull ? 1f : Mathf.Clamp((pull - prevPull) / Mathf.Max(Time.unscaledDeltaTime, 0.001f) / 10f, 0.35f, 1.2f);
            prevPull = pull;
            if (hookFull) pull = pull > 0.1f ? 1f : 0f;
            if (rmb && !prevRmb) { HookPressedAt = Time.time; hookStrength = pull > 0f ? sharp : 1f; }
            else if (pull > 0f && Time.time - HookPressedAt < 0.08f) hookStrength = Mathf.Max(hookStrength, sharp);
            prevRmb = rmb;
            // held, the hook button keeps the rod raised (the fish arrow pointing up); a quick click is only the flick.
            // A trigger raises it as far as it's squeezed, a mouse button all the way
            hookHold = LineOut() ? Mathf.MoveTowards(hookHold, pull > 0f ? pull : rmb ? 1f : 0f, Time.unscaledDeltaTime * 5f) : 0f;
            bool pgUp = on && Keys.Taller.Down, pgDn = on && Keys.Shorter.Down;
            if (pgUp && !prevPgUp) heightOffset += 0.05f;
            if (pgDn && !prevPgDn) heightOffset -= 0.05f;
            prevPgUp = pgUp; prevPgDn = pgDn;
            if (on && Keys.ResetView.Down) { pitch = 10f; heightOffset = 0f; }
            // A / D swing the rod left / right (fish jumps); in the lodge they also walk
            bool a = on && Keys.Left.Down, dk = on && Keys.Right.Down;
            if (a && !prevA) SwingLeftAt = Time.time;
            if (dk && !prevD) SwingRightAt = Time.time;
            prevA = a; prevD = dk;
            // with the line out, holding them keeps the rod pulled to that side: when a fish runs, the game wants the rod
            // held well off the line (12° to 28° by fish size) for as long as the run lasts
            float holdTarget = LineOut() ? (dk ? 1f : 0f) - (a ? 1f : 0f) : 0f;
            holdSide = Mathf.MoveTowards(holdSide, holdTarget, Time.unscaledDeltaTime * 5f);
            bool zk = on && Keys.Zoom.Down;
            // each step blends from wherever the view is now, so zooming back out goes straight to normal
            int step = !on ? 0 : zk && !prevZoom ? (zoomStep + 1) % 3 : zoomStep;
            if (step != zoomStep) { zoomFrom = cam != null ? cam.fieldOfView : ZoomFov(zoomStep); zoomStep = step; zoom = 0f; }
            prevZoom = zk;
            zoom = Mathf.MoveTowards(zoom, 1f, Time.unscaledDeltaTime * 6f);

            m.Trigger = on && Keys.Reel.Down;
            // a controller trigger reels as far as it is pulled (see Pad.ReelValue); the slow key wins
            bool padReel = on && !Win.Down(Keys.Reel.Vk) && Pad.Down(Keys.Reel.PadButton);
            m.TriggerAmount = on && Keys.SlowReel.Down ? 0.4f : padReel ? Pad.ReelValue(Pad.Amount(Keys.Reel.PadButton)) : 1f;
            m.Grip = on && Keys.Grab.Down;
            m.Primary = on && Keys.ButtonA.Down;
            m.Secondary = on && Keys.ButtonB.Down;
            m.StickClick = false;
            m.Menu = false;
            // Windows sends the wheel to the window under the cursor even when another app has focus
            float wheel = on ? Win.Wheel() : 0f;
            if (wheel != 0f) wheelPulse = Mathf.Sign(wheel) * 0.15f;
            float wy = wheelPulse > 0f ? 1f : wheelPulse < 0f ? -1f : 0f;
            wheelPulse = Mathf.MoveTowards(wheelPulse, 0f, Time.unscaledDeltaTime);
            m.Stick = on ? new Vector2((Keys.StickRight.Down ? 1f : 0f) - (Keys.StickLeft.Down ? 1f : 0f),
                                       Mathf.Clamp((Keys.StickUp.Down ? 1f : 0f) - (Keys.StickDown.Down ? 1f : 0f) + wy, -1f, 1f)) : Vector2.zero;

            o.Trigger = on && Keys.OffTrigger.Down;
            o.TriggerAmount = 1f;
            o.Grip = on && Keys.OffGrip.Down;
            o.Primary = on && Keys.ButtonX.Down;
            // the game opens its menu on either hand's B / Y button; the controller menu button toggles its music player
            // the LIV camera tablet opens right in front of you and covers the wrist menu, which a VR hand would reach
            // around; here the menu key puts the tablet away first, and the next press opens the menu
            bool menu = on && Keys.Menu.Down;
            if (menu && !prevMenu && CloseLivTablet()) menuSwallowed = true;
            if (!menu) menuSwallowed = false;
            prevMenu = menu;
            o.Secondary = on && Keys.ButtonY.Down || menu && !menuSwallowed;
            o.Menu = on && Keys.Music.Down;
            o.StickClick = false;
            o.Stick = Vector2.zero;
        }

        bool prevMenu = true, menuSwallowed, padAfterModal;

        static bool CloseLivTablet()
        {
            try
            {
                var liv = UnityEngine.Object.FindFirstObjectByType<LIVManager>();
                if (liv == null || !liv.IsTabletOn) return false;
                liv.ToggleTablet();
                Plugin.Log.LogInfo("menu button: put the LIV camera tablet away");
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("LIV tablet: " + e.Message); return false; }
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

        // flat play runs uncapped (the game turns V-Sync off for VR), several frames per physics step, and the line twitches
        static void CapFrameRate()
        {
            string c = Plugin.FrameCap.Value;
            int vsync = c == "Monitor" ? 1 : 0;
            int fps = int.TryParse(c, out var n) ? n : -1;
            if (QualitySettings.vSyncCount != vsync) QualitySettings.vSyncCount = vsync;
            if (Application.targetFrameRate != fps) Application.targetFrameRate = fps;
        }

        static float ZoomFov(int step) => step == 1 ? Plugin.Zoom1FieldOfView.Value : step == 2 ? Plugin.ZoomFieldOfView.Value : Plugin.FieldOfView.Value;

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
                : Mathf.Lerp(zoomFrom, ZoomFov(zoomStep), Mathf.SmoothStep(0f, 1f, zoom));
            if (cam != null && Plugin.DisableVR.Value && Mathf.Abs(cam.fieldOfView - fov) > 0.01f)
                cam.fieldOfView = fov;
            Tutorial.Follow();
        }

        bool padRun, prevRunClick;

        // the lodge has its own stick walking; WASD moves the player directly, including sideways
        void Walk(bool on)
        {
            if (!on || head == null || PhotoMode.On) return;
            float f = (Keys.Forward.Down ? 1f : 0f) - (Keys.Back.Down ? 1f : 0f);
            float s = (Keys.Right.Down ? 1f : 0f) - (Keys.Left.Down ? 1f : 0f);
            // the left stick walks at the speed it is pushed; a click on it runs until the stick is let go
            var ls = Pad.LeftStick;
            if (ls != Vector2.zero) { f = ls.y; s = ls.x; }
            bool runClick = Pad.Down(Keys.Run.PadButton);
            if (runClick && !prevRunClick) padRun = true;
            prevRunClick = runClick;
            if (ls == Vector2.zero && !runClick) padRun = false;
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
            float speed = Plugin.WalkSpeed.Value * (Keys.Run.Down || padRun ? 2f : 1f);
            player.transform.position += Vector3.ClampMagnitude(fwd * f + right * s, 1f) * speed * Time.deltaTime;
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

        static bool LineOut()
        {
            var fc = Singleton<global::Main>.Instance != null ? Singleton<global::Main>.Instance.fishingController : null;
            if (fc == null) return false;
            var st = fc.gameState;
            return st == FISHINGSTATE.CASTING || st == FISHINGSTATE.RETRIEVE || st == FISHINGSTATE.FIGHTING || st == FISHINGSTATE.LANDING;
        }

        // Auto: there to aim a cast or point at a menu, gone while the line is out and over the catch screen
        static bool ShowAimDot()
        {
            if (Plugin.AimDot.Value == "Off" || CatchCard.Showing && !CatchCard.Hidden) return false;
            if (Plugin.AimDot.Value == "On") return true;
            return !LineOut();
        }

        // extra rod pitch in degrees: the cast winds back over the shoulder then whips forward, the hook flicks up
        float RodAnimLift()
        {
            float lift = Flick(Time.time - HookPressedAt) * hookStrength + hookHold * 35f;
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

        // with the line out the rod leans to the side for as long as A / D are held, so a quick tap is a flick and a hold stays;
        // the lean is slow enough that the game never mistakes it for a lure twitch
        float RodAnimSwing() => LineOut() ? Mathf.SmoothStep(0f, 1f, Mathf.Abs(holdSide)) * Mathf.Sign(holdSide) * HoldSwing
            : (Bump(Time.time - SwingRightAt, 0.35f) - Bump(Time.time - SwingLeftAt, 0.35f)) * 40f;

        // well past the 28° the biggest fish need, so the rod still counts when you aren't looking straight down the line
        const float HoldSwing = 50f;
        float holdSide;

        // a rounded bar beside the crosshair that fills from the bottom: green, then gold, then red at the top
        void DrawPowerBar(Vector2 c)
        {
            float w = Ui.S(12f), h = Ui.S(220f);
            var card = new Rect(c.x + Ui.S(140f), c.y - h / 2f - Ui.S(36f), w + Ui.S(36f), h + Ui.S(52f));
            Ui.Panel(card, Ui.Card, 12f);
            var bar = new Rect(card.center.x - w / 2f, card.y + Ui.S(36f), w, h);
            Ui.Box(bar, new Color(1f, 1f, 1f, 0.10f), w / 2f);
            var col = CastPower < 0.5f ? Color.Lerp(new Color(0.35f, 0.88f, 0.45f), Ui.Gold, CastPower * 2f)
                                       : Color.Lerp(Ui.Gold, new Color(1f, 0.35f, 0.3f), CastPower * 2f - 1f);
            float fill = Mathf.Max(w, bar.height * CastPower);
            Ui.Box(new Rect(bar.x, bar.yMax - fill, w, fill), col, w / 2f);
            for (int i = 1; i < 4; i++)
                Ui.Box(new Rect(bar.x - Ui.S(7f), bar.yMax - bar.height * i / 4f, Ui.S(4f), Mathf.Max(1f, Ui.S(1.5f))), new Color(1f, 1f, 1f, 0.35f), 0f);
            Ui.Label(new Rect(card.x, card.y + Ui.S(8f), card.width, Ui.S(20f)), Mathf.RoundToInt(CastPower * 100f) + "%",
                Ui.Style(14, true, TextAnchor.MiddleCenter), col);
            var keys = new[] { HelpKey(Keys.Hook) };
            float lw = Ui.KeyLine(0f, 0f, keys, "cancel", 24f, 14, true);
            Ui.KeyLine(card.center.x - lw / 2f, card.yMax + Ui.S(8f), keys, "cancel", 24f, 14);
        }

        float nextPlan;
        string castText;
        int helpVersion = -1;
        bool helpPowerCast, helpPad, helpPs;

        struct Row
        {
            public string[] Keys;
            public string Text;
            public Row(string text, params string[] keys) { Text = text; Keys = keys; }
        }
        Row[] helpLeft, helpRight;

        void OnGUI()
        {
            Ui.Begin();
            if (SettingsPanel.Open) { SettingsPanel.Draw(); return; }
            if (Tips.Open) { Tips.Draw(); return; }
            if (PhotoMode.On) { PhotoMode.Draw(ShowHelp); return; }
            var c = new Vector2(Screen.width / 2f, Screen.height / 2f);
            if (focused && !Keys.FreeCursor.Down && ShowAimDot())
            {
                // a small dot with a dark rim, readable on bright water and dark rooms alike
                float d = Ui.S(4f), rim = Ui.S(1.5f);
                Ui.Box(new Rect(c.x - d / 2f - rim, c.y - d / 2f - rim, d + 2f * rim, d + 2f * rim), new Color(0f, 0f, 0f, 0.45f), d / 2f + rim);
                Ui.Box(new Rect(c.x - d / 2f, c.y - d / 2f, d, d), new Color(1f, 1f, 1f, 0.95f), d / 2f);
            }

            if (Time.unscaledTime >= nextPlan)
            {
                nextPlan = Time.unscaledTime + 0.1f;
                castText = CastPatch.Readout(this);
            }
            if (!string.IsNullOrEmpty(castText))
            {
                var st = Ui.Style(16, true, TextAnchor.MiddleCenter);
                float tw = Ui.Width(castText, st) + Ui.S(24f);
                var pill = new Rect(c.x - tw / 2f, c.y + Ui.S(22f), tw, Ui.S(30f));
                Ui.Panel(pill, Ui.Card, 15f);
                Ui.Label(pill, castText, st, Ui.Text);
            }

            if (Charging) DrawPowerBar(c);
            if (CatchCard.Drawing)
                try { CatchCard.Draw(); } catch (Exception e) { Plugin.Log.LogWarning("catch card draw: " + e.Message); }
            else Hud.Draw();
            if (Diag.On && Diag.Text != null)
            {
                var st = Ui.Style(14, false, TextAnchor.UpperLeft, true);
                float w = Screen.width - Ui.S(40f), h = st.CalcHeight(Ui.Content(Diag.Text), w - Ui.S(24f)) + Ui.S(20f);
                var r = new Rect(Ui.S(20f), Screen.height - h - Ui.S(20f), w, h);
                Ui.Panel(r, Ui.Card, 10f);
                Ui.Label(new Rect(r.x + Ui.S(12f), r.y + Ui.S(10f), r.width - Ui.S(24f), r.height - Ui.S(20f)), Diag.Text, st, Ui.Text);
            }

            // the catch screen has its own key hints
            if (helpMode == 2 || CatchCard.Showing && !CatchCard.Hidden) return;
            if (helpVersion != Keys.Version || helpPowerCast != Plugin.PowerCast || helpPad != Pad.InUse || helpPs != Pad.PlayStation)
            {
                helpVersion = Keys.Version; helpPowerCast = Plugin.PowerCast; helpPad = Pad.InUse; helpPs = Pad.PlayStation;
                BuildHelp();
            }
            // small is the full card drawn at 70% size
            float full = Ui.Scale;
            if (helpMode == 1) Ui.Scale *= 0.7f;
            DrawHelp();
            Ui.Scale = full;
        }

        // a bound action as the controls card shows it: the controller button once the controller was used last
        static string HelpKey(Keys.Bind b) => Pad.InUse && b.PadButton != Pad.Button.None ? Ui.PadKey(b.PadButton) : Ui.Key(b);

        // the controls card, written with whatever keys (or controller buttons) are bound
        void BuildHelp()
        {
            if (Pad.InUse) { BuildPadHelp(); return; }
            string reel = Ui.Key(Keys.Reel);
            helpLeft = new[]
            {
                Plugin.PowerCast ? new Row("Hold, let go to cast", reel) : new Row("Click the water to cast", reel),
                new Row("Hold to reel", reel),
                new Row("Hold too for slow reeling", Ui.Key(Keys.SlowReel)),
                new Row("Hook, twitch, land; hold: rod up", Ui.Key(Keys.Hook)),
                new Row("Hold rod left / right, tap to flick", Ui.Key(Keys.Left), Ui.Key(Keys.Right)),
                new Row("Zoom (press to step)", Ui.Key(Keys.Zoom)),
                new Row("Lure depth, bait (or wheel)", Ui.Key(Keys.StickUp), Ui.Key(Keys.StickDown)),
                new Row("Turn a landed fish", "Middle mouse"),
            };
            helpRight = new[]
            {
                new Row("Walk in the lodge", Ui.Key(Keys.Forward), Ui.Key(Keys.Left), Ui.Key(Keys.Back), Ui.Key(Keys.Right)),
                new Row("Run", Ui.Key(Keys.Run)),
                new Row("Hold for a free cursor", Ui.Key(Keys.FreeCursor)),
                new Row("Game menu", Ui.Key(Keys.Menu)),
                new Row("Grab", Ui.Key(Keys.Grab)),
                new Row("VR controller A, B, X, Y", Ui.Key(Keys.ButtonA), Ui.Key(Keys.ButtonB), Ui.Key(Keys.ButtonX), Ui.Key(Keys.ButtonY)),
                new Row("Taller, shorter, reset view", Ui.Key(Keys.Taller), Ui.Key(Keys.Shorter), Ui.Key(Keys.ResetView)),
                new Row("Photo mode", Ui.Key(Keys.Photo)),
            };
        }

        // the same card for the controller; actions without a button are left out
        void BuildPadHelp()
        {
            string P(Keys.Bind b) => Ui.PadKey(b.PadButton);
            bool stickSwing = Keys.Left.PadButton == Pad.Button.LStickLeft && Keys.Right.PadButton == Pad.Button.LStickRight;
            bool stickWalk = stickSwing && Keys.Forward.PadButton == Pad.Button.LStickUp && Keys.Back.PadButton == Pad.Button.LStickDown;
            string reel = P(Keys.Reel);
            helpLeft = Present(
                Plugin.PowerCast ? new Row("Hold, let go to cast", reel) : new Row("Aim and press to cast there", reel),
                new Row(Keys.Reel.PadButton == Pad.Button.RT || Keys.Reel.PadButton == Pad.Button.LT ? "Reel; pull lightly to reel slowly" : "Hold to reel", reel),
                new Row("Hold too for slow reeling", P(Keys.SlowReel)),
                new Row("Hook, twitch, land; hold: rod up", P(Keys.Hook)),
                stickSwing ? new Row("Hold rod left / right, tap to flick", "L-stick ← →") : new Row("Hold rod left / right, tap to flick", P(Keys.Left), P(Keys.Right)),
                new Row("Zoom (press to step)", P(Keys.Zoom)),
                new Row("Lure depth, bait", P(Keys.StickUp), P(Keys.StickDown)),
                new Row("Spin a landed fish", P(Keys.StickLeft), P(Keys.StickRight)));
            helpRight = Present(
                new Row("Look around", "R-stick"),
                stickWalk ? new Row("Walk in the lodge", "L-stick") : new Row("Walk in the lodge", P(Keys.Forward), P(Keys.Left), P(Keys.Back), P(Keys.Right)),
                new Row(stickWalk ? "Click to run" : "Run", P(Keys.Run)),
                new Row("Game menu", P(Keys.Menu)),
                new Row("Grab", P(Keys.Grab)),
                new Row("VR controller A, B, X, Y", P(Keys.ButtonA), P(Keys.ButtonB), P(Keys.ButtonX), P(Keys.ButtonY)),
                new Row("Reset view", P(Keys.ResetView)),
                new Row("Taller, shorter", P(Keys.Taller), P(Keys.Shorter)),
                new Row("Photo mode", P(Keys.Photo)));
        }

        static Row[] Present(params Row[] rows)
        {
            var list = new List<Row>();
            foreach (var r in rows)
                if (Array.TrueForAll(r.Keys, k => k != null)) list.Add(r);
            return list.ToArray();
        }

        void DrawHelp()
        {
            const float chip = 24f;
            float pad = Ui.S(16f), rowH = Ui.S(30f), gap = Ui.S(32f);
            float lw = 0f, rw = 0f;
            foreach (var r in helpLeft) lw = Mathf.Max(lw, Ui.KeyLine(0f, 0f, r.Keys, r.Text, chip, 14, true));
            foreach (var r in helpRight) rw = Mathf.Max(rw, Ui.KeyLine(0f, 0f, r.Keys, r.Text, chip, 14, true));
            string[] f1 = { HelpKey(Keys.Help) }, f10 = { "F10" };
            // on a controller the settings open by holding the controls-list button
            bool padSettings = Pad.InUse && Keys.Help.PadButton != Pad.Button.None;
            string settingsText = padSettings ? "hold: settings" : "settings";
            string next = helpMode == 1 ? "hide" : "smaller";
            float hide = Ui.KeyLine(0f, 0f, f1, next, chip, 13, true), settings = Ui.KeyLine(0f, 0f, padSettings ? f1 : f10, settingsText, chip, 13, true);
            var titleStyle = Ui.Style(18, true);
            const string title = "Real VR Fishing  <color=#45C7DB>PC Mode</color>";
            float w = pad * 2f + Mathf.Max(lw + gap + rw, Ui.Width(title, titleStyle) + hide + settings + Ui.S(40f));
            int rows = Mathf.Max(helpLeft.Length, helpRight.Length);
            float x = Ui.S(20f), y = Ui.S(20f);
            float h = pad + Ui.S(30f) + Ui.S(14f) + Ui.S(22f) + rows * rowH + pad - Ui.S(6f);
            Ui.Panel(new Rect(x, y, w, h), Ui.Card, 14f);

            float cy = y + pad;
            Ui.Label(new Rect(x + pad, cy, w, Ui.S(26f)), title, titleStyle, Ui.Text);
            float hx = x + w - pad - settings;
            Ui.KeyLine(hx, cy + Ui.S(1f), padSettings ? f1 : f10, settingsText, chip, 13);
            Ui.KeyLine(hx - Ui.S(16f) - hide, cy + Ui.S(1f), f1, next, chip, 13);
            cy += Ui.S(30f) + Ui.S(6f);
            Ui.Box(new Rect(x + pad, cy, w - 2f * pad, Mathf.Max(1f, Ui.Scale)), Ui.Edge, 0f);
            cy += Ui.S(8f);
            var head = Ui.Style(12, true);
            Ui.Label(new Rect(x + pad, cy, lw, Ui.S(18f)), "FISHING", head, Ui.Accent);
            Ui.Label(new Rect(x + pad + lw + gap, cy, rw, Ui.S(18f)), "GETTING AROUND", head, Ui.Accent);
            cy += Ui.S(22f);
            for (int i = 0; i < rows; i++)
            {
                if (i < helpLeft.Length) Ui.KeyLine(x + pad, cy + i * rowH, helpLeft[i].Keys, helpLeft[i].Text, chip, 14);
                if (i < helpRight.Length) Ui.KeyLine(x + pad + lw + gap, cy + i * rowH, helpRight[i].Keys, helpRight[i].Text, chip, 14);
            }
        }
    }
}

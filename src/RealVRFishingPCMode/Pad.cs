using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RealVRFishingPCMode
{
    // An Xbox or PlayStation controller through Unity's Input System. Every action in Keys can also have a
    // controller button; the sticks add analog walking and looking, and the reel trigger reels as far as it is pulled.
    static class Pad
    {
        // stick directions count as buttons too, so walking and the rod swing can be bound to them
        public enum Button { None, A, B, X, Y, LB, RB, LT, RT, View, Menu, LS, RS, DpadUp, DpadDown, DpadLeft, DpadRight,
            LStickUp, LStickDown, LStickLeft, LStickRight }

        public static ConfigEntry<string> Glyphs;
        public static ConfigEntry<float> LookSpeed;
        public static ConfigEntry<bool> InvertLook;
        public static ConfigEntry<float> RumbleStrength;
        public static ConfigEntry<string> ReelTrigger, HookTrigger;

        public static bool PlayStation => Glyphs.Value == "PlayStation";
        // true after the controller was used last; the controls card then shows controller buttons
        public static bool InUse;
        public static Vector2 LeftStick, RightStick;
        static Gamepad pad;

        const float Dead = 0.18f;

        public static void Init(ConfigFile cfg)
        {
            Glyphs = cfg.Bind("Controller", "ButtonLabels", "Xbox", new ConfigDescription("Which controller's button names to show.",
                new AcceptableValueList<string>("Xbox", "PlayStation")));
            LookSpeed = cfg.Bind("Controller", "LookSpeed", 160f, "Degrees per second the right stick turns the view when pushed all the way.");
            InvertLook = cfg.Bind("Controller", "InvertLook", false, "Push the right stick up to look down.");
            RumbleStrength = cfg.Bind("Controller", "Rumble", 1f, "How strongly the controller rumbles: a tick on each nibble, a thump on the strike, and a steady rumble while the line is red. 0 turns it off.");
            ReelTrigger = cfg.Bind("Controller", "ReelTrigger", "Squeeze", new ConfigDescription("Squeeze: the reel trigger reels faster the further you pull it, with full speed only near a full pull. Full: any pull reels at full speed.",
                new AcceptableValueList<string>("Squeeze", "Full")));
            HookTrigger = cfg.Bind("Controller", "HookTrigger", "Squeeze", new ConfigDescription("Squeeze: the hook trigger jerks the rod as sharply as you squeeze it and raises it as far as you hold it. Full: any pull is a full jerk and a full raise.",
                new AcceptableValueList<string>("Squeeze", "Full")));
        }

        public static bool Connected => pad != null;
        public static string Name => pad != null ? pad.displayName : null;

        // once per frame, before anything reads the controller
        public static void Tick(bool focused)
        {
            try { pad = Gamepad.current; } catch { pad = null; }
            bool live = pad != null && focused && !Win.Scripted;
            if (!live) LeftStick = RightStick = Vector2.zero;
            else
            {
                LeftStick = Shape(pad.leftStick.ReadValue());
                RightStick = Shape(pad.rightStick.ReadValue());
                if (LeftStick != Vector2.zero || RightStick != Vector2.zero || FirstDown() != Button.None) InUse = true;
            }
            Rumble(live && InUse);
        }

        // The game buzzes the VR controllers almost nonstop (every reel turn, every tug of the fish), which on a gamepad
        // is one long hard rumble. The pad keeps to three signals that each feel different:
        //   a nibble (the float dips): one quick light tick on the small fast motor
        //   the strike (the fish takes it, set the hook now): one short hard thump on both motors
        //   a red line while reeling: a steady rumble that holds until the line is safe again
        // and a soft buzz while a fish jumps.
        static Gamepad rumbling;
        static float lowNow = -1f, highNow = -1f, nibbleUntil, strikeUntil;

        public static void Nibble() => nibbleUntil = Time.unscaledTime + 0.08f;
        public static void Strike() => strikeUntil = Time.unscaledTime + 0.35f;

        // (heavy low-frequency motor, light high-frequency motor)
        static Vector2 Wanted()
        {
            float t = Time.unscaledTime;
            if (t < strikeUntil) return new Vector2(0.9f, 0.8f);
            var main = Singleton<Main>.Instance;
            var fc = main != null ? main.fishingController : null;
            if (fc != null && fc.gameState == FISHINGSTATE.FIGHTING)
            {
                if (fc.lineTensionStressed) return new Vector2(0.45f, 0.6f);
                if (main.baitController != null && main.baitController.GetIsFishJump()) return new Vector2(0f, 0.3f);
            }
            if (t < nibbleUntil) return new Vector2(0f, 0.4f);
            return Vector2.zero;
        }

        static void Rumble(bool on)
        {
            var m = Vector2.zero;
            if (on) try { m = Wanted() * RumbleStrength.Value; } catch { }
            float low = Mathf.Clamp01(m.x), high = Mathf.Clamp01(m.y);
            if (rumbling != null && rumbling != pad) StopRumble();
            if (pad == null) { lowNow = highNow = -1f; return; }
            if (Mathf.Abs(low - lowNow) < 0.01f && Mathf.Abs(high - highNow) < 0.01f) return;
            try { pad.SetMotorSpeeds(low, high); } catch { }
            lowNow = low; highNow = high;
            rumbling = low > 0f || high > 0f ? pad : null;
        }

        public static void StopRumble()
        {
            if (rumbling != null) try { rumbling.SetMotorSpeeds(0f, 0f); } catch { }
            rumbling = null;
            lowNow = highNow = -1f;
        }

        // a round dead zone, rescaled so the stick still reaches full speed
        static Vector2 Shape(Vector2 v)
        {
            float m = v.magnitude;
            if (m < Dead) return Vector2.zero;
            return v / m * Mathf.Clamp01((m - Dead) / (1f - Dead));
        }

        public static bool Down(Button b)
        {
            if (pad == null || b == Button.None || Win.Scripted) return false;
            switch (b)
            {
                case Button.A: return pad.buttonSouth.isPressed;
                case Button.B: return pad.buttonEast.isPressed;
                case Button.X: return pad.buttonWest.isPressed;
                case Button.Y: return pad.buttonNorth.isPressed;
                case Button.LB: return pad.leftShoulder.isPressed;
                case Button.RB: return pad.rightShoulder.isPressed;
                case Button.LT: return pad.leftTrigger.ReadValue() > 0.25f;
                case Button.RT: return pad.rightTrigger.ReadValue() > 0.25f;
                case Button.View: return pad.selectButton.isPressed;
                case Button.Menu: return pad.startButton.isPressed;
                case Button.LS: return pad.leftStickButton.isPressed;
                case Button.RS: return pad.rightStickButton.isPressed;
                case Button.DpadUp: return pad.dpad.up.isPressed;
                case Button.DpadDown: return pad.dpad.down.isPressed;
                case Button.DpadLeft: return pad.dpad.left.isPressed;
                case Button.DpadRight: return pad.dpad.right.isPressed;
                case Button.LStickUp: return LeftStick.y > 0.5f;
                case Button.LStickDown: return LeftStick.y < -0.5f;
                case Button.LStickLeft: return LeftStick.x < -0.5f;
                case Button.LStickRight: return LeftStick.x > 0.5f;
            }
            return false;
        }

        // how far a trigger is pulled, 0 to 1; 1 for any other button that is down
        public static float Amount(Button b)
        {
            if (pad == null || Win.Scripted) return 0f;
            if (b == Button.LT) return pad.leftTrigger.ReadValue();
            if (b == Button.RT) return pad.rightTrigger.ReadValue();
            return Down(b) ? 1f : 0f;
        }

        // the reel trigger's pull as the game's trigger value. With a fish on the game reels slow under 0.33, normal
        // under 0.66 and fast above (bringing a lure in, as fast as the value), so the pull is spread out: slow to
        // 40%, normal to 85%, fast past that
        public static float ReelValue(float pull)
        {
            if (ReelTrigger.Value == "Full") return 1f;
            if (pull < 0.4f) return Mathf.Lerp(0.2f, 0.33f, pull / 0.4f);
            if (pull < 0.85f) return Mathf.Lerp(0.33f, 0.66f, (pull - 0.4f) / 0.45f);
            return Mathf.Lerp(0.66f, 1f, (pull - 0.85f) / 0.12f);
        }

        // the first button held, for rebinding (stick directions only when asked)
        public static Button FirstDown(bool sticks = false)
        {
            for (var b = Button.A; b <= (sticks ? Button.LStickRight : Button.DpadRight); b++)
                if (Down(b)) return b;
            return Button.None;
        }

        public static string ConfigName(Button b) => b.ToString();

        public static Button Parse(string s, Button fallback)
        {
            s = (s ?? "").Trim();
            foreach (Button b in System.Enum.GetValues(typeof(Button)))
                if (string.Equals(b.ToString(), s, System.StringComparison.OrdinalIgnoreCase)) return b;
            Plugin.Log.LogWarning($"unknown controller button \"{s}\", using {fallback}");
            return fallback;
        }

        // the name printed on the button, in the chosen controller's style
        public static string Display(Button b)
        {
            bool ps = PlayStation;
            switch (b)
            {
                case Button.None: return "None";
                case Button.A: return ps ? "Cross" : "A";
                case Button.B: return ps ? "Circle" : "B";
                case Button.X: return ps ? "Square" : "X";
                case Button.Y: return ps ? "Triangle" : "Y";
                case Button.LB: return ps ? "L1" : "LB";
                case Button.RB: return ps ? "R1" : "RB";
                case Button.LT: return ps ? "L2" : "LT";
                case Button.RT: return ps ? "R2" : "RT";
                case Button.View: return ps ? "Create" : "Select";
                case Button.Menu: return ps ? "Options" : "Start";
                case Button.LS: return ps ? "L3" : "LS click";
                case Button.RS: return ps ? "R3" : "RS click";
                case Button.DpadUp: return "D-pad ↑";
                case Button.DpadDown: return "D-pad ↓";
                case Button.DpadLeft: return "D-pad ←";
                case Button.DpadRight: return "D-pad →";
                case Button.LStickUp: return "L-stick ↑";
                case Button.LStickDown: return "L-stick ↓";
                case Button.LStickLeft: return "L-stick ←";
                case Button.LStickRight: return "L-stick →";
            }
            return b.ToString();
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(FishingController), "BiteFish")]
    static class StrikeRumble
    {
        static void Postfix() => Pad.Strike();
    }

    // each little dip of the float while a fish picks at the bait
    [HarmonyLib.HarmonyPatch(typeof(BobberController), "SetBobberAnimation")]
    static class NibbleRumble
    {
        static void Postfix(string __0) { if (__0 == "Bite2") Pad.Nibble(); }
    }
}

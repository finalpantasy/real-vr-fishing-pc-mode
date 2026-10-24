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
        }

        public static bool Connected => pad != null;
        public static string Name => pad != null ? pad.displayName : null;

        // once per frame, before anything reads the controller
        public static void Tick(bool focused)
        {
            try { pad = Gamepad.current; } catch { pad = null; }
            if (pad == null || !focused || Win.Scripted) { LeftStick = RightStick = Vector2.zero; return; }
            LeftStick = Shape(pad.leftStick.ReadValue());
            RightStick = Shape(pad.rightStick.ReadValue());
            if (LeftStick != Vector2.zero || RightStick != Vector2.zero || FirstDown() != Button.None) InUse = true;
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
                case Button.View: return ps ? "Create" : "View";
                case Button.Menu: return ps ? "Options" : "Menu";
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
}

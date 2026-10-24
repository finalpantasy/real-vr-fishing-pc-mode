using System.Collections.Generic;
using BepInEx.Configuration;

namespace RealVRFishingPCMode
{
    // Every key the mod reads, stored by name in the config's [Keys] section and changeable in the F10 panel.
    // Each one can also have a controller button, kept in the [Controller] section.
    // F9 (diagnostics), F10 (settings) and Esc stay fixed, so a bad binding can always be undone.
    static class Keys
    {
        public class Bind
        {
            public string Label;
            public int Default, Vk;
            public ConfigEntry<string> Entry, PadEntry;
            public Pad.Button PadDefault, PadButton;
            public bool Down => Win.Down(Vk) || Pad.Down(PadButton);
            public string Name => Keys.Display(Vk);
        }

        public static readonly List<Bind> All = new List<Bind>();
        // goes up whenever a binding changes, so cached help text knows to rebuild
        public static int Version;

        public static Bind Reel, Hook, SlowReel, Zoom, FreeCursor, Left, Right, Forward, Back, Run, Grab, ButtonA, ButtonB,
            ButtonX, ButtonY, OffTrigger, OffGrip, Menu, StickUp, StickDown, StickLeft, StickRight, Taller, Shorter, ResetView,
            Help, Photo, Keep, Release, CardToggle;

        public const int Escape = 0x1B, F9 = 0x78, F10 = 0x79;

        public static void Init(ConfigFile cfg)
        {
            Reel = Add(cfg, "Reel", "Cast, reel, click menus", 0x01, Pad.Button.RT);
            Hook = Add(cfg, "Hook", "Set the hook, twitch, land the fish", 0x02, Pad.Button.LT);
            SlowReel = Add(cfg, "SlowReel", "Hold for slow reeling", 0x11, Pad.Button.None);
            Left = Add(cfg, "Left", "Walk left, swing the rod left", 'A', Pad.Button.LStickLeft);
            Right = Add(cfg, "Right", "Walk right, swing the rod right", 'D', Pad.Button.LStickRight);
            Forward = Add(cfg, "Forward", "Walk forward", 'W', Pad.Button.LStickUp);
            Back = Add(cfg, "Back", "Walk back", 'S', Pad.Button.LStickDown);
            Run = Add(cfg, "Run", "Hold to run", 0x10, Pad.Button.LS);
            Zoom = Add(cfg, "Zoom", "Zoom in, in more, out", 'Z', Pad.Button.LB);
            FreeCursor = Add(cfg, "FreeCursor", "Hold to free the mouse cursor", 0xA4, Pad.Button.None);
            Menu = Add(cfg, "Menu", "Game menu", 0x09, Pad.Button.Menu);
            Grab = Add(cfg, "Grab", "Grab (rod hand grip)", 'F', Pad.Button.RB);
            ButtonA = Add(cfg, "ButtonA", "VR controller A button", 'E', Pad.Button.A);
            ButtonB = Add(cfg, "ButtonB", "VR controller B button", 'R', Pad.Button.B);
            ButtonX = Add(cfg, "ButtonX", "VR controller X button", 'Q', Pad.Button.X);
            ButtonY = Add(cfg, "ButtonY", "VR controller Y button", 'G', Pad.Button.Y);
            OffTrigger = Add(cfg, "OffHandTrigger", "Other hand trigger", 'C', Pad.Button.None);
            OffGrip = Add(cfg, "OffHandGrip", "Other hand grip", 'V', Pad.Button.None);
            StickUp = Add(cfg, "StickUp", "Stick up (lure depth, bait)", 0x26, Pad.Button.DpadUp);
            StickDown = Add(cfg, "StickDown", "Stick down", 0x28, Pad.Button.DpadDown);
            StickLeft = Add(cfg, "StickLeft", "Stick left, spin a landed fish", 0x25, Pad.Button.DpadLeft);
            StickRight = Add(cfg, "StickRight", "Stick right, spin a landed fish", 0x27, Pad.Button.DpadRight);
            Taller = Add(cfg, "Taller", "Make yourself taller", 0x21, Pad.Button.None);
            Shorter = Add(cfg, "Shorter", "Make yourself shorter", 0x22, Pad.Button.None);
            ResetView = Add(cfg, "ResetView", "Reset the view", 0x24, Pad.Button.RS);
            Help = Add(cfg, "Help", "Show or hide the controls list", 0x70, Pad.Button.View);
            Photo = Add(cfg, "PhotoMode", "Photo mode", 'P', Pad.Button.None);
            Keep = Add(cfg, "Keep", "Keep the fish (catch card on screen)", 0x20, Pad.Button.None);
            Release = Add(cfg, "Release", "Release the fish (catch card on screen)", 0x08, Pad.Button.None);
            CardToggle = Add(cfg, "CatchCard", "Hide or show the catch card", 'H', Pad.Button.None);
        }

        static Bind Add(ConfigFile cfg, string key, string label, int vk, Pad.Button pad)
        {
            var b = new Bind { Label = label, Default = vk, PadDefault = pad };
            b.Entry = cfg.Bind("Keys", key, ConfigName(vk), label + ". A key name like Z, F1, Space, LeftMouse, PageUp or Num5.");
            b.Vk = Parse(b.Entry.Value, vk);
            b.PadEntry = cfg.Bind("Controller", key, Pad.ConfigName(pad), label + ". A, B, X, Y, LB, RB, LT, RT, View, Menu, LS, RS, "
                + "DpadUp/Down/Left/Right, LStickUp/Down/Left/Right or None (Xbox names; Cross is A, Circle B, Square X, Triangle Y, L1 LB, R2 RT).");
            b.PadButton = Pad.Parse(b.PadEntry.Value, pad);
            All.Add(b);
            return b;
        }

        public static void Set(Bind b, int vk)
        {
            b.Vk = vk;
            b.Entry.Value = ConfigName(vk);
            Version++;
        }

        public static void ResetAll()
        {
            foreach (var b in All) Set(b, b.Default);
        }

        public static void SetPad(Bind b, Pad.Button button)
        {
            b.PadButton = button;
            b.PadEntry.Value = Pad.ConfigName(button);
            Version++;
        }

        public static void ResetPad()
        {
            foreach (var b in All) SetPad(b, b.PadDefault);
        }

        // config name, shown name
        static readonly Dictionary<int, string[]> names = new Dictionary<int, string[]>
        {
            { 0x01, new[] { "LeftMouse", "Left-click" } }, { 0x02, new[] { "RightMouse", "Right-click" } },
            { 0x04, new[] { "MiddleMouse", "Middle mouse" } }, { 0x05, new[] { "Mouse4", "Mouse 4" } }, { 0x06, new[] { "Mouse5", "Mouse 5" } },
            { 0x08, new[] { "Backspace", "Backspace" } }, { 0x09, new[] { "Tab", "Tab" } }, { 0x0D, new[] { "Enter", "Enter" } },
            { 0x10, new[] { "Shift", "Shift" } }, { 0x11, new[] { "Ctrl", "Ctrl" } }, { 0x12, new[] { "Alt", "Alt" } },
            { 0x13, new[] { "Pause", "Pause" } }, { 0x14, new[] { "CapsLock", "Caps Lock" } }, { 0x20, new[] { "Space", "Space" } },
            { 0x21, new[] { "PageUp", "Page Up" } }, { 0x22, new[] { "PageDown", "Page Down" } }, { 0x23, new[] { "End", "End" } },
            { 0x24, new[] { "Home", "Home" } }, { 0x25, new[] { "Left", "Left arrow" } }, { 0x26, new[] { "Up", "Up arrow" } },
            { 0x27, new[] { "Right", "Right arrow" } }, { 0x28, new[] { "Down", "Down arrow" } }, { 0x2D, new[] { "Insert", "Insert" } },
            { 0x2E, new[] { "Delete", "Delete" } }, { 0x6A, new[] { "NumMultiply", "Num *" } }, { 0x6B, new[] { "NumPlus", "Num +" } },
            { 0x6D, new[] { "NumMinus", "Num -" } }, { 0x6E, new[] { "NumPeriod", "Num ." } }, { 0x6F, new[] { "NumDivide", "Num /" } },
            { 0xA0, new[] { "LeftShift", "Left Shift" } }, { 0xA1, new[] { "RightShift", "Right Shift" } },
            { 0xA2, new[] { "LeftCtrl", "Left Ctrl" } }, { 0xA3, new[] { "RightCtrl", "Right Ctrl" } },
            { 0xA4, new[] { "LeftAlt", "Alt" } }, { 0xA5, new[] { "RightAlt", "Right Alt" } },
            { 0xBA, new[] { "Semicolon", ";" } }, { 0xBB, new[] { "Equals", "=" } }, { 0xBC, new[] { "Comma", "," } },
            { 0xBD, new[] { "Minus", "-" } }, { 0xBE, new[] { "Period", "." } }, { 0xBF, new[] { "Slash", "/" } },
            { 0xC0, new[] { "Backquote", "`" } }, { 0xDB, new[] { "LeftBracket", "[" } }, { 0xDC, new[] { "Backslash", "\\" } },
            { 0xDD, new[] { "RightBracket", "]" } }, { 0xDE, new[] { "Quote", "'" } },
        };

        static Keys()
        {
            for (int i = 0; i <= 9; i++) { names[0x30 + i] = new[] { i.ToString(), i.ToString() }; names[0x60 + i] = new[] { "Num" + i, "Num " + i }; }
            for (char c = 'A'; c <= 'Z'; c++) names[c] = new[] { c.ToString(), c.ToString() };
            for (int i = 1; i <= 24; i++) names[0x6F + i] = new[] { "F" + i, "F" + i };
        }

        public static bool Known(int vk) => names.ContainsKey(vk);
        public static string ConfigName(int vk) => names.TryGetValue(vk, out var n) ? n[0] : "0x" + vk.ToString("X2");
        public static string Display(int vk) => names.TryGetValue(vk, out var n) ? n[1] : "key 0x" + vk.ToString("X2");

        static int Parse(string s, int fallback)
        {
            s = (s ?? "").Trim();
            foreach (var kv in names)
                if (string.Equals(kv.Value[0], s, System.StringComparison.OrdinalIgnoreCase)) return kv.Key;
            if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)
                && int.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out int vk) && vk > 0 && vk < 0xFF)
                return vk;
            Plugin.Log.LogWarning($"unknown key name \"{s}\", using {ConfigName(fallback)}");
            return fallback;
        }

        // the other actions on the same key (or controller button), for the panel's warning
        public static string Clash(Bind b, bool pad = false)
        {
            if (pad && b.PadButton == Pad.Button.None) return null;
            string s = null;
            foreach (var o in All)
                if (o != b && (pad ? o.PadButton == b.PadButton : o.Vk == b.Vk)) s = s == null ? o.Label : s + ", " + o.Label;
            return s;
        }
    }
}

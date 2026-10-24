using System;
using BepInEx.Configuration;
using UnityEngine;

namespace RealVRFishingPCMode
{
    // F10: sliders for the main settings and lists of every key and controller button, so nobody has to edit the config file.
    // Changes apply at once and are written to the config file when the panel closes.
    static class SettingsPanel
    {
        public static bool Open;
        static int tab;
        static Keys.Bind capturing, capturingPad;
        static bool waitRelease, prevF10, prevEsc, prevPadB, dirty;
        static Vector2 scroll;
        static float contentHeight;

        // the power bar fill time that "Power bar speed 100%" means
        public const float BaseFillSeconds = 1.375f;

        public static void Tick(bool focused)
        {
            bool f10 = focused && Win.Down(Keys.F10), esc = focused && Win.Down(Keys.Escape), padB = focused && Pad.Down(Pad.Button.B);
            if (capturing != null) Capture(focused, esc && !prevEsc);
            else if (capturingPad != null) CapturePad(focused, esc && !prevEsc);
            else if ((f10 && !prevF10) || (Open && esc && !prevEsc) || (Open && padB && !prevPadB)) SetOpen(!Open);
            prevF10 = f10; prevEsc = esc; prevPadB = padB;
        }

        static void SetOpen(bool open)
        {
            Open = open;
            capturing = capturingPad = null;
            if (!open) Save();
        }

        public static void Save()
        {
            if (!dirty) return;
            dirty = false;
            try { Plugin.Cfg.Save(); }
            catch (Exception e) { Plugin.Log.LogError("saving settings: " + e.Message); }
        }

        // waits until every key is up (the click that started it), then takes the next key or mouse button
        static void Capture(bool focused, bool escPressed)
        {
            if (!focused) return;
            if (escPressed) { capturing = null; return; }
            int vk = 0;
            for (int k = 1; k < 0xFF && vk == 0; k++)
            {
                // the left / right Shift, Ctrl and Alt codes come in as plain Shift, Ctrl and Alt
                if (k == Keys.Escape || k == Keys.F9 || k == Keys.F10 || (k >= 0xA0 && k <= 0xA5) || !Keys.Known(k)) continue;
                if (Win.Down(k)) vk = k;
            }
            if (waitRelease) { if (vk == 0) waitRelease = false; return; }
            if (vk == 0) return;
            Keys.Set(capturing, vk);
            capturing = null;
            dirty = true;
        }

        // waits until the controller is let go, then takes the next button or stick direction; Delete or Backspace clears it
        static void CapturePad(bool focused, bool escPressed)
        {
            if (!focused) return;
            if (escPressed) { capturingPad = null; return; }
            if (Win.Down(0x2E) || Win.Down(0x08))
            {
                Keys.SetPad(capturingPad, Pad.Button.None);
                capturingPad = null;
                dirty = true;
                return;
            }
            var b = Pad.FirstDown(sticks: true);
            if (waitRelease) { if (b == Pad.Button.None) waitRelease = false; return; }
            if (b == Pad.Button.None) return;
            Keys.SetPad(capturingPad, b);
            capturingPad = null;
            dirty = true;
        }

        static float S(float v) => Ui.S(v);

        public static void Draw()
        {
            // dim the game behind the panel
            Ui.Box(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.35f), 0f);
            float w = Mathf.Min(S(760f), Screen.width - S(40f)), h = Mathf.Min(S(880f), Screen.height - S(40f));
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            Ui.Panel(r, Ui.Solid, 16f);
            float pad = S(28f), x = r.x + pad, iw = w - 2f * pad;

            // header
            Ui.Label(new Rect(x, r.y + S(22f), iw, S(32f)), "Settings", Ui.Style(26, true), Ui.Text);
            Ui.Label(new Rect(x, r.y + S(54f), iw, S(20f)), "Real VR Fishing PC Mode " + Plugin.Version, Ui.Style(13), Ui.Dim);
            var close = new Rect(r.xMax - S(20f) - S(36f), r.y + S(20f), S(36f), S(36f));
            Ui.Box(close, new Color(1f, 1f, 1f, Ui.Hover(close) ? 0.14f : 0.06f), S(18f));
            Ui.Label(close, "×", Ui.Style(24, false, TextAnchor.MiddleCenter), Ui.Text);
            if (Ui.Click(close)) { SetOpen(false); return; }

            // tabs, underlined in the accent color
            float ty = r.y + S(88f), tx = x;
            string[] tabs = { "Settings", "Keys", "Controller" };
            for (int i = 0; i < tabs.Length; i++)
            {
                var st = Ui.Style(16, true, TextAnchor.MiddleCenter);
                float tw = Ui.Width(tabs[i], st) + S(24f);
                var tr = new Rect(tx, ty, tw, S(34f));
                Ui.Label(tr, tabs[i], st, i == tab ? Ui.Text : Ui.Hover(tr) ? Color.Lerp(Ui.Dim, Ui.Text, 0.5f) : Ui.Dim);
                if (i == tab) Ui.Box(new Rect(tr.x + S(8f), tr.yMax - S(3f), tr.width - S(16f), S(3f)), Ui.Accent, S(1.5f));
                if (Ui.Click(tr) && tab != i) { tab = i; capturing = capturingPad = null; scroll = Vector2.zero; }
                tx += tw + S(8f);
            }
            Ui.Box(new Rect(x, ty + S(34f), iw, Mathf.Max(1f, Ui.Scale)), Ui.Edge, 0f);

            // body, scrolled with the mouse wheel when it doesn't fit
            float footH = S(72f), top = ty + S(46f);
            var view = new Rect(x - S(4f), top, iw + S(8f), r.yMax - footH - top);
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0f, 0f, view.width - S(8f), Mathf.Max(contentHeight, view.height)), false, false, GUIStyle.none, GUIStyle.none);
            contentHeight = tab == 0 ? DrawSettings(S(4f), view.width - S(16f)) : tab == 1 ? DrawKeys(S(4f), view.width - S(16f))
                : DrawPad(S(4f), view.width - S(16f));
            GUI.EndScrollView();
            if (contentHeight > view.height)
            {
                // a thin scroll position marker
                float frac = view.height / contentHeight, track = view.height - S(8f);
                float pos = Mathf.Clamp01(scroll.y / (contentHeight - view.height));
                Ui.Box(new Rect(view.xMax - S(2f), view.y + S(4f) + (track - track * frac) * pos, S(4f), track * frac), new Color(1f, 1f, 1f, 0.25f), S(2f));
            }

            // footer
            float fy = r.yMax - footH;
            Ui.Box(new Rect(x, fy, iw, Mathf.Max(1f, Ui.Scale)), Ui.Edge, 0f);
            Ui.Label(new Rect(x, fy + S(18f), iw * 0.5f, S(40f)), Pad.InUse ? "Changes apply right away.  F10, Esc or " + Pad.Display(Pad.Button.B) + " closes."
                : "Changes apply right away.  F10 or Esc closes.", Ui.Style(13), Ui.Dim);
            var done = new Rect(r.xMax - pad - S(110f), fy + S(18f), S(110f), S(40f));
            var reset = new Rect(done.x - S(12f) - S(150f), done.y, S(150f), S(40f));
            if (Ui.Button(reset, tab == 0 ? "Reset settings" : tab == 1 ? "Reset keys" : "Reset controller"))
            {
                if (tab == 0) ResetSettings();
                else if (tab == 1) Keys.ResetAll();
                else
                {
                    Keys.ResetPad();
                    foreach (var e in new ConfigEntryBase[] { Pad.Glyphs, Pad.LookSpeed, Pad.InvertLook }) e.BoxedValue = e.DefaultValue;
                }
                capturing = capturingPad = null;
                dirty = true;
            }
            if (Ui.Button(done, "Done", primary: true)) SetOpen(false);
        }

        // the settings tab; returns its height
        static float DrawSettings(float x, float w)
        {
            float y = 0f;
            Section(ref y, x, "VIEW");
            Slider(ref y, x, w, Plugin.MouseSensitivity, "Mouse sensitivity", 0.02f, 0.5f, 0.005f, v => v.ToString("0.000"));
            Slider(ref y, x, w, Plugin.FieldOfView, "Field of view", 50f, 110f, 1f, v => v.ToString("0") + "°");
            Slider(ref y, x, w, Plugin.ZoomFieldOfView, "Zoom field of view", 10f, 60f, 1f, v => v.ToString("0") + "°");
            Slider(ref y, x, w, Plugin.HeadHeight, "Eye height", 1.2f, 2.2f, 0.01f, FeetInches);
            if (Driver.I != null)
            {
                bool help = OnOff(ref y, x, w, "Controls card", Driver.I.ShowHelp, Ui.Key(Keys.Help) + " also shows or hides it");
                if (help != Driver.I.ShowHelp) Driver.I.ShowHelp = help;
            }

            Section(ref y, x, "FISHING");
            int mode = Choice(ref y, x, w, "Cast with", Plugin.PowerCast ? 0 : 1, null, "Power bar", "Click a spot");
            string m = mode == 0 ? "PowerBar" : "ClickSpot";
            if (m != Plugin.CastMode.Value) { Plugin.CastMode.Value = m; dirty = true; }
            PowerSpeed(ref y, x, w);
            Slider(ref y, x, w, Plugin.RodLift, "Rod angle", 30f, 90f, 1f, v => v.ToString("0") + "° up");
            Slider(ref y, x, w, Plugin.RodFollowLook, "Rod follows your view", 0f, 1f, 0.05f, v => (v * 100f).ToString("0") + "%");
            Toggle(ref y, x, w, Plugin.AllowOnlineCasting, "Mouse casting online", "off keeps online rooms fair");

            Section(ref y, x, "MOVEMENT AND GRAPHICS");
            Slider(ref y, x, w, Plugin.WalkSpeed, "Walk speed", 0.5f, 5f, 0.1f, v => (v * 2.237f).ToString("0.0") + " mph");
            Toggle(ref y, x, w, Plugin.SharpenScenery, "Sharper far scenery", "on the next start, up to 1 GB video memory");
            return y + S(8f);
        }

        // the keys tab; returns its height
        static float DrawKeys(float x, float w)
        {
            float y = S(10f);
            var note = Ui.Style(14, false, TextAnchor.UpperLeft, true);
            const string text = "Click a key, then press the new key or mouse button. Esc cancels. F9, F10 and Esc stay fixed. "
                + "Photo mode moves with Forward, Back, Left and Right; its other keys are fixed.";
            float nh = note.CalcHeight(Ui.Content(text), w);
            Ui.Label(new Rect(x, y, w, nh), text, note, Ui.Dim);
            y += nh + S(14f);
            float rowH = S(40f), bw = S(170f), bx = x + S(300f);
            foreach (var b in Keys.All)
            {
                var row = new Rect(x - S(8f), y, w + S(16f), rowH);
                if (Ui.Hover(row)) Ui.Box(row, new Color(1f, 1f, 1f, 0.04f), S(8f));
                Ui.Label(new Rect(x, y, S(290f), rowH), b.Label, Ui.Style(15), Ui.Text);
                var br = new Rect(bx, y + S(5f), bw, rowH - S(10f));
                bool cap = capturing == b;
                Ui.Box(br, cap ? new Color(1f, 0.77f, 0.3f, 0.16f) : new Color(1f, 1f, 1f, Ui.Hover(br) ? 0.18f : 0.11f), S(6f));
                Ui.Box(br, cap ? Ui.Gold : Ui.Edge, S(6f), Mathf.Max(1f, cap ? S(1.5f) : Ui.Scale));
                Ui.Label(br, cap ? "Press a key…" : b.Name, Ui.Style(14, true, TextAnchor.MiddleCenter), cap ? Ui.Gold : Ui.Text);
                if (Ui.Click(br)) { capturing = cap ? null : b; waitRelease = true; }
                string clash = Keys.Clash(b);
                if (clash != null)
                    Ui.Label(new Rect(br.xMax + S(14f), y, w - (br.xMax - x) - S(14f), rowH), "also " + clash, Ui.Style(13), Ui.Gold);
                y += rowH;
            }
            return y + S(8f);
        }

        // the controller tab; returns its height
        static float DrawPad(float x, float w)
        {
            float y = S(10f);
            string status = Pad.Connected ? "Connected: " + Pad.Name
                : "No controller found. Plug in an Xbox or PlayStation controller by USB or Bluetooth.";
            Ui.Label(new Rect(x, y, w, S(22f)), status, Ui.Style(14, Pad.Connected), Pad.Connected ? Ui.Accent : Ui.Gold);
            y += S(16f);

            Section(ref y, x, "CONTROLLER");
            int g = Choice(ref y, x, w, "Button labels", Pad.PlayStation ? 1 : 0, null, "Xbox", "PlayStation");
            string gv = g == 1 ? "PlayStation" : "Xbox";
            if (gv != Pad.Glyphs.Value) { Pad.Glyphs.Value = gv; Keys.Version++; dirty = true; }
            Slider(ref y, x, w, Pad.LookSpeed, "Look speed", 60f, 400f, 5f, v => v.ToString("0") + "°/s");
            Toggle(ref y, x, w, Pad.InvertLook, "Invert look", "push up to look down");

            Section(ref y, x, "BUTTONS");
            var note = Ui.Style(14, false, TextAnchor.UpperLeft, true);
            const string text = "Click a button, then press the new controller button or push the left stick. Esc cancels, Delete clears it. "
                + "The right stick always looks around, the left stick walks, and the reel trigger reels as far as you pull it.";
            float nh = note.CalcHeight(Ui.Content(text), w);
            Ui.Label(new Rect(x, y, w, nh), text, note, Ui.Dim);
            y += nh + S(14f);
            float rowH = S(40f), bw = S(170f), bx = x + S(300f);
            foreach (var b in Keys.All)
            {
                var row = new Rect(x - S(8f), y, w + S(16f), rowH);
                if (Ui.Hover(row)) Ui.Box(row, new Color(1f, 1f, 1f, 0.04f), S(8f));
                Ui.Label(new Rect(x, y, S(290f), rowH), b.Label, Ui.Style(15), Ui.Text);
                var br = new Rect(bx, y + S(5f), bw, rowH - S(10f));
                bool cap = capturingPad == b;
                Ui.Box(br, cap ? new Color(1f, 0.77f, 0.3f, 0.16f) : new Color(1f, 1f, 1f, Ui.Hover(br) ? 0.18f : 0.11f), S(6f));
                Ui.Box(br, cap ? Ui.Gold : Ui.Edge, S(6f), Mathf.Max(1f, cap ? S(1.5f) : Ui.Scale));
                if (cap) Ui.Label(br, "Press a button…", Ui.Style(14, true, TextAnchor.MiddleCenter), Ui.Gold);
                else if (b.PadButton == Pad.Button.None) Ui.Label(br, "None", Ui.Style(14, false, TextAnchor.MiddleCenter), Ui.Dim);
                else
                {
                    string k = Ui.PadKey(b.PadButton);
                    float cw = Ui.ChipWidth(k, 24f);
                    Ui.Chip(br.center.x - cw / 2f, br.center.y - S(12f), k, 24f);
                }
                if (Ui.Click(br)) { capturingPad = cap ? null : b; capturing = null; waitRelease = true; }
                string clash = Keys.Clash(b, pad: true);
                if (clash != null)
                    Ui.Label(new Rect(br.xMax + S(14f), y, w - (br.xMax - x) - S(14f), rowH), "also " + clash, Ui.Style(13), Ui.Gold);
                y += rowH;
            }
            return y + S(8f);
        }

        static void Section(ref float y, float x, string name)
        {
            y += S(18f);
            Ui.Label(new Rect(x, y, S(400f), S(18f)), name, Ui.Style(12, true), Ui.Accent);
            y += S(26f);
        }

        // label on the left, control in the middle, value or note on the right
        static Rect Row(ref float y, float x, float w, string name, out Rect value)
        {
            const float rowH = 44f;
            Ui.Label(new Rect(x, y, S(250f), S(rowH)), name, Ui.Style(15), Ui.Text);
            var control = new Rect(x + S(250f), y + S(7f), S(300f), S(rowH - 14f));
            value = new Rect(control.xMax + S(20f), y, w - (control.xMax + S(20f) - x), S(rowH));
            y += S(rowH);
            return control;
        }

        static void Slider(ref float y, float x, float w, ConfigEntry<float> e, string name, float min, float max, float step, Func<float, string> format)
        {
            var c = Row(ref y, x, w, name, out var value);
            // only a drag changes the value, so a value set outside the slider's range in the file stays as it is
            GUI.changed = false;
            float v = Ui.Slider(c, e.Value, min, max);
            if (GUI.changed)
            {
                v = Mathf.Round(v / step) * step;
                if (Mathf.Abs(v - e.Value) > step * 0.25f) { e.Value = v; dirty = true; }
            }
            Ui.Label(value, format(e.Value), Ui.Style(15, true), Ui.Text);
        }

        // the bar's fill time shown as a speed, 100% being the default
        static void PowerSpeed(ref float y, float x, float w)
        {
            var c = Row(ref y, x, w, "Power bar speed", out var value);
            var e = Plugin.PowerFillSeconds;
            GUI.changed = false;
            float speed = Ui.Slider(c, BaseFillSeconds / Mathf.Max(0.05f, e.Value), 0.5f, 2f);
            if (GUI.changed)
            {
                speed = Mathf.Round(speed / 0.05f) * 0.05f;
                float secs = BaseFillSeconds / speed;
                if (Mathf.Abs(secs - e.Value) > 0.001f) { e.Value = secs; dirty = true; }
            }
            float shown = BaseFillSeconds / Mathf.Max(0.05f, e.Value);
            Ui.Label(value, (shown * 100f).ToString("0") + "%  <color=#9EB0BA>" + e.Value.ToString("0.00") + " s</color>", Ui.Style(15, true), Ui.Text);
        }

        static void Toggle(ref float y, float x, float w, ConfigEntry<bool> e, string name, string note)
        {
            bool v = OnOff(ref y, x, w, name, e.Value, note);
            if (v != e.Value) { e.Value = v; dirty = true; }
        }

        static bool OnOff(ref float y, float x, float w, string name, bool on, string note) =>
            Choice(ref y, x, w, name, on ? 1 : 0, note, "Off", "On") == 1;

        static int Choice(ref float y, float x, float w, string name, int index, string note, params string[] options)
        {
            var c = Row(ref y, x, w, name, out var value);
            if (options[0].Length <= 3) c.width = S(150f);
            index = Ui.Segmented(c, index, options);
            if (note != null)
            {
                value.x = c.xMax + S(20f);
                value.width = w - (value.x - x);
                Ui.Label(value, note, Ui.Style(13, false, TextAnchor.MiddleLeft, true), Ui.Dim);
            }
            return index;
        }

        static string FeetInches(float meters)
        {
            int inches = Mathf.RoundToInt(meters / 0.0254f);
            return $"{meters:0.00} m  <color=#9EB0BA>{inches / 12} ft {inches % 12} in</color>";
        }

        static void ResetSettings()
        {
            foreach (var e in new ConfigEntryBase[] { Plugin.MouseSensitivity, Plugin.FieldOfView, Plugin.ZoomFieldOfView, Plugin.HeadHeight,
                Plugin.CastMode, Plugin.PowerFillSeconds, Plugin.RodLift, Plugin.RodFollowLook, Plugin.AllowOnlineCasting, Plugin.WalkSpeed,
                Plugin.SharpenScenery })
                e.BoxedValue = e.DefaultValue;
            if (Driver.I != null) Driver.I.ShowHelp = true;
        }
    }
}

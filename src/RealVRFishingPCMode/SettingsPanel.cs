using System;
using BepInEx.Configuration;
using UnityEngine;

namespace RealVRFishingPCMode
{
    // F10: sliders for the main settings and a list of every key, so nobody has to edit the config file.
    // Changes apply at once and are written to the config file when the panel closes.
    static class SettingsPanel
    {
        public static bool Open;
        static int tab;
        static Keys.Bind capturing;
        static bool waitRelease, prevF10, prevEsc, dirty;
        static Vector2 scroll;
        static GUIStyle title, label, small, warn, button, keyButton, tabStyle, slider, thumb;

        public static void Tick(bool focused)
        {
            bool f10 = focused && Win.Down(Keys.F10), esc = focused && Win.Down(Keys.Escape);
            if (capturing != null) Capture(focused, esc && !prevEsc);
            else if ((f10 && !prevF10) || (Open && esc && !prevEsc)) SetOpen(!Open);
            prevF10 = f10; prevEsc = esc;
        }

        static void SetOpen(bool open)
        {
            Open = open;
            capturing = null;
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

        // sizes are for 1080p; Draw scales the whole panel up on bigger screens
        static void Styles()
        {
            if (title != null) return;
            label = new GUIStyle(GUI.skin.label) { fontSize = 17, richText = true, wordWrap = false };
            small = new GUIStyle(label) { fontSize = 14, wordWrap = true };
            small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            warn = new GUIStyle(small);
            warn.normal.textColor = new Color(1f, 0.7f, 0.3f);
            title = new GUIStyle(label) { fontSize = 24 };
            button = new GUIStyle(GUI.skin.button) { fontSize = 16 };
            keyButton = new GUIStyle(button) { fontStyle = FontStyle.Bold };
            tabStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fixedHeight = 34 };
            // the chosen option of a row stands out in gold
            foreach (var st in new[] { button, tabStyle })
            {
                st.onNormal.background = st.onActive.background;
                st.onNormal.textColor = st.onHover.textColor = st.onActive.textColor = new Color(1f, 0.8f, 0.3f);
            }
            // the default slider handle is a few pixels; make it easy to grab
            slider = new GUIStyle(GUI.skin.horizontalSlider) { fixedHeight = 12f, margin = new RectOffset(4, 4, 10, 4) };
            thumb = new GUIStyle(GUI.skin.horizontalSliderThumb) { fixedWidth = 16f, fixedHeight = 22f };
            thumb.margin = new RectOffset(0, 0, 0, 0);
        }

        public static void Draw(float scale)
        {
            Styles();
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float sw = Screen.width / scale, sh = Screen.height / scale;
            float w = Mathf.Min(680f, sw - 40f), h = Mathf.Min(780f, sh - 40f);
            var r = new Rect((sw - w) / 2f, (sh - h) / 2f, w, h);
            var oldColor = GUI.color;
            GUI.color = new Color(0.07f, 0.09f, 0.11f, 1f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = oldColor;

            GUILayout.BeginArea(new Rect(r.x + 18f, r.y + 14f, r.width - 36f, r.height - 28f));
            GUILayout.Label("<b>Real VR Fishing PC Mode</b>", title);
            GUILayout.Space(6f);
            int newTab = GUILayout.Toolbar(tab, new[] { "Settings", "Keys" }, tabStyle);
            if (newTab != tab) { tab = newTab; capturing = null; scroll = Vector2.zero; }
            GUILayout.Space(8f);
            scroll = GUILayout.BeginScrollView(scroll);
            if (tab == 0) DrawSettings(); else DrawKeys();
            GUILayout.EndScrollView();
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Changes apply right away and are saved. F10 or Esc closes.", small);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(tab == 0 ? "Reset settings" : "Reset keys", button, GUILayout.Width(130f), GUILayout.Height(32f)))
            {
                if (tab == 0) ResetSettings(); else Keys.ResetAll();
                capturing = null;
                dirty = true;
            }
            if (GUILayout.Button("Close", button, GUILayout.Width(90f), GUILayout.Height(32f))) SetOpen(false);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.matrix = oldMatrix;
        }

        static void DrawSettings()
        {
            Heading("View");
            Slider(Plugin.MouseSensitivity, "Mouse sensitivity", 0.02f, 0.5f, 0.005f, v => v.ToString("0.000"));
            Slider(Plugin.FieldOfView, "Field of view", 50f, 110f, 1f, v => v.ToString("0") + "°");
            Slider(Plugin.ZoomFieldOfView, "Zoom field of view", 10f, 60f, 1f, v => v.ToString("0") + "°");
            Slider(Plugin.HeadHeight, "Eye height", 1.2f, 2.2f, 0.01f, FeetInches);
            if (Driver.I != null)
            {
                bool help = OnOff("Controls list (" + Keys.Help.Name + ")", Driver.I.ShowHelp);
                if (help != Driver.I.ShowHelp) Driver.I.ShowHelp = help;
            }

            Heading("Fishing");
            int mode = Choice("Cast", Plugin.PowerCast ? 0 : 1, "Power bar", "Click a spot");
            string m = mode == 0 ? "PowerBar" : "ClickSpot";
            if (m != Plugin.CastMode.Value) { Plugin.CastMode.Value = m; dirty = true; }
            Slider(Plugin.PowerFillSeconds, "Power bar fill time", 0.4f, 3f, 0.05f, v => v.ToString("0.00") + " s");
            Slider(Plugin.RodLift, "Rod angle", 30f, 90f, 1f, v => v.ToString("0") + "° up");
            Slider(Plugin.RodFollowLook, "Rod follows your view", 0f, 1f, 0.05f, v => (v * 100f).ToString("0") + "%");
            Toggle(Plugin.AllowOnlineCasting, "Mouse casting online");

            Heading("Movement and graphics");
            Slider(Plugin.WalkSpeed, "Walk speed", 0.5f, 5f, 0.1f, v => (v * 2.237f).ToString("0.0") + " mph");
            Toggle(Plugin.SharpenScenery, "Sharper far scenery");
            GUILayout.Label("Sharper far scenery changes on the next start. It uses up to 1 GB more video memory.", small);
        }

        static void DrawKeys()
        {
            GUILayout.Label("Click a key, then press the new key or mouse button. Esc cancels. F9, F10 and Esc stay fixed. "
                + "Photo mode flies with Forward, Back, Left and Right; its other keys are fixed.", small);
            GUILayout.Space(6f);
            foreach (var b in Keys.All)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(b.Label, label, GUILayout.Width(330f));
                string text = capturing == b ? "Press a key..." : b.Name;
                if (GUILayout.Button(text, keyButton, GUILayout.Width(170f), GUILayout.Height(28f)))
                {
                    capturing = capturing == b ? null : b;
                    waitRelease = true;
                }
                string clash = Keys.Clash(b);
                if (clash != null) GUILayout.Label("also " + clash, warn);
                GUILayout.EndHorizontal();
            }
        }

        static void Heading(string text)
        {
            GUILayout.Space(10f);
            GUILayout.Label("<b>" + text + "</b>", label);
        }

        static void Slider(ConfigEntry<float> e, string name, float min, float max, float step, Func<float, string> format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, label, GUILayout.Width(200f));
            // only a drag changes the value, so a value set outside the slider's range in the file stays as it is
            GUI.changed = false;
            float v = GUILayout.HorizontalSlider(e.Value, min, max, slider, thumb, GUILayout.Width(260f));
            if (GUI.changed)
            {
                v = Mathf.Round(v / step) * step;
                if (Mathf.Abs(v - e.Value) > step * 0.25f) { e.Value = v; dirty = true; }
            }
            GUILayout.Space(14f);
            GUILayout.Label(format(e.Value), label);
            GUILayout.EndHorizontal();
        }

        static void Toggle(ConfigEntry<bool> e, string name)
        {
            bool v = OnOff(name, e.Value);
            if (v != e.Value) { e.Value = v; dirty = true; }
        }

        static bool OnOff(string name, bool value) => Choice(name, value ? 1 : 0, "Off", "On") == 1;

        static int Choice(string name, int index, params string[] options)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, label, GUILayout.Width(200f));
            index = GUILayout.Toolbar(index, options, button, GUILayout.Width(options[0].Length > 3 ? 260f : 140f), GUILayout.Height(28f));
            GUILayout.EndHorizontal();
            return index;
        }

        static string FeetInches(float meters)
        {
            int inches = Mathf.RoundToInt(meters / 0.0254f);
            return $"{meters:0.00} m ({inches / 12} ft {inches % 12} in)";
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

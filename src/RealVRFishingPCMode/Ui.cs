using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealVRFishingPCMode
{
    // The mod's look: Segoe UI, dark rounded cards, a water-blue accent and keycap chips for keys.
    // Sizes are written for 1080p and multiplied by Scale (fonts too), so text stays sharp at any resolution.
    static class Ui
    {
        public static float Scale = 1f;
        public static readonly Color Card = new Color(0.05f, 0.08f, 0.10f, 0.80f), Solid = new Color(0.07f, 0.10f, 0.12f, 0.97f),
            Edge = new Color(1f, 1f, 1f, 0.09f), Text = new Color(0.94f, 0.96f, 0.97f), Dim = new Color(0.62f, 0.69f, 0.73f),
            Accent = new Color(0.27f, 0.78f, 0.86f), Gold = new Color(1f, 0.77f, 0.30f), Cap = new Color(1f, 1f, 1f, 0.13f),
            Dark = new Color(0.04f, 0.06f, 0.07f);

        static Font regular;
        static bool fontsTried;
        static readonly Dictionary<int, GUIStyle> styles = new Dictionary<int, GUIStyle>();

        public static void Begin() => Scale = Mathf.Clamp(Screen.height / 1080f, 0.7f, 3f);
        public static float S(float v) => v * Scale;
        public static bool Repaint => Event.current.type == EventType.Repaint;

        public static GUIStyle Style(int size, bool bold = false, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false)
        {
            int px = Mathf.Max(8, Mathf.RoundToInt(size * Scale));
            int key = (px * 2 + (bold ? 1 : 0)) * 64 + (int)anchor * 2 + (wrap ? 1 : 0);
            if (styles.TryGetValue(key, out var st)) return st;
            if (!fontsTried)
            {
                fontsTried = true;
                // one font only: a second OS font (Segoe UI Semibold) shares the first one's text cache in Unity 6
                // and draws other labels' strings, so bold is the same font with a bold style
                try { regular = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial" }, 16); }
                catch (Exception e) { Plugin.Log.LogWarning("fonts: " + e.Message); }
            }
            st = new GUIStyle(GUI.skin.label)
            {
                font = regular, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal, fontSize = px, alignment = anchor, richText = true, wordWrap = wrap,
                padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0), clipping = TextClipping.Overflow
            };
            st.normal.textColor = Color.white;
            styles[key] = st;
            return st;
        }

        public static void Box(Rect r, Color c, float radius, float border = 0f)
        {
            if (!Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, border, radius);
        }

        // a card: fill plus a faint edge
        public static void Panel(Rect r, Color fill, float radius)
        {
            Box(r, fill, S(radius));
            Box(r, Edge, S(radius), Mathf.Max(1f, Scale));
        }

        // Unity 6's IMGUI text cache mixes up strings drawn through GUI.Label's shared content object,
        // so every string gets its own GUIContent and is drawn straight through its style
        static readonly Dictionary<string, GUIContent> contents = new Dictionary<string, GUIContent>();
        public static GUIContent Content(string text)
        {
            text = text ?? "";
            if (!contents.TryGetValue(text, out var c))
            {
                if (contents.Count > 2000) contents.Clear();
                contents[text] = c = new GUIContent(text);
            }
            return c;
        }

        public static void Label(Rect r, string text, GUIStyle st, Color c)
        {
            if (!Repaint) return;
            var old = GUI.contentColor;
            GUI.contentColor = c;
            st.Draw(r, Content(text), false, false, false, false);
            GUI.contentColor = old;
        }

        public static float Width(string text, GUIStyle st) => st.CalcSize(Content(text)).x;

        // controller buttons travel through the same string lists as key names, marked by this first character
        public const char PadMark = '';
        public static string PadKey(Pad.Button b) => b == Pad.Button.None ? null : PadMark.ToString() + (char)('A' + (int)b);
        static bool IsPad(string key, out Pad.Button b)
        {
            b = Pad.Button.None;
            if (key == null || key.Length != 2 || key[0] != PadMark) return false;
            b = (Pad.Button)(key[1] - 'A');
            return true;
        }
        static bool Face(Pad.Button b) => b >= Pad.Button.A && b <= Pad.Button.Y;

        public static float ChipWidth(string key, float h = 26f, int size = 14)
        {
            if (IsPad(key, out var b))
            {
                if (Face(b)) return S(h);
                key = Pad.Display(b);
            }
            return Mathf.Max(S(h), Width(key, Style(size, true, TextAnchor.MiddleCenter)) + S(14f));
        }

        // a key drawn as a keycap (or a controller button); returns its width
        public static float Chip(float x, float y, string key, float h = 26f, int size = 14)
        {
            float w = ChipWidth(key, h, size);
            if (IsPad(key, out var pb))
            {
                if (Face(pb)) { FaceButton(new Rect(x, y, w, S(h)), pb); return w; }
                key = Pad.Display(pb);
            }
            var st = Style(size, true, TextAnchor.MiddleCenter);
            var r = new Rect(x, y, w, S(h));
            Box(r, Cap, S(5f));
            Box(new Rect(r.x, r.yMax - S(2f), r.width, S(2f)), new Color(0f, 0f, 0f, 0.25f), S(1f));
            Box(r, Edge, S(5f), Mathf.Max(1f, Scale));
            Label(r, key, st, Text);
            return w;
        }

        public static float ChipsWidth(string[] keys, float h = 26f, int size = 14)
        {
            float w = 0f;
            foreach (var k in keys) w += ChipWidth(k, h, size) + S(4f);
            return keys.Length > 0 ? w - S(4f) : 0f;
        }

        // A, B, X, Y as round buttons: Xbox letters on their colors, or PlayStation's four shapes in theirs
        static void FaceButton(Rect r, Pad.Button b)
        {
            int i = (int)b - (int)Pad.Button.A;
            if (!Pad.PlayStation)
            {
                Color[] fill = { new Color(0.37f, 0.72f, 0.25f), new Color(0.86f, 0.27f, 0.24f), new Color(0.22f, 0.52f, 0.92f), new Color(0.96f, 0.74f, 0.18f) };
                Box(r, fill[i], r.height / 2f);
                Label(r, "ABXY".Substring(i, 1), Style(Mathf.RoundToInt(r.height / Scale * 0.55f), true, TextAnchor.MiddleCenter), Dark);
                return;
            }
            Color[] ink = { new Color(0.49f, 0.70f, 1f), new Color(1f, 0.42f, 0.44f), new Color(0.98f, 0.55f, 0.82f), new Color(0.30f, 0.86f, 0.70f) };
            Box(r, new Color(0.10f, 0.13f, 0.16f, 0.95f), r.height / 2f);
            Box(r, new Color(1f, 1f, 1f, 0.3f), r.height / 2f, Mathf.Max(1f, Scale));
            Vector2 c = r.center;
            float s = r.height * 0.24f, t = Mathf.Max(1.5f, r.height * 0.09f);
            var col = ink[i];
            switch (b)
            {
                case Pad.Button.A:
                    Line(c + new Vector2(-s, -s), c + new Vector2(s, s), t, col);
                    Line(c + new Vector2(-s, s), c + new Vector2(s, -s), t, col);
                    break;
                case Pad.Button.B:
                    Box(new Rect(c.x - s * 1.1f, c.y - s * 1.1f, s * 2.2f, s * 2.2f), col, s * 1.1f, t);
                    break;
                case Pad.Button.X:
                    Box(new Rect(c.x - s, c.y - s, s * 2f, s * 2f), col, t * 0.5f, t);
                    break;
                case Pad.Button.Y:
                    var top = c + new Vector2(0f, -s * 1.15f);
                    var bl = c + new Vector2(-s * 1.15f, s * 0.85f);
                    var br = c + new Vector2(s * 1.15f, s * 0.85f);
                    Line(top, bl, t, col); Line(bl, br, t, col); Line(br, top, t, col);
                    break;
            }
        }

        // a straight stroke with round ends
        static void Line(Vector2 a, Vector2 b, float width, Color c)
        {
            if (!Repaint) return;
            var d = b - a;
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            Box(new Rect(a.x - width / 2f, a.y - width / 2f, d.magnitude + width, width), c, width / 2f);
            GUI.matrix = m;
        }

        // text with keycaps in it; a part starting with KeyMark is drawn as a key, PadMark as a controller button
        public const char KeyMark = '';
        public static float Flow(float x, float y, string[] parts, float h = 26f, int size = 15, bool measureOnly = false)
        {
            float cx = x;
            var st = Style(size);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p == null) continue;
                if (i > 0) cx += S(6f);
                if (p.Length > 0 && p[0] == KeyMark) p = p.Substring(1);
                else if (!IsPad(p, out _))
                {
                    float tw = Width(p, st);
                    if (!measureOnly) Label(new Rect(cx, y, tw + S(4f), S(h)), p, st, Text);
                    cx += tw;
                    continue;
                }
                cx += measureOnly ? ChipWidth(p, h) : Chip(cx, y, p, h);
            }
            return cx - x;
        }

        // keys then a description, on one line; returns the line's width
        public static float KeyLine(float x, float y, string[] keys, string text, float h = 26f, int size = 15, bool measureOnly = false)
        {
            float cx = x;
            if (measureOnly) cx += ChipsWidth(keys, h);
            else foreach (var k in keys) cx += Chip(cx, y, k, h) + S(4f);
            if (!measureOnly && keys.Length > 0) cx -= S(4f);
            cx += S(10f);
            var st = Style(size);
            float tw = Width(text, st);
            if (!measureOnly) Label(new Rect(cx, y, tw + S(4f), S(h)), text, st, Text);
            return cx + tw - x;
        }

        public static bool Hover(Rect r) => r.Contains(Event.current.mousePosition);

        // an invisible button over whatever was drawn there
        public static bool Click(Rect r) => GUI.Button(r, GUIContent.none, GUIStyle.none);

        public static bool Button(Rect r, string text, bool primary = false)
        {
            bool hover = Hover(r);
            Color fill = primary ? (hover ? Color.Lerp(Accent, Color.white, 0.15f) : Accent) : new Color(1f, 1f, 1f, hover ? 0.16f : 0.08f);
            Box(r, fill, S(8f));
            if (!primary) Box(r, Edge, S(8f), Mathf.Max(1f, Scale));
            Label(r, text, Style(15, true, TextAnchor.MiddleCenter), primary ? Dark : Text);
            return Click(r);
        }

        // a row of options, the chosen one filled with the accent color
        public static int Segmented(Rect r, int index, string[] options)
        {
            Box(r, new Color(1f, 1f, 1f, 0.06f), S(8f));
            Box(r, Edge, S(8f), Mathf.Max(1f, Scale));
            float w = r.width / options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                var seg = new Rect(r.x + w * i + S(3f), r.y + S(3f), w - S(6f), r.height - S(6f));
                bool on = i == index;
                if (on) Box(seg, Accent, S(6f));
                else if (Hover(seg)) Box(seg, new Color(1f, 1f, 1f, 0.08f), S(6f));
                Label(seg, options[i], Style(14, true, TextAnchor.MiddleCenter), on ? Dark : Dim);
                if (Click(seg)) index = i;
            }
            return index;
        }

        // a rounded track filled up to the knob; press anywhere on it and drag
        public static float Slider(Rect r, float value, float min, float max)
        {
            float knob = S(18f);
            var track = new Rect(r.x + knob / 2f, r.center.y - S(3f), r.width - knob, S(6f));
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; value = At(e.mousePosition.x); GUI.changed = true; e.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) { value = At(e.mousePosition.x); GUI.changed = true; e.Use(); }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
            }
            float t = Mathf.InverseLerp(min, max, Mathf.Clamp(value, min, max));
            Box(track, new Color(1f, 1f, 1f, 0.12f), S(3f));
            Box(new Rect(track.x, track.y, track.width * t, track.height), Accent, S(3f));
            var k = new Rect(track.x + track.width * t - knob / 2f, r.center.y - knob / 2f, knob, knob);
            Box(new Rect(k.x - S(1f), k.y + S(1f), k.width + S(2f), k.height + S(2f)), new Color(0f, 0f, 0f, 0.35f), knob / 2f + S(1f));
            Box(k, GUIUtility.hotControl == id || Hover(r) ? Color.white : new Color(0.88f, 0.93f, 0.95f), knob / 2f);
            return value;

            float At(float x) => Mathf.Lerp(min, max, Mathf.InverseLerp(track.x, track.xMax, x));
        }

        // the short name for a keycap
        public static string Key(Keys.Bind b) => Key(b.Vk);
        public static string Key(int vk)
        {
            switch (vk)
            {
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                default: return Keys.Display(vk);
            }
        }
    }
}

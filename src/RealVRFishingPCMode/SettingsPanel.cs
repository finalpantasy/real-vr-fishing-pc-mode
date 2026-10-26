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
        static string clashNote;
        static float contentHeight, bodyHeight;
        static readonly string[] Tabs = { "View", "Fishing", "System", "Keys", "Controller", "Buttons" };

        // on a controller: the D-pad or left stick moves a focus ring through the controls in drawing order, left / right
        // change the focused one, A presses it, LB / RB switch tabs. Tick reads the pad; the next repaint acts on it
        static int focus, items, count, navSide, prevDy, prevDx, prevTab;
        static bool navPress, navScroll, inBody, prevPadA;
        static float dyAt, dxAt;

        // the power bar fill time that "Power bar speed 100%" means
        public const float BaseFillSeconds = 1.375f;

        public static void Tick(bool focused)
        {
            bool f10 = focused && Win.Down(Keys.F10), esc = focused && Win.Down(Keys.Escape), padB = focused && Pad.Down(Pad.Button.B);
            bool escPressed = esc && !prevEsc, padBPressed = padB && !prevPadB, act = false;
            // a key can't be typed on a handheld, so B also cancels waiting for one
            if (capturing != null) Capture(focused, escPressed || padBPressed);
            else if (capturingPad != null) CapturePad(focused, escPressed);
            else if ((f10 && !prevF10) || (Open && escPressed) || (Open && padBPressed)) SetOpen(!Open);
            else act = Open;
            Navigate(focused, act);
            prevF10 = f10; prevEsc = esc; prevPadB = padB;
        }

        // read every frame, so a button still held after binding it doesn't count as a fresh press
        static void Navigate(bool focused, bool act)
        {
            int dy = !focused ? 0 : Pad.Down(Pad.Button.DpadDown) || Pad.Down(Pad.Button.LStickDown) ? 1 : Pad.Down(Pad.Button.DpadUp) || Pad.Down(Pad.Button.LStickUp) ? -1 : 0;
            int dx = !focused ? 0 : Pad.Down(Pad.Button.DpadRight) || Pad.Down(Pad.Button.LStickRight) ? 1 : Pad.Down(Pad.Button.DpadLeft) || Pad.Down(Pad.Button.LStickLeft) ? -1 : 0;
            int tb = !focused ? 0 : Pad.Down(Pad.Button.RB) ? 1 : Pad.Down(Pad.Button.LB) ? -1 : 0;
            bool a = focused && Pad.Down(Pad.Button.A);
            int vStep = Repeat(dy, ref prevDy, ref dyAt), hStep = Repeat(dx, ref prevDx, ref dxAt);
            bool tabPress = tb != 0 && tb != prevTab, aPress = a && !prevPadA;
            prevTab = tb; prevPadA = a;
            if (!act) return;
            if (vStep != 0) { focus = Mathf.Clamp(focus + vStep, 0, Mathf.Max(0, items - 1)); navScroll = true; }
            if (hStep != 0) navSide = hStep;
            if (aPress) navPress = true;
            if (tabPress) SetTab((tab + tb + Tabs.Length) % Tabs.Length);
        }

        // a direction moves once, then again and again while held
        static int Repeat(int d, ref int prev, ref float next)
        {
            int step = 0;
            if (d != prev) { step = d; next = Time.unscaledTime + 0.4f; }
            else if (d != 0 && Time.unscaledTime >= next) { step = d; next = Time.unscaledTime + 0.09f; }
            prev = d;
            return step;
        }

        static void SetTab(int t)
        {
            tab = t;
            capturing = capturingPad = null;
            scroll = Vector2.zero;
            focus = 0;
        }

        static bool Repaint => Event.current.type == EventType.Repaint;

        // each control takes the next number as it's drawn; the focused one gets an accent ring and is kept in view
        static bool Focus(Rect r)
        {
            int i = count++;
            if (!Pad.InUse || i != focus) return false;
            if (Repaint)
            {
                Ui.Box(new Rect(r.x - S(4f), r.y - S(4f), r.width + S(8f), r.height + S(8f)), Ui.Accent, S(10f), Mathf.Max(1f, S(2f)));
                if (navScroll && inBody)
                {
                    if (r.y - S(48f) < scroll.y) scroll.y = Mathf.Max(0f, r.y - S(48f));
                    else if (r.yMax + S(40f) > scroll.y + bodyHeight) scroll.y = r.yMax + S(40f) - bodyHeight;
                }
            }
            return true;
        }

        // A on the focused control, used up by the first control that asks
        static bool Pressed(bool focused)
        {
            if (!focused || !navPress || !Repaint) return false;
            navPress = false;
            return true;
        }

        // left (-1) or right (1) on the focused control, used up by the first control that asks
        static int Side(bool focused)
        {
            if (!focused || !Repaint) return 0;
            int s = navSide;
            navSide = 0;
            return s;
        }

        // about 60 D-pad steps from one end of a slider to the other, in whole steps of its own
        static float NavStep(float min, float max, float step) => step * Mathf.Max(1f, Mathf.Round((max - min) / step / 60f));

        // holding the controls-list button on a controller, for handhelds with no F10
        public static void Toggle() { if (capturing == null && capturingPad == null) SetOpen(!Open); }

        static void SetOpen(bool open)
        {
            Open = open;
            capturing = capturingPad = null;
            if (open) { focus = 0; navScroll = true; navPress = false; navSide = 0; }
            if (!open) Save();
        }

        // something outside the panel changed a setting; it is saved with the rest
        public static void Changed() => dirty = true;

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
            count = 0;
            // dim the game behind the panel
            Ui.Box(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.35f), 0f);
            float w = Mathf.Min(S(760f), Screen.width - S(40f)), h = Mathf.Min(S(880f), Screen.height - S(40f));
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            Ui.Panel(r, Ui.Solid, 16f);
            float pad = S(28f), x = r.x + pad, iw = w - 2f * pad;

            // header
            Ui.Label(new Rect(x, r.y + S(22f), iw, S(32f)), "Settings", Ui.Style(26, true), Ui.Text);
            string ver = "Real VR Fishing PC Mode " + Plugin.Version;
            Ui.Label(new Rect(x, r.y + S(54f), iw, S(20f)), ver, Ui.Style(13), Ui.Dim);
            if (Updates.Newer != null)
            {
                // a newer release is out: a link to its page
                var vs = Ui.Style(13, true);
                float lx = x + Ui.Width(ver, Ui.Style(13)) + S(14f);
                string link = "Version " + Updates.Newer + " is out. Get it on GitHub";
                var lr = new Rect(lx, r.y + S(54f), Ui.Width(link, vs) + S(4f), S(20f));
                Ui.Label(lr, link, vs, Ui.Hover(lr) ? Ui.Text : Ui.Gold);
                if (Ui.Click(lr)) Application.OpenURL(Updates.Page);
            }
            var close = new Rect(r.xMax - S(20f) - S(36f), r.y + S(20f), S(36f), S(36f));
            Ui.Box(close, new Color(1f, 1f, 1f, Ui.Hover(close) ? 0.14f : 0.06f), S(18f));
            Ui.Label(close, "×", Ui.Style(24, false, TextAnchor.MiddleCenter), Ui.Text);
            if (Ui.Click(close)) { SetOpen(false); return; }

            // tabs, underlined in the accent color
            float ty = r.y + S(88f), tx = x;
            // on a controller, LB and RB chips on either side of the tabs
            if (Pad.InUse) tx += Ui.ChipWidth(Ui.PadKey(Pad.Button.LB), 24f) + S(8f);
            if (Pad.InUse) Ui.Chip(x, ty + S(5f), Ui.PadKey(Pad.Button.LB), 24f);
            for (int i = 0; i < Tabs.Length; i++)
            {
                var st = Ui.Style(16, true, TextAnchor.MiddleCenter);
                float tw = Ui.Width(Tabs[i], st) + S(24f);
                var tr = new Rect(tx, ty, tw, S(34f));
                Ui.Label(tr, Tabs[i], st, i == tab ? Ui.Text : Ui.Hover(tr) ? Color.Lerp(Ui.Dim, Ui.Text, 0.5f) : Ui.Dim);
                if (i == tab) Ui.Box(new Rect(tr.x + S(8f), tr.yMax - S(3f), tr.width - S(16f), S(3f)), Ui.Accent, S(1.5f));
                if (Ui.Click(tr) && tab != i) SetTab(i);
                tx += tw + S(8f);
            }
            if (Pad.InUse) Ui.Chip(tx, ty + S(5f), Ui.PadKey(Pad.Button.RB), 24f);
            Ui.Box(new Rect(x, ty + S(34f), iw, Mathf.Max(1f, Ui.Scale)), Ui.Edge, 0f);

            // body, scrolled with the mouse wheel when it doesn't fit
            float footH = S(72f), top = ty + S(46f);
            var view = new Rect(x - S(4f), top, iw + S(8f), r.yMax - footH - top);
            bodyHeight = view.height;
            scroll = GUI.BeginScrollView(view, scroll, new Rect(0f, 0f, view.width - S(8f), Mathf.Max(contentHeight, view.height)), false, false, GUIStyle.none, GUIStyle.none);
            float bx0 = S(4f), bw0 = view.width - S(16f);
            inBody = true;
            contentHeight = tab == 0 ? DrawView(bx0, bw0) : tab == 1 ? DrawFishing(bx0, bw0) : tab == 2 ? DrawSystem(bx0, bw0)
                : tab == 3 ? DrawKeys(bx0, bw0) : tab == 4 ? DrawPad(bx0, bw0) : DrawButtons(bx0, bw0);
            inBody = false;
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
            if (Pad.InUse)
                Ui.Flow(x, fy + S(25f), new[] { Ui.PadKey(Pad.Button.DpadDown), "moves,", Ui.PadKey(Pad.Button.A), "changes,", Ui.PadKey(Pad.Button.B), "closes." }, 24f, 13);
            else Ui.Label(new Rect(x, fy + S(18f), iw * 0.5f, S(40f)), "Changes apply right away.  F10 or Esc closes.", Ui.Style(13), Ui.Dim);
            var done = new Rect(r.xMax - pad - S(110f), fy + S(18f), S(110f), S(40f));
            var reset = new Rect(done.x - S(12f) - S(150f), done.y, S(150f), S(40f));
            bool focusReset = Focus(reset);
            if (Ui.Button(reset, tab == 3 ? "Reset keys" : tab == 5 ? "Reset buttons" : "Reset this tab") || Pressed(focusReset))
            {
                if (tab <= 2) ResetSettings(tab);
                else if (tab == 3) Keys.ResetAll();
                else if (tab == 4) foreach (var e in new ConfigEntryBase[] { Pad.Glyphs, Pad.LookSpeed, Pad.InvertLook, Pad.RumbleStrength, Pad.ReelTrigger, Pad.HookTrigger }) e.BoxedValue = e.DefaultValue;
                else Keys.ResetPad();
                capturing = capturingPad = null;
                dirty = true;
            }
            if (Side(focusReset) > 0) focus++;
            bool focusDone = Focus(done);
            if (Ui.Button(done, "Done", primary: true) || Pressed(focusDone)) SetOpen(false);
            if (Side(focusDone) < 0) focus--;
            if (Repaint)
            {
                items = count;
                focus = Mathf.Clamp(focus, 0, Mathf.Max(0, items - 1));
                navPress = navScroll = false;
                navSide = 0;
            }
        }

        // each tab draws its rows and returns its height
        static float DrawView(float x, float w)
        {
            float y = 0f;
            Section(ref y, x, "VIEW");
            Slider(ref y, x, w, Plugin.MouseSensitivity, "Mouse sensitivity", 0.02f, 0.5f, 0.005f, v => v.ToString("0.000"),
                "How far your view turns when you move the mouse.");
            Slider(ref y, x, w, Plugin.FieldOfView, "Field of view", 50f, 110f, 1f, v => v.ToString("0") + "°",
                "How much of the world fits on screen. Higher shows more around you.");
            Slider(ref y, x, w, Plugin.Zoom1FieldOfView, "Zoom, first press", 10f, 90f, 1f, v => v.ToString("0") + "°",
                "Press " + Ui.Key(Keys.Zoom) + " once to zoom in this far. Lower zooms in closer.");
            Slider(ref y, x, w, Plugin.ZoomFieldOfView, "Zoom, second press", 10f, 90f, 1f, v => v.ToString("0") + "°",
                "Press it again to zoom in this far.");
            string[] zoomOrders = { "Cycle", "BackAndForth" };
            int zo = Choice(ref y, x, w, "Zoom order", Plugin.ZoomOrder.Value == "BackAndForth" ? 1 : 0, null,
                "Cycle: a third press goes straight back to normal. Back and forth: it steps back out one level at a time, in, in more, in, normal.", "Cycle", "Back and forth");
            if (zoomOrders[zo] != Plugin.ZoomOrder.Value) { Plugin.ZoomOrder.Value = zoomOrders[zo]; dirty = true; }
            Slider(ref y, x, w, Plugin.HeadHeight, "Eye height", 1.2f, 2.2f, 0.01f, FeetInches,
                "How tall you stand. " + Ui.Key(Keys.Taller) + " and " + Ui.Key(Keys.Shorter) + " change it while you play.");
            if (Driver.I != null)
            {
                int help = Choice(ref y, x, w, "Controls list", Driver.I.HelpMode, null,
                    "The list of controls at the top left. Small is the same list at a smaller size. " + Ui.Key(Keys.Help) + " switches it in game.", "Full", "Small", "Off");
                if (help != Driver.I.HelpMode) Driver.I.HelpMode = help;
            }
            string[] dots = { "Auto", "On", "Off" };
            int dot = Choice(ref y, x, w, "Aim dot", Mathf.Max(0, System.Array.IndexOf(dots, Plugin.AimDot.Value)), null,
                "The dot in the middle of the screen. Auto shows it to aim a cast or point at a menu, and hides it while the line is out.", dots);
            if (dots[dot] != Plugin.AimDot.Value) { Plugin.AimDot.Value = dots[dot]; dirty = true; }

            Section(ref y, x, "STATS CORNER");
            string[] corner = { "Always", "On change", "Off" };
            int sc = Choice(ref y, x, w, "Stats corner", Mathf.Max(0, System.Array.IndexOf(corner, Plugin.StatsCorner.Value)), null,
                "Your stats at the top right while you fish, as many as you switch on below. On change shows them for a few seconds when one changes. Hidden on the catch screen and in photo mode.", corner);
            if (corner[sc] != Plugin.StatsCorner.Value) { Plugin.StatsCorner.Value = corner[sc]; dirty = true; }
            if (sc != 2)
            {
                Toggle(ref y, x, w, Plugin.HudLevel, "Level", null, "Your level and its experience as you have it out of what it takes, like 1,240 / 2,000 XP. At the top level it turns gold and counts the experience since.");
                Toggle(ref y, x, w, Plugin.HudSeason, "Season score", null, "Your season score for this water: freshwater on lakes and rivers, saltwater at the beach and sea.");
                if (Plugin.HudSeason.Value)
                    Toggle(ref y, x, w, Plugin.HudBothSeasons, "Both seasons", null, "Freshwater and saltwater scores together, wherever you fish.");
                Toggle(ref y, x, w, Plugin.HudCredits, "Credits", null, "The credits you have to spend.");
                Toggle(ref y, x, w, Plugin.HudSpecies, "Species caught", null, "How many kinds of fish you have caught, out of all of them.");
                Toggle(ref y, x, w, Plugin.HudLifetime, "Fish caught in all", null, "Every fish you have ever landed.");
                Toggle(ref y, x, w, Plugin.HudSession, "This session", null, "Fish caught and time on the water since you started the game.");
                if (Plugin.HudSession.Value)
                {
                    Toggle(ref y, x, w, Plugin.HudBiggest, "Biggest this session", null, "The heaviest fish so far, with its kind and weight.");
                    Toggle(ref y, x, w, Plugin.HudEarned, "Earned this session", null, "Credits, experience and season points earned since you started the game. Spending credits doesn't count against it.");
                }
            }
            string[] tut = { "Left", "Right", "Center" };
            int tp = Choice(ref y, x, w, "Tutorial text", Mathf.Max(0, System.Array.IndexOf(tut, Plugin.TutorialPanel.Value)), null,
                "Where the game's tutorial instructions sit: pinned to the left or right side of the screen as you look around, or in front of you.", tut);
            if (tut[tp] != Plugin.TutorialPanel.Value) { Plugin.TutorialPanel.Value = tut[tp]; dirty = true; }

            Section(ref y, x, "MOVEMENT");
            Slider(ref y, x, w, Plugin.WalkSpeed, "Walk speed", 0.5f, 5f, 0.1f, v => (v * 2.237f).ToString("0.0") + " mph",
                "How fast you walk in the lodge.");
            return y + S(8f);
        }

        static float DrawFishing(float x, float w)
        {
            float y = 0f;
            Section(ref y, x, "CASTING");
            int mode = Choice(ref y, x, w, "Cast with", Plugin.PowerCast ? 0 : 1, null,
                "Power bar: hold to wind up, let go to cast toward the crosshair. Click a spot: click the water where the lure should land.", "Power bar", "Click a spot");
            string m = mode == 0 ? "PowerBar" : "ClickSpot";
            if (m != Plugin.CastMode.Value) { Plugin.CastMode.Value = m; dirty = true; }
            PowerSpeed(ref y, x, w);
            Toggle(ref y, x, w, Plugin.AllowOnlineCasting, "Mouse casting online", null,
                "Lets this mod's casting work in online rooms. Off keeps it fair for players in VR.");

            Section(ref y, x, "ROD");
            Slider(ref y, x, w, Plugin.RodLift, "Rod angle", 30f, 90f, 1f, v => v.ToString("0") + "° up",
                "How high the rod tip points while you look straight ahead.");
            Slider(ref y, x, w, Plugin.RodFollowLook, "Rod tilts with your view", 0f, 1f, 0.05f, v => (v * 100f).ToString("0") + "%",
                "When you look up or down, the rod tilts along by this much. 0% keeps the rod still.");

            Section(ref y, x, "CATCHES");
            Toggle(ref y, x, w, Plugin.CustomCatchCard, "Full-screen catch screen", null,
                "Your live fish, its size against the smallest and biggest of its kind, and what each choice earns. " + Ui.Key(Keys.Keep) + " keeps, " + Ui.Key(Keys.Release) + " releases, " + Ui.Key(Keys.CardToggle) + " shows the game's board.");
            Toggle(ref y, x, w, Plugin.CatchScreenMotion, "Catch screen animations", null,
                "Slides the catch screen in, sends the fish off when you choose, and floats up what you earned. Off opens and closes it at once.");

            var note = Ui.Style(14, false, TextAnchor.UpperLeft, true);
            const string hands = "Reeling is always one-handed: hold the reel button to reel. The game's two-handed reeling needs a VR hand turning the crank, so the mod switches it off.";
            y += S(18f);
            float nh = note.CalcHeight(Ui.Content(hands), w);
            Ui.Label(new Rect(x, y, w, nh), hands, note, Ui.Dim);
            return y + nh + S(8f);
        }

        static float DrawSystem(float x, float w)
        {
            float y = 0f;
            Section(ref y, x, "GRAPHICS");
            Toggle(ref y, x, w, Plugin.SharpenScenery, "Sharper distant scenery", null,
                "Stops far shores and skylines from looking blurry or shimmering. Uses up to 1 GB more video memory. Applies after a restart.");
            string[] caps = { "Monitor", "60", "90", "120", "144", "Off" };
            int cap = Choice(ref y, x, w, "Frame rate limit", Mathf.Max(0, System.Array.IndexOf(caps, Plugin.FrameCap.Value)), null,
                "Monitor matches your screen's refresh rate. Off lets it run as fast as it can, which can make the fishing line twitch and jump.", caps);
            if (caps[cap] != Plugin.FrameCap.Value) { Plugin.FrameCap.Value = caps[cap]; dirty = true; }

            Section(ref y, x, "UPDATES");
            Toggle(ref y, x, w, Plugin.CheckForUpdates, "Check for updates", null,
                "Each time the game starts, asks GitHub once whether a newer version of this mod is out.");
            return y + S(8f);
        }

        // the keys tab; returns its height
        static float DrawKeys(float x, float w)
        {
            float y = S(10f);
            var note = Ui.Style(14, false, TextAnchor.UpperLeft, true);
            string text = "Click a key, then press the new key or mouse button. Esc" + (Pad.InUse ? " or " + Pad.Display(Pad.Button.B) : "")
                + " cancels. F9, F10 and Esc stay fixed. Photo mode moves with Forward, Back, Left and Right; its other keys are fixed.";
            float nh = note.CalcHeight(Ui.Content(text), w);
            Ui.Label(new Rect(x, y, w, nh), text, note, Ui.Dim);
            y += nh + S(14f);
            float rowH = S(36f), colW = (w - S(24f)) / 2f, bw = S(118f), top = y;
            int rows = (Keys.All.Count + 1) / 2;
            for (int i = 0; i < Keys.All.Count; i++)
            {
                var b = Keys.All[i];
                float cx = x + (i < rows ? 0f : colW + S(24f));
                y = top + (i % rows) * rowH;
                var row = new Rect(cx - S(6f), y, colW + S(12f), rowH);
                if (Ui.Hover(row)) Ui.Box(row, new Color(1f, 1f, 1f, 0.04f), S(8f));
                Ui.Label(new Rect(cx, y, colW - bw - S(10f), rowH), b.Label, Fit(b.Label, colW - bw - S(10f)), Ui.Text);
                var br = new Rect(cx + colW - bw, y + S(4f), bw, rowH - S(8f));
                bool cap = capturing == b, f = Focus(br);
                Ui.Box(br, cap ? new Color(1f, 0.77f, 0.3f, 0.16f) : new Color(1f, 1f, 1f, Ui.Hover(br) ? 0.18f : 0.11f), S(6f));
                Ui.Box(br, cap ? Ui.Gold : Ui.Edge, S(6f), Mathf.Max(1f, cap ? S(1.5f) : Ui.Scale));
                Ui.Label(br, cap ? "Press a key…" : b.Name, Ui.Style(14, true, TextAnchor.MiddleCenter), cap ? Ui.Gold : Ui.Text);
                if (Ui.Click(br) || Pressed(f)) { capturing = cap ? null : b; waitRelease = true; }
                OtherColumn(f, i, rows);
                // a key used twice gets a gold edge; hovering it says what else uses it
                string clash = Keys.Clash(b);
                if (clash != null)
                {
                    Ui.Box(br, Ui.Gold, S(6f), Mathf.Max(1f, S(1.5f)));
                    if (Ui.Hover(br)) clashNote = b.Name + " also " + clash;
                }
            }
            return ClashLine(top + rows * rowH, x, w);
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
            int g = Choice(ref y, x, w, "Button labels", Pad.PlayStation ? 1 : 0, null,
                "Which controller's button names and symbols the mod shows.", "Xbox", "PlayStation");
            string gv = g == 1 ? "PlayStation" : "Xbox";
            if (gv != Pad.Glyphs.Value) { Pad.Glyphs.Value = gv; Keys.Version++; dirty = true; }
            Slider(ref y, x, w, Pad.LookSpeed, "Look speed", 60f, 400f, 5f, v => v.ToString("0") + "°/s",
                "How fast the right stick turns your view.");
            Toggle(ref y, x, w, Pad.InvertLook, "Invert look", null,
                "On: push the right stick up to look down, like a flight game.");
            Slider(ref y, x, w, Pad.RumbleStrength, "Rumble", 0f, 1f, 0.05f, v => v <= 0f ? "Off" : (v * 100f).ToString("0") + "%",
                "How hard the controller shakes. A light tick when a fish nibbles, a hard thump when it takes the bait (set the hook), and a steady rumble while the line is red (ease off).");
            int rt = Choice(ref y, x, w, "Reel trigger", Pad.ReelTrigger.Value == "Full" ? 1 : 0, null,
                "Squeeze: reels faster the further you pull, with full speed only near a full pull. Full: any pull reels at full speed.", "Squeeze", "Full");
            string rv = rt == 1 ? "Full" : "Squeeze";
            if (rv != Pad.ReelTrigger.Value) { Pad.ReelTrigger.Value = rv; dirty = true; }
            int ht = Choice(ref y, x, w, "Hook trigger", Pad.HookTrigger.Value == "Full" ? 1 : 0, null,
                "Squeeze: jerks the rod as sharply as you squeeze and raises it as far as you hold. Full: any pull is a full jerk and a full raise.", "Squeeze", "Full");
            string hv = ht == 1 ? "Full" : "Squeeze";
            if (hv != Pad.HookTrigger.Value) { Pad.HookTrigger.Value = hv; dirty = true; }

            var note = Ui.Style(14, false, TextAnchor.UpperLeft, true);
            const string sticks = "The right stick looks around and the left stick walks. On Squeeze, bringing a lure in follows the reel trigger smoothly, "
                + "with full speed near a full pull. With a fish on, reeling is on or off, as in the game.";
            y += S(18f);
            float sh = note.CalcHeight(Ui.Content(sticks), w);
            Ui.Label(new Rect(x, y, w, sh), sticks, note, Ui.Dim);
            return y + sh + S(8f);
        }

        // the controller buttons tab; returns its height
        static float DrawButtons(float x, float w)
        {
            float y = S(10f);
            var note = Ui.Style(14, false, TextAnchor.UpperLeft, true);
            string text = Pad.InUse ? "Pick a button with " + Pad.Display(Pad.Button.A) + ", then press the new controller button or push the left stick. "
                    + "On a keyboard, Esc cancels and Delete clears it."
                : "Click a button, then press the new controller button or push the left stick. Esc cancels, Delete clears it.";
            float nh = note.CalcHeight(Ui.Content(text), w);
            Ui.Label(new Rect(x, y, w, nh), text, note, Ui.Dim);
            y += nh + S(14f);
            float rowH = S(36f), colW = (w - S(24f)) / 2f, bw = S(118f), top = y;
            int rows = (Keys.All.Count + 1) / 2;
            for (int i = 0; i < Keys.All.Count; i++)
            {
                var b = Keys.All[i];
                float cx = x + (i < rows ? 0f : colW + S(24f));
                y = top + (i % rows) * rowH;
                var row = new Rect(cx - S(6f), y, colW + S(12f), rowH);
                if (Ui.Hover(row)) Ui.Box(row, new Color(1f, 1f, 1f, 0.04f), S(8f));
                Ui.Label(new Rect(cx, y, colW - bw - S(10f), rowH), b.Label, Fit(b.Label, colW - bw - S(10f)), Ui.Text);
                var br = new Rect(cx + colW - bw, y + S(4f), bw, rowH - S(8f));
                bool cap = capturingPad == b, f = Focus(br);
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
                if (Ui.Click(br) || Pressed(f)) { capturingPad = cap ? null : b; capturing = null; waitRelease = true; }
                OtherColumn(f, i, rows);
                string clash = Keys.Clash(b, pad: true);
                if (clash != null)
                {
                    Ui.Box(br, Ui.Gold, S(6f), Mathf.Max(1f, S(1.5f)));
                    if (Ui.Hover(br)) clashNote = Pad.Display(b.PadButton) + " also " + clash;
                }
            }
            return ClashLine(top + rows * rowH, x, w);
        }

        // in a two-column list, left / right jump to the same row in the other column
        static void OtherColumn(bool focused, int i, int rows)
        {
            int side = Side(focused), j = i + side * rows;
            if (side != 0 && j >= 0 && j < Keys.All.Count) { focus += side * rows; navScroll = true; }
        }

        static void Section(ref float y, float x, string name)
        {
            y += S(18f);
            Ui.Label(new Rect(x, y, S(400f), S(18f)), name, Ui.Style(12, true), Ui.Accent);
            y += S(26f);
        }

        // label on the left, control in the middle, value or note on the right, and a line under them saying what it does
        static Rect Row(ref float y, float x, float w, string name, out Rect value, string help)
        {
            const float rowH = 44f;
            Ui.Label(new Rect(x, y, S(250f), S(rowH)), name, Ui.Style(15), Ui.Text);
            var control = new Rect(x + S(250f), y + S(7f), S(300f), S(rowH - 14f));
            value = new Rect(control.xMax + S(20f), y, w - (control.xMax + S(20f) - x), S(rowH));
            y += S(rowH);
            if (help != null)
            {
                // a help line that wraps pushes the next row down instead of running into it
                var hs = Ui.Style(13, false, TextAnchor.UpperLeft, true);
                float hh = hs.CalcHeight(Ui.Content(help), w);
                Ui.Label(new Rect(x, y - S(6f), w, hh), help, hs, Ui.Dim);
                y += Mathf.Max(S(20f), hh + S(2f));
            }
            return control;
        }

        static void Slider(ref float y, float x, float w, ConfigEntry<float> e, string name, float min, float max, float step, Func<float, string> format, string help = null)
        {
            var c = Row(ref y, x, w, name, out var value, help);
            bool f = Focus(c);
            // only a drag or the D-pad changes the value, so a value set outside the slider's range in the file stays as it is
            GUI.changed = false;
            float v = Ui.Slider(c, e.Value, min, max);
            int side = Side(f);
            if (side != 0) { v = Mathf.Clamp(e.Value + side * NavStep(min, max, step), min, max); GUI.changed = true; }
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
            var c = Row(ref y, x, w, "Power bar speed", out var value, "How fast the power bar fills while you hold to cast.");
            var e = Plugin.PowerFillSeconds;
            bool f = Focus(c);
            GUI.changed = false;
            float speed = Ui.Slider(c, BaseFillSeconds / Mathf.Max(0.05f, e.Value), 0.5f, 2f);
            int side = Side(f);
            if (side != 0) { speed = Mathf.Clamp(BaseFillSeconds / Mathf.Max(0.05f, e.Value) + side * 0.05f, 0.5f, 2f); GUI.changed = true; }
            if (GUI.changed)
            {
                speed = Mathf.Round(speed / 0.05f) * 0.05f;
                float secs = BaseFillSeconds / speed;
                if (Mathf.Abs(secs - e.Value) > 0.001f) { e.Value = secs; dirty = true; }
            }
            float shown = BaseFillSeconds / Mathf.Max(0.05f, e.Value);
            Ui.Label(value, (shown * 100f).ToString("0") + "%  <color=#9EB0BA>" + e.Value.ToString("0.00") + " s</color>", Ui.Style(15, true), Ui.Text);
        }

        static void Toggle(ref float y, float x, float w, ConfigEntry<bool> e, string name, string note, string help = null)
        {
            bool v = OnOff(ref y, x, w, name, e.Value, note, help);
            if (v != e.Value) { e.Value = v; dirty = true; }
        }

        static bool OnOff(ref float y, float x, float w, string name, bool on, string note, string help = null) =>
            Choice(ref y, x, w, name, on ? 1 : 0, note, help, "Off", "On") == 1;

        static int Choice(ref float y, float x, float w, string name, int index, string note, string help, params string[] options)
        {
            var c = Row(ref y, x, w, name, out var value, help);
            if (options[0].Length <= 3) c.width = S(150f);
            bool f = Focus(c);
            index = Ui.Segmented(c, index, options);
            // left / right step through the options; A goes to the next one and wraps, which flips an Off / On
            int side = Side(f);
            if (side != 0) index = Mathf.Clamp(index + side, 0, options.Length - 1);
            if (Pressed(f)) index = (index + 1) % options.Length;
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

        // tab 0 view, 1 fishing, 2 system
        static void ResetSettings(int which)
        {
            var entries = which == 0 ? new ConfigEntryBase[] { Plugin.MouseSensitivity, Plugin.FieldOfView, Plugin.Zoom1FieldOfView, Plugin.ZoomFieldOfView, Plugin.ZoomOrder, Plugin.HeadHeight, Plugin.AimDot, Plugin.StatsCorner, Plugin.HudLevel, Plugin.HudSeason, Plugin.HudBothSeasons, Plugin.HudCredits, Plugin.HudSpecies, Plugin.HudLifetime,
                Plugin.HudSession, Plugin.HudBiggest, Plugin.HudEarned, Plugin.TutorialPanel, Plugin.WalkSpeed }
                : which == 1 ? new ConfigEntryBase[] { Plugin.CastMode, Plugin.PowerFillSeconds, Plugin.CustomCatchCard, Plugin.RodLift, Plugin.RodFollowLook, Plugin.AllowOnlineCasting, Plugin.CatchScreenMotion }
                : new ConfigEntryBase[] { Plugin.SharpenScenery, Plugin.FrameCap, Plugin.CheckForUpdates };
            foreach (var e in entries) e.BoxedValue = e.DefaultValue;
            if (which == 0 && Driver.I != null) Driver.I.ShowHelp = true;
        }

        // under a two-column list: what else uses the hovered key or button
        static float ClashLine(float y, float x, float w)
        {
            if (clashNote != null) Ui.Label(new Rect(x, y + S(6f), w, S(20f)), clashNote, Ui.Style(13), Ui.Gold);
            clashNote = null;
            return y + S(30f);
        }

        // a label in the biggest size up to 15 that fits the width
        static GUIStyle Fit(string text, float width)
        {
            for (int size = 15; size > 11; size--)
                if (Ui.Width(text, Ui.Style(size)) <= width) return Ui.Style(size);
            return Ui.Style(11);
        }
    }
}

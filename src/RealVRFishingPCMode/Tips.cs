using UnityEngine;

namespace RealVRFishingPCMode
{
    // A three-step card the first time the game reaches the lodge: cast, reel and hook, settings.
    // It shows the controller's buttons if the controller was used last, and turns itself off once closed.
    static class Tips
    {
        public static bool Open;
        static bool shown, prevClose = true;
        static float showAt = -1f;

        public static void Tick(bool focused, bool inWorld)
        {
            if (!shown && Plugin.ShowTips.Value && inWorld)
            {
                // the lodge fades in for a few seconds after the rig appears
                if (showAt < 0f) showAt = Time.unscaledTime + 6f;
                else if (Time.unscaledTime >= showAt && !SettingsPanel.Open) { Open = shown = true; prevClose = true; }
            }
            if (!Open || SettingsPanel.Open) return;
            bool close = focused && (Win.Down(0x0D) || Win.Down(0x20) || Win.Down(Keys.Escape) || Pad.Down(Pad.Button.A) || Pad.Down(Pad.Button.B));
            if (close && !prevClose) Close();
            prevClose = close;
        }

        static void Close()
        {
            Open = false;
            Plugin.ShowTips.Value = false;
            try { Plugin.Cfg.Save(); }
            catch (System.Exception e) { Plugin.Log.LogError("saving settings: " + e.Message); }
        }

        static float S(float v) => Ui.S(v);

        // a bound action as a keycap or a controller button
        static string K(Keys.Bind b) => Pad.InUse && b.PadButton != Pad.Button.None ? Ui.PadKey(b.PadButton) : Ui.KeyMark + Ui.Key(b);

        public static void Draw()
        {
            Ui.Box(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.35f), 0f);
            string reel = K(Keys.Reel), hook = K(Keys.Hook);
            var steps = new[]
            {
                Plugin.PowerCast ? new[] { "Hold", reel, "and let go to cast. The bar sets how far." }
                                 : new[] { "Aim at the water and press", reel, "to cast there." },
                new[] { "Hold", reel, "to reel. When a fish bites, press", hook, "to set the hook." },
                new[] { Ui.KeyMark + "F10", Pad.InUse ? "on the keyboard opens settings and buttons," : "opens settings and keys,",
                    K(Keys.Help), "hides the controls card." },
            };
            string[] titles = { "Cast", "Reel and hook", "Make it yours" };

            float pad = S(32f), stepH = S(70f);
            float tw = 0f;
            foreach (var st in steps) tw = Mathf.Max(tw, Ui.Flow(0f, 0f, st, 26f, 15, true));
            float w = Mathf.Min(Screen.width - S(40f), Mathf.Max(S(560f), tw + S(48f) + 2f * pad));
            float h = pad + S(40f) + S(28f) + steps.Length * stepH + S(64f) + pad - S(8f);
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            Ui.Panel(r, Ui.Solid, 16f);

            float x = r.x + pad, y = r.y + pad;
            Ui.Label(new Rect(x, y, w, S(34f)), "Welcome to <color=#45C7DB>PC Mode</color>", Ui.Style(24, true), Ui.Text);
            y += S(38f);
            Ui.Label(new Rect(x, y, w, S(20f)), "Three things to know. The card at the top left lists every control.", Ui.Style(14), Ui.Dim);
            y += S(34f);
            for (int i = 0; i < steps.Length; i++)
            {
                var num = new Rect(x, y + S(2f), S(32f), S(32f));
                Ui.Box(num, Ui.Accent, S(16f));
                Ui.Label(num, (i + 1).ToString(), Ui.Style(16, true, TextAnchor.MiddleCenter), Ui.Dark);
                Ui.Label(new Rect(x + S(48f), y, w, S(20f)), titles[i], Ui.Style(16, true), Ui.Text);
                Ui.Flow(x + S(48f), y + S(26f), steps[i], 26f, 15);
                y += stepH;
            }

            var done = new Rect(r.xMax - pad - S(130f), r.yMax - pad - S(40f), S(130f), S(40f));
            string[] hint = { Pad.InUse ? Ui.PadKey(Pad.Button.A) : Ui.KeyMark + "Enter", "also closes this. It shows once." };
            Ui.Flow(x, done.y + S(7f), hint, 26f, 14);
            if (Ui.Button(done, "Got it", primary: true)) Close();
        }
    }
}

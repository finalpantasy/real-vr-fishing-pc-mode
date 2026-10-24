using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RealVRFishingPCMode
{
    // The stats corner at the top right while fishing: a strip of level and experience, season score, credits,
    // species and fish caught, then chips for this session's catches and earnings, each switched on or off in the
    // settings. Always up, only for a few seconds after something changes, or off; it
    // steps aside for the catch screen, photo mode and the menus, and stays away in the lobby and the lodge, where
    // the game's level isn't your fishing level yet.
    static class Hud
    {
        const float ChangeShow = 6f, FadeTime = 1f;
        static float nextRead, fishingTime, lastRead = -1f, changedAt = -100f;
        static bool ok;
        static int level, maxLevel, fresh, salt, credits, caught, species, speciesAll, lifetime;
        static long xpIn, xpNeed, xpPast;
        static float xp, bestGrams = -1f;
        static bool atSalt;
        static string bestName, bestWeight, signature;
        static object lastFish;
        // earned this session: what went up between reads, so spending in the shop doesn't count against it
        static long earnedCredits, earnedXp, earnedSeason, prevCredits = -1, prevXp = -1, prevSeason = -1;
        static readonly Color Green = new Color(0.56f, 0.78f, 0.24f);

        // a fish landed: count it and keep the heaviest (the game shows the board once per fish)
        [HarmonyPatch(typeof(ScoreController), "ScoreShow")]
        static class Landed
        {
            static void Postfix(ScoreController __instance, float fishLength, float fishWeight)
            {
                try
                {
                    var fish = Singleton<Main>.Instance.fishSpawnController.fish;
                    if (fish == null || ReferenceEquals(fish, lastFish)) return;
                    lastFish = fish;
                    caught++;
                    // the game's own weight for this fish, the one its board shows
                    float g = fishWeight > 0f ? fishWeight : CatchCard.Grams(fishLength);
                    if (g > bestGrams)
                    {
                        bestGrams = g;
                        // the board's name is in capitals; the corner writes it like its other labels
                        string name = __instance.textFishName != null ? __instance.textFishName.text.Trim() : "";
                        if (name.Length > 1 && name == name.ToUpperInvariant()) name = name.Substring(0, 1) + name.Substring(1).ToLowerInvariant();
                        bestName = name != "" ? name : "Biggest";
                        bestWeight = CatchCard.WeightOfGrams(g);
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning("stats corner: " + e.Message); }
            }
        }

        static void Read()
        {
            ok = false;
            var dm = Singleton<DataManager>.Instance;
            var main = Singleton<Main>.Instance;
            if (dm == null || dm.userData == null || main == null || main.fishingController == null || StageInfoManager.CurStageItem == null)
            {
                // off the water: start fresh, so arriving doesn't count as a change
                signature = null; lastRead = -1f;
                return;
            }
            var u = dm.userData;
            level = dm.GetLevel();
            maxLevel = Singleton<GM>.Instance != null ? Singleton<GM>.Instance.MaxLevel : 20;
            long exp = (long)dm.GetExp();
            xp = 1f; xpIn = xpNeed = xpPast = 0;
            try
            {
                // the game's own bar (GetExpBar) can rewrite your experience, so this only reads
                long lo = level <= 1 ? 0 : (long)dm.levelUpDatabase.GetExp(level - 2);
                if (level < maxLevel)
                {
                    long hi = (long)dm.levelUpDatabase.GetExp(level - 1);
                    xpIn = Math.Max(0, exp - lo); xpNeed = Math.Max(0, hi - lo);
                    xp = xpNeed > 0 ? Mathf.Clamp01((float)xpIn / xpNeed) : 0f;
                }
                else xpPast = Math.Max(0, exp - lo);
            }
            catch { xp = 0f; }
            credits = u.userGold;
            fresh = u.totalScoreFresh; salt = u.totalScoreSalt;
            atSalt = StageInfoManager.CurStageItem.stageType >= 2;
            lifetime = u.fishTotal;
            if (Plugin.HudSpecies.Value)
                try
                {
                    // the kinds you've caught, out of the fish book's: the fish that live in some region
                    species = speciesAll = 0;
                    var counts = u.fishes.fishCount;
                    var book = new HashSet<FishItem>();
                    foreach (var dlc in dm.itemDatabase.dlcs)
                        if (dlc != null && dlc.fishItems != null) book.UnionWith(dlc.fishItems);
                    foreach (var f in dm.itemDatabase.fishes)
                    {
                        if (f == null || !book.Contains(f)) continue;
                        speciesAll++;
                        if (f.ID >= 0 && f.ID < counts.Count && counts[f.ID] > 0) species++;
                    }
                }
                catch { speciesAll = 0; }

            Earn(ref earnedCredits, ref prevCredits, credits);
            Earn(ref earnedXp, ref prevXp, exp);
            Earn(ref earnedSeason, ref prevSeason, (long)fresh + salt);

            // the session clock runs only while out on the water
            float now = Time.unscaledTime;
            if (lastRead >= 0f) fishingTime += Mathf.Min(now - lastRead, 1f);
            lastRead = now;
            string sig = level + "|" + xpIn + "|" + xpPast + "|" + fresh + "|" + salt + "|" + credits + "|" + caught + "|" + species;
            if (signature != null && sig != signature) changedAt = now;
            signature = sig;
            ok = true;
        }

        static void Earn(ref long earned, ref long prev, long now)
        {
            if (prev >= 0 && now > prev) earned += now - prev;
            prev = now;
        }

        public static void Draw()
        {
            string mode = Plugin.StatsCorner.Value;
            if (mode == "Off" || !Plugin.DisableVR.Value) return;
            if (Time.unscaledTime >= nextRead)
            {
                nextRead = Time.unscaledTime + 0.5f;
                try { Read(); } catch (Exception e) { ok = false; Plugin.Log.LogWarning("stats corner: " + e.Message); nextRead += 10f; }
            }
            if (!ok) return;
            float a = 1f;
            if (mode == "On change")
            {
                float t = Time.unscaledTime - changedAt;
                a = 1f - Mathf.Clamp01((t - ChangeShow + FadeTime) / FadeTime);
                if (a <= 0f) return;
            }
            float old = Ui.Alpha;
            Ui.Alpha = a;
            try { DrawCard(); }
            finally { Ui.Alpha = old; }
        }

        // the catch screen's look: a dark tile of stat columns (caption over value), then a dark chip per line of
        // this session, all against the top right
        static readonly Color Tile = new Color(0.13f, 0.14f, 0.15f, 0.88f), Chip = new Color(0.07f, 0.08f, 0.09f, 0.88f),
            Yellow = new Color(0.98f, 0.78f, 0.18f), Hairline = new Color(1f, 1f, 1f, 0.12f);

        class Col { public string cap, val, suf = ""; public Color color = Ui.Text; public bool bar; public float w; }

        static void DrawCard()
        {
            var capSt = Ui.Style(10, true);
            var valSt = Ui.Style(17, true);
            var sufSt = Ui.Style(12, false);
            var chipSt = Ui.Style(13, true);
            float colPad = Ui.S(14f), vpad = Ui.S(9f), capH = Ui.S(13f), valH = Ui.S(21f), barH = Ui.S(8f), chipH = Ui.S(26f), gap = Ui.S(4f);

            // the columns
            bool top = level >= maxLevel;
            var cols = new List<Col>();
            if (Plugin.HudLevel.Value)
                cols.Add(new Col
                {
                    cap = "LEVEL " + level, bar = true, color = top ? Ui.Gold : Ui.Text,
                    // this level's experience out of what it takes; at the top, what you've earned since
                    val = top ? "Max" : xpIn.ToString("N0"),
                    suf = top ? "+" + xpPast.ToString("N0") + " XP" : xpNeed > 0 ? "/ " + xpNeed.ToString("N0") + " XP" : ""
                });
            if (Plugin.HudBothSeasons.Value)
            {
                cols.Add(new Col { cap = "FRESHWATER", val = fresh.ToString("N0") });
                cols.Add(new Col { cap = "SALTWATER", val = salt.ToString("N0") });
            }
            else if (Plugin.HudSeason.Value) cols.Add(new Col { cap = atSalt ? "SALTWATER" : "FRESHWATER", val = (atSalt ? salt : fresh).ToString("N0") });
            if (Plugin.HudCredits.Value) cols.Add(new Col { cap = "CREDITS", val = credits.ToString("N0") });
            if (Plugin.HudSpecies.Value && speciesAll > 0) cols.Add(new Col { cap = "SPECIES", val = species.ToString("N0"), suf = "of " + speciesAll.ToString("N0") });
            if (Plugin.HudLifetime.Value) cols.Add(new Col { cap = "FISH CAUGHT", val = lifetime.ToString("N0") });
            foreach (var c in cols)
            {
                float vw = Ui.Width(c.val, valSt) + (c.suf != "" ? Ui.S(3f) + Ui.Width(c.suf, sufSt) : 0f);
                c.w = Mathf.Max(Ui.Width(c.cap, capSt), vw, c.bar ? Ui.S(68f) : 0f) + 2f * colPad;
            }
            // into lines no wider than half the screen; a strip that must wrap splits into even lines, not a full
            // one and a stub
            var lines = new List<List<Col>>();
            float total = 0f;
            foreach (var c in cols) total += c.w;
            int perLine = Mathf.Max(1, Mathf.CeilToInt(cols.Count / Mathf.Ceil(total / (Screen.width * 0.5f))));
            float stripW = 0f, lineW = 0f;
            foreach (var c in cols)
            {
                if (lines.Count == 0 || lines[lines.Count - 1].Count >= perLine) { lines.Add(new List<Col>()); lineW = 0f; }
                lines[lines.Count - 1].Add(c);
                lineW += c.w;
                stripW = Mathf.Max(stripW, lineW);
            }

            // the session chips, as runs of text: a caption, then values with dots between
            var chips = new List<List<(string text, GUIStyle st, Color color)>>();
            if (Plugin.HudSession.Value)
            {
                int min = Mathf.FloorToInt(fishingTime / 60f);
                var s = new List<(string, GUIStyle, Color)>
                {
                    ("THIS SESSION", capSt, Ui.Dim), (caught == 1 ? "1 fish" : caught + " fish", chipSt, Ui.Text),
                    (min >= 60 ? $"{min / 60} h {min % 60} min" : min + " min", chipSt, Ui.Text)
                };
                if (Plugin.HudEarned.Value)
                {
                    s.Add(("+" + earnedCredits.ToString("N0") + " credits", chipSt, Yellow));
                    s.Add(("+" + earnedXp.ToString("N0") + " XP", chipSt, Green));
                    s.Add(("+" + earnedSeason.ToString("N0") + " season", chipSt, Ui.Text));
                }
                chips.Add(s);
                if (Plugin.HudBiggest.Value && bestWeight != null)
                    chips.Add(new List<(string, GUIStyle, Color)> { ("BIGGEST", capSt, Ui.Dim), (Fit(bestName, chipSt, Ui.S(260f)), chipSt, Ui.Text), (bestWeight, chipSt, Ui.Text) });
            }
            if (lines.Count == 0 && chips.Count == 0) return;

            float dotW = Ui.Width("·", chipSt), sep = Ui.S(6f);
            float w = stripW;
            foreach (var chip in chips)
            {
                float cw = Ui.S(20f) + Ui.S(8f);
                for (int i = 0; i < chip.Count; i++) cw += Ui.Width(chip[i].text, chip[i].st) + (i > 1 ? dotW + 2f * sep : 0f);
                w = Mathf.Max(w, cw);
            }
            float x0 = Screen.width - w - Ui.S(20f), y = Ui.S(20f);

            if (lines.Count > 0)
            {
                float h = vpad * 2f + (lines.Count - 1) * vpad * 2f;
                foreach (var line in lines) h += capH + valH + (line.Exists(c => c.bar) ? barH : 0f);
                Ui.Box(new Rect(x0, y, w, h), Tile, Ui.S(3f));
                float ly = y + vpad;
                for (int li = 0; li < lines.Count; li++)
                {
                    var line = lines[li];
                    float lh = capH + valH + (line.Exists(c => c.bar) ? barH : 0f);
                    if (li > 0) Ui.Box(new Rect(x0, ly - vpad, w, Mathf.Max(1f, Ui.Scale)), Hairline, 0f);
                    float cx = x0;
                    for (int ci = 0; ci < line.Count; ci++)
                    {
                        var c = line[ci];
                        if (ci > 0) Ui.Box(new Rect(cx, ly, Mathf.Max(1f, Ui.Scale), lh), Hairline, 0f);
                        float tx = cx + colPad, iw = c.w - 2f * colPad;
                        Color side = top && c.bar ? Ui.Gold : Ui.Dim;
                        Ui.Label(new Rect(tx, ly, iw, capH), c.cap, capSt, side);
                        float vw = Ui.Width(c.val, valSt);
                        Ui.Label(new Rect(tx, ly + capH, vw + Ui.S(2f), valH), c.val, valSt, c.color);
                        if (c.suf != "") Ui.Label(new Rect(tx + vw + Ui.S(3f), ly + capH + Ui.S(2f), iw, valH), c.suf, sufSt, side);
                        if (c.bar)
                        {
                            var bar = new Rect(tx, ly + capH + valH + Ui.S(4f), iw, Ui.S(3f));
                            Ui.Box(bar, Hairline, Ui.S(1.5f));
                            Ui.Box(new Rect(bar.x, bar.y, bar.width * xp, bar.height), top ? Ui.Gold : Green, Ui.S(1.5f));
                        }
                        cx += c.w;
                    }
                    ly += lh + vpad * 2f;
                }
                y += h + gap;
            }

            foreach (var chip in chips)
            {
                Ui.Box(new Rect(x0, y, w, chipH), Chip, Ui.S(3f));
                float cx = x0 + Ui.S(10f);
                for (int i = 0; i < chip.Count; i++)
                {
                    if (i > 1)
                    {
                        cx += sep;
                        Ui.Label(new Rect(cx, y, dotW + Ui.S(2f), chipH), "·", chipSt, Ui.Dim);
                        cx += dotW + sep;
                    }
                    float tw = Ui.Width(chip[i].text, chip[i].st);
                    Ui.Label(new Rect(cx, y, tw + Ui.S(2f), chipH), chip[i].text, chip[i].st, chip[i].color);
                    cx += tw + (i == 0 ? Ui.S(8f) : 0f);
                }
                y += chipH + gap;
            }
        }

        // a long name, cut short to fit
        static string Fit(string text, GUIStyle st, float room)
        {
            if (Ui.Width(text, st) <= room) return text;
            while (text.Length > 1 && Ui.Width(text + "…", st) > room) text = text.Substring(0, text.Length - 1);
            return text.TrimEnd() + "…";
        }
    }
}

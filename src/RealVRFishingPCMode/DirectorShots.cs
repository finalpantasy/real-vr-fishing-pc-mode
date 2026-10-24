#if TRAILER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RealVRFishingPCMode
{
    // the trailer's shot list
    partial class Director
    {
        const int LMB = 0x01, RMB = 0x02, ENTER = 0x0D;

        Main main;
        FishingController Fc => main != null ? main.fishingController : null;
        FISHINGSTATE State => Fc != null ? Fc.gameState : FISHINGSTATE.READY;
        bool FloatFishing { get { var dm = FindFirstObjectByType<DataManager>(); return dm != null && dm.userData.curType >= 2; } }

        IEnumerator Shots()
        {
            D.ShowHelp = false;
            yield return WaitUntil(() => SceneManager.GetActiveScene().name == "Lobby" && FindFirstObjectByType<DataManager>() != null, 240f, "lobby");
            Log("scene " + SceneManager.GetActiveScene().name);
            yield return Wait(4f);

            StartClip("01_lodge");
            float y = D.Yaw;
            // the F1 controls card, then turn to the room and walk in
            D.ShowHelp = true;
            yield return Look(y + 20f, 4f, 3f);
            D.ShowHelp = false;
            yield return Look(y - 40f, 6f, 2.5f);
            yield return Hold('W', 2.5f);
            yield return Look(y - 70f, 4f, 2f);
            StopClip();

            var stage = FindStage();
            if (stage == null) yield break;
            Log("loading " + stage.stageScene);
            staticLoading.LoadStage(stage);
            yield return WaitUntil(() => (main = FindFirstObjectByType<Main>()) != null && Fc != null && main.handController != null, 180f, "stage");
            yield return Wait(5f);
            Log("in stage, state " + State + ", float " + FloatFishing);

            StartClip("02_scenery");
            y = D.Yaw;
            D.Pitch = 2f;
            D.Yaw = y - 55f;
            yield return Look(y + 25f, -1f, 7f);
            yield return Look(y, 6f, 1.5f);
            StopClip();

            // Z zooms the view in on the far skyline
            StartClip("02b_zoom");
            yield return Look(y - 20f, 3f, 1f);
            yield return Hold('Z', 3f);
            yield return Wait(1f);
            yield return Look(y, 6f, 1f);
            StopClip();

            main.handController.MoveMainHandToRod();
            yield return Wait(2f);
            Traverse.Create(Fc).Property("DebugNotFail").SetValue(true);

            yield return CastAndCatch(0.8f, true);
            Log("finished, state " + State);
        }

        StageItem FindStage()
        {
            var dm = FindFirstObjectByType<DataManager>();
            var stages = dm.itemDatabase.GetStageItems_ByRegionAndPart(DlcType.Korea);
            Log("korea stages: " + string.Join(", ", stages.Select(s => s.stageScene).ToArray()));
            return stages.FirstOrDefault(s => s.stageScene.IndexOf("hanRiver", StringComparison.OrdinalIgnoreCase) >= 0) ?? stages.FirstOrDefault();
        }

        // the biggest, rarest fish this stage has that can still be held up in one hand
        FishItem PickTrophy()
        {
            var all = StageInfoManager.CurStageItem.AllStageFishes;
            Log("stage fish: " + string.Join(", ", all.Select(f => $"{f.ID}:{f.size}/{f.fishTier}").ToArray()));
            return all.Where(f => f.size != FishItem.FishSize.Huge && f.size != FishItem.FishSize.Monster)
                      .OrderByDescending(f => (int)f.size).ThenByDescending(f => (int)f.fishTier).FirstOrDefault();
        }

        IEnumerator CastAndCatch(float power, bool photo)
        {
            yield return WaitUntil(() => State == FISHINGSTATE.READY, 20f, "ready");

            StartClip("03_cast");
            // first click readies the cast, then hold to fill the bar and let go
            yield return Tap(LMB);
            yield return Wait(0.9f);
            yield return Hold(LMB, power * Plugin.PowerFillSeconds.Value);
            yield return WaitUntil(() => State == FISHINGSTATE.RETRIEVE, 10f, "lure in the water");
            yield return Wait(1.5f);
            StopClip();
            if (State != FISHINGSTATE.RETRIEVE) { Log("cast failed, state " + State); yield break; }

            StartClip("04_retrieve");
            var trophy = PickTrophy();
            bool attracted = false;
            bool fl = FloatFishing;
            for (float t = 0f; t < 40f && Fc.attractedFish == null && State == FISHINGSTATE.RETRIEVE; )
            {
                if (!attracted && t > 2f && trophy != null)
                {
                    StageFishAttractTool.AttractFish(trophy);
                    attracted = true;
                    Log("attracting " + trophy.ID);
                }
                if (fl) { yield return Wait(0.5f); t += 0.5f; continue; }
                // short reel, then a twitch
                yield return Hold(LMB, 0.5f);
                yield return Tap(RMB);
                yield return Wait(0.6f);
                t += 1.2f;
            }
            if (Fc.attractedFish == null) { Log("no bite, state " + State); StopClip(); yield break; }
            yield return Wait(0.4f);
            yield return Tap(RMB);
            yield return WaitUntil(() => State == FISHINGSTATE.FIGHTING, 10f, "fight");
            StopClip();

            StartClip("05_fight");
            yield return Fight(240f);
            StopClip();
            if (State != FISHINGSTATE.CATCHING) { Log("not landed, state " + State); yield break; }

            StartClip("06_catch");
            yield return WaitUntil(() => main.scoreController.isScoreVisible, 15f, "score board");
            yield return Wait(2.5f);
            if (photo) yield return PhotoShot();
            StopClip();
            Traverse.Create(main.scoreController).Method("ReleaseOrKeep", true).GetValue();
            yield return Wait(4f);
        }

        IEnumerator Fight(float timeout)
        {
            var bait = main.baitController;
            FISHMOVEMENT last = FISHMOVEMENT.STAY;
            for (float t = 0f; t < timeout; t += Time.deltaTime)
            {
                var s = State;
                if (s == FISHINGSTATE.CATCHING || s == FISHINGSTATE.FAILED || s == FISHINGSTATE.READY) break;
                if (s == FISHINGSTATE.LANDING)
                {
                    // flick the rod up to swing the fish in
                    Win.Virtual.Remove(LMB);
                    if (!bait.GetIsFishJump()) yield return Tap(RMB);
                    yield return Wait(0.4f);
                    continue;
                }
                var move = bait.fishMovement;
                if (move != last && (move == FISHMOVEMENT.JUMPL || move == FISHMOVEMENT.JUMPR))
                {
                    // a jump to the left wants the rod swung right, and the other way round
                    Win.Virtual.Remove(LMB);
                    yield return Wait(0.25f);
                    yield return Tap(move == FISHMOVEMENT.JUMPL ? 'D' : 'A');
                }
                last = move;
                // reel while the line has room, ease off near the limit
                float limit = 50f - Fc.tensionStress;
                if (Fc.lineTension < limit - 8f) Win.Virtual.Add(LMB);
                else if (Fc.lineTension > limit - 4f) Win.Virtual.Remove(LMB);
                yield return null;
            }
            Win.Virtual.Remove(LMB);
            Log("fight over, state " + State);
        }

        IEnumerator PhotoShot()
        {
            yield return Tap('P');
            yield return Wait(1.2f);
            yield return Drag(new Vector2(500f, 0f), 2.5f);
            yield return Drag(new Vector2(0f, -120f), 1f);
            yield return Tap('H');
            yield return Wait(1f);
            yield return Tap('B');
            yield return Wait(2f);
            float y = D.Yaw;
            yield return Look(y + 160f, 4f, 4f);
            yield return Hold('E', 0.8f);
            yield return Wait(1.5f);
            yield return Drag(new Vector2(-400f, 0f), 2f);
            yield return Tap(ENTER);
            yield return Wait(1.5f);
            yield return Tap('P');
            yield return Wait(1f);
        }
    }

    // trailer runs never save and never report a catch anywhere: no save file, leaderboards, analytics or Steam stats
    [HarmonyPatch]
    static class TrailerOfflinePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new (string type, string[] methods)[]
            {
                ("DataSaver", new[] { "OfflineSave", "SaveAsync" }),
                ("DUGPlatformService", new[] { "WriteEntry", "WriteEntryAsync", "WriteEventEntry" }),
                ("RVRFAnalytics", new[] { "AddUserEvent" }),
                ("AchievementController", new[] { "Upgrade", "CheckUpgrade" }),
            };
            foreach (var (typeName, names) in targets)
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) { Plugin.Log.LogWarning("trailer: no type " + typeName); continue; }
                foreach (var m in AccessTools.GetDeclaredMethods(type))
                    if (names.Contains(m.Name) && !m.IsAbstract && !m.IsGenericMethodDefinition) yield return m;
            }
        }

        static bool Prefix(MethodBase __originalMethod)
        {
            Plugin.Log.LogDebug("trailer: blocked " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
            return false;
        }
    }
}
#endif

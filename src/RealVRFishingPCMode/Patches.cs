using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using XRInputUtility;

namespace RealVRFishingPCMode
{
    // with no headset runtime the Meta rig would zero or never set its anchors; the driver owns them
    [HarmonyPatch(typeof(OVRCameraRig), "UpdateAnchors")]
    static class RigAnchorsPatch
    {
        static bool Prefix()
        {
            if (Driver.I == null) return true;
            Driver.I.ApplyPoses();
            return false;
        }
    }

    [HarmonyPatch(typeof(RvrfCameraRig), "UpdateAnchors")]
    static class RvrfRigAnchorsPatch
    {
        static bool Prefix()
        {
            if (Driver.I == null) return true;
            Driver.I.ApplyPoses();
            return false;
        }
    }

    // menus: the main hand's laser starts at the camera and goes through the crosshair or cursor
    [HarmonyPatch(typeof(VRPointerInputModule), "GetPointerData", new[] { typeof(HAND_SIDE) })]
    static class PointerRayPatch
    {
        static void Prefix(VRPointerInputModule __instance, HAND_SIDE side)
        {
            var d = Driver.I;
            if (d == null || !d.HasAim || side != d.MainSide) return;
            var pointer = __instance.GetPointer(side);
            var t = pointer?.RayStartTransform;
            if (t == null) return;
            t.position = d.AimRay.origin + d.AimRay.direction * 0.05f;
            t.rotation = Quaternion.LookRotation(d.AimRay.direction);
        }
    }

    // right-click during a bite counts as the upward rod flick that sets the hook
    // and A / D count as the sideways swings a jumping fish asks for (2 = left, 3 = right)
    [HarmonyPatch(typeof(FishingController), nameof(FishingController.GetSwingState))]
    static class SwingPatch
    {
        const float Window = 0.35f;

        static void Postfix(int direction, ref SWINGRESULT __result)
        {
            var d = Driver.I;
            if (d == null) return;
            float now = Time.time;
            switch (direction)
            {
                case 0:
                    if (now - d.HookPressedAt < Window) __result = SWINGRESULT.SUCCEED;
                    break;
                case 2:
                    if (now - d.SwingLeftAt < Window) __result = SWINGRESULT.SUCCEED;
                    else if (now - d.SwingRightAt < Window) __result = SWINGRESULT.FAILED;
                    break;
                case 3:
                    if (now - d.SwingRightAt < Window) __result = SWINGRESULT.SUCCEED;
                    else if (now - d.SwingLeftAt < Window) __result = SWINGRESULT.FAILED;
                    break;
            }
        }
    }

    // PowerBar: hold left-click to wind the rod back while a bar fills, release to whip it forward and cast
    // that share of max range toward the crosshair. ClickSpot: click plays the whole swing and lands on the spot.
    [HarmonyPatch(typeof(FishingController), "CheckCasting")]
    static class CastPatch
    {
        const float ClickReleaseDelay = 0.26f, PowerReleaseDelay = 0.1f, MinCast = 2f;
        static bool pending;
        static Ray pendingRay;
        static float pendingAt, pendingDelay, pendingPower;

        static bool Prefix(FishingController __instance)
        {
            var d = Driver.I;
            if (d == null || !Plugin.ClickToCast.Value) return true;
            if (IsOnline() && !Plugin.AllowOnlineCasting.Value) return true;
            float now = Time.time;

            if (pending)
            {
                if (now - pendingAt < pendingDelay) return false;
                pending = false;
                try { Cast(__instance, pendingRay, pendingPower); }
                catch (Exception e) { Plugin.Log.LogError("cast failed: " + e); }
                return false;
            }

            var trigger = InputUtility.Controllers[d.MainSide].Trigger;
            if (d.Charging)
            {
                d.ChargeSeenAt = now;
                d.CastPower = Mathf.PingPong((now - chargeStartedAt) / Mathf.Max(0.2f, Plugin.PowerFillSeconds.Value), 1f);
                d.CastReleaseLift = d.CastPower * 45f;
                if (d.HookPressedAt > chargeStartedAt)
                {
                    // right-click while winding up: no cast, the rod drops back and left-click must be pressed again
                    d.Charging = false;
                    d.CastCancelled = true;
                    d.CastAnimAt = now;
                    return false;
                }
                if (trigger.Press) return false;
                d.Charging = false;
                Queue(d, d.CastPower, PowerReleaseDelay);
                return false;
            }

            if (!d.HasAim || !trigger.Click || !__instance.isCastingReady || __instance.isHolder) return false;
            var media = UnityEngine.Object.FindFirstObjectByType<MediaController>();
            if (media != null && media.IsEnterPointer()) return false;
            if (Plugin.PowerCast)
            {
                chargeStartedAt = now;
                d.Charging = true;
                d.ChargeSeenAt = now;
                d.CastPower = 0f;
            }
            else
            {
                d.CastReleaseLift = 0f;
                Queue(d, -1f, ClickReleaseDelay);
            }
            return false;
        }

        static float chargeStartedAt;

        static void Queue(Driver d, float power, float delay)
        {
            pending = true;
            pendingRay = d.AimRay;
            pendingPower = power;
            pendingAt = Time.time;
            pendingDelay = delay;
            d.CastCancelled = false;
            d.CastAnimAt = Time.time;
        }

        static bool IsOnline()
        {
            try { return PhotonHelper.IsMultiplay; } catch { return false; }
        }

        public struct Plan
        {
            public Vector3 Start, Target;
            public float Distance, MaxDistance, Speed;
            public Rigidbody Bait;
        }

        // power < 0 aims at the spot the ray hits on the water; 0..1 picks that share of max range in the ray's direction
        public static bool TryPlan(FishingController fc, Main main, Ray ray, float power, out Plan plan)
        {
            plan = default;
            if (fc == null || main == null || main.envController == null || main.tackleController == null) return false;
            plan.Bait = Traverse.Create(fc).Field("baitControllerRigidbody").GetValue<Rigidbody>();
            plan.Start = plan.Bait != null ? plan.Bait.position : ray.origin;
            float waterY = main.envController.waterColliderHeight;
            if (ray.direction.y < -0.01f) plan.Target = ray.GetPoint((waterY - ray.origin.y) / ray.direction.y);
            else plan.Target = ray.origin + new Vector3(ray.direction.x, 0f, ray.direction.z).normalized * 1000f;
            var flat = plan.Target - plan.Start; flat.y = 0f;
            plan.Distance = flat.magnitude;
            float maxSpeed = 10f + main.tackleController.GetRodDistance();
            float height = Mathf.Max(0f, plan.Start.y - waterY);
            plan.MaxDistance = Range(maxSpeed, height, Time.fixedDeltaTime);
            if (power >= 0f)
            {
                var dir = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
                plan.Distance = Mathf.Lerp(MinCast, plan.MaxDistance, power);
                plan.Target = new Vector3(plan.Start.x, waterY, plan.Start.z) + dir * plan.Distance;
            }
            plan.Speed = plan.Distance >= plan.MaxDistance ? maxSpeed : SolveSpeed(plan.Distance, height, Time.fixedDeltaTime, maxSpeed);
            return plan.Distance >= 0.5f;
        }

        static void Cast(FishingController fc, Ray ray, float power)
        {
            var main = UnityEngine.Object.FindFirstObjectByType<Main>();
            if (fc.gameState != FISHINGSTATE.READY || !TryPlan(fc, main, ray, power, out var plan)) return;
            var flat = plan.Target - plan.Start; flat.y = 0f;
            var dir = flat.normalized;

            var reel = main.tackleController.GetReel();
            if (reel != null) reel.OpenBail();
            var tr = Traverse.Create(fc);
            fc.castingSpeed = plan.Speed;
            tr.Field("castingVector").SetValue((dir + new Vector3(0f, 0.3f, 0f)) * plan.Speed);
            tr.Field("castingTime").SetValue(0f);
            fc.isCastingReady = false;
            fc.gameState = FISHINGSTATE.CASTING;
            MultiplayManager.SetLocalPlayerFishingState(fc.gameState);
            Physics.IgnoreLayerCollision(LayerMask.NameToLayer("bait"), LayerMask.NameToLayer("ground"), ignore: false);
            var dm = UnityEngine.Object.FindFirstObjectByType<DataManager>();
            if (dm != null && dm.userData.gameDifficulty == 1 && main.fishFinderUIController != null)
                main.fishFinderUIController.ShowRadar(show: false);
            int type = dm != null ? dm.userData.curType : 0;
            if (main.useTutorial) type = TutorialType(type);
            if (type < 2 && plan.Bait != null) plan.Bait.isKinematic = true;
            Plugin.Log.LogInfo($"cast to {plan.Target} distance {plan.Distance:F1} m of max {plan.MaxDistance:F1} m, speed {plan.Speed:F2}");
        }

        static FishingController cachedFc;
        static Main cachedMain;
        static float nextFind;

        // ClickSpot only: text under the crosshair while the lure is ready, how far the cast will go in feet.
        // The power bar shows no distance; judging it is part of the game.
        public static string Readout(Driver d)
        {
            if (!Plugin.ClickToCast.Value || Plugin.PowerCast || !d.HasAim) return null;
            if (Time.unscaledTime >= nextFind || cachedFc == null)
            {
                nextFind = Time.unscaledTime + 1f;
                cachedFc = UnityEngine.Object.FindFirstObjectByType<FishingController>();
                cachedMain = UnityEngine.Object.FindFirstObjectByType<Main>();
            }
            var fc = cachedFc;
            if (fc == null || fc.gameState != FISHINGSTATE.READY || !fc.isCastingReady || fc.isHolder) return null;
            if (IsOnline() && !Plugin.AllowOnlineCasting.Value) return null;
            try
            {
                if (!TryPlan(fc, cachedMain, d.AimRay, -1f, out var plan)) return null;
                int feet = Mathf.RoundToInt(Mathf.Min(plan.Distance, plan.MaxDistance) * 3.28084f);
                return plan.Distance >= plan.MaxDistance ? $"<color=#ffb347>cast {feet} ft (max)</color>" : $"cast {feet} ft";
            }
            catch { return null; }
        }

        static int TutorialType(int fallback)
        {
            try { return Traverse.Create(typeof(TutorialController)).Property("FishingType").GetValue<int>(); }
            catch { }
            try { return Traverse.Create(typeof(TutorialController)).Field("FishingType").GetValue<int>(); }
            catch { }
            return fallback;
        }

        // the game moves the lure with its own steps (gravity grows with flight time), so match them exactly
        static float Range(float speed, float height, float dt)
        {
            float t = 0f, vy = 0.3f * speed, y = height, x = 0f;
            for (int i = 0; i < 5000; i++)
            {
                t += dt;
                vy += -9.8f * t * dt;
                y += vy * dt;
                x += speed * dt;
                if (y <= 0f) return x;
            }
            return x;
        }

        static float SolveSpeed(float distance, float height, float dt, float maxSpeed)
        {
            float lo = 0.3f, hi = maxSpeed;
            for (int i = 0; i < 40; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Range(mid, height, dt) < distance) lo = mid; else hi = mid;
            }
            return (lo + hi) * 0.5f;
        }
    }
}

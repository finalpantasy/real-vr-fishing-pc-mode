using System.Collections.Generic;
using HarmonyLib;
using Obi;
using UnityEngine;

namespace RealVRFishingPCMode
{
    // The game's float rig sometimes blows up its physics right after the line is reeled in: the float
    // and bait fly off kilometers away and the line whips around the sky. The game notices (its log says
    // RVRFLIVE-731) and resets the rope, but leaves the float and bait where they are, so the line is
    // pulled out again every frame. This puts the tackle and the line back at the rod tip, at rest.
    [HarmonyPatch(typeof(FishingController), "Update")]
    static class TackleRescue
    {
        const float Limit = 300f;
        static float lastRescue = -10f;

        static void Postfix()
        {
            try
            {
                var main = Singleton<Main>.Instance;
                var rc = main != null ? main.ropeController : null;
                if (rc == null || rc.topGuide == null) return;
                var tip = rc.topGuide.position;
                var bodies = Bodies(main, rc);
#if RESCUETEST
                // test builds only: F11 throws the tackle far away the way the physics blow-up does
                if (Win.Down(0x7A))
                    foreach (var t in bodies)
                    {
                        var rb = t.GetComponent<Rigidbody>();
                        if (rb != null && !rb.isKinematic) rb.linearVelocity = new Vector3(3e5f, 2e5f, -4e5f);
                        else t.position = tip + new Vector3(1e6f, 5e5f, 0f);
                    }
#endif
#if RESCUETEST
                Probe(main, rc, tip);
#endif
                bool lost = false;
                foreach (var t in bodies) if (Lost(t.position, tip)) { lost = true; break; }
                if (!lost || Time.unscaledTime - lastRescue < 1f) return;
                lastRescue = Time.unscaledTime;

                foreach (var t in bodies)
                {
                    var rb = t.GetComponent<Rigidbody>();
                    t.position = tip;
                    if (rb == null) continue;
                    rb.position = tip;
                    if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                }
                Settle(rc.mainRope, tip);
                Settle(rc.baitRope, tip);
                Plugin.Log.LogInfo($"tackle physics blew up ({main.fishingController.gameState}); put the float, bait and line back at the rod tip");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("tackle rescue: " + e.Message); }
        }

#if RESCUETEST
        // test builds only: log the float rig frame by frame for 2 s after each reel-in
        static FISHINGSTATE lastState;
        static float probeUntil;
        static void Probe(Main main, RopeController rc, Vector3 tip)
        {
            var fc = main.fishingController;
            if (fc.gameState == FISHINGSTATE.READY && lastState != FISHINGSTATE.READY) probeUntil = Time.time + 2f;
            lastState = fc.gameState;
            if (Time.time > probeUntil) return;
            var sb = new System.Text.StringBuilder($"probe t={Time.time:F3} dt={Time.deltaTime:F3} tip={tip:F2} main={rc.mainRope?.restLength:F2} baitLine={rc.baitRope?.restLength:F2}");
            void Body(string name, GameObject g)
            {
                if (g == null) return;
                var rb = g.GetComponent<Rigidbody>();
                sb.Append($" | {name} {g.transform.position:F2}");
                if (rb != null) sb.Append($" v={rb.linearVelocity.magnitude:F1} kin={rb.isKinematic} m={rb.mass:F2}");
                foreach (var c in Physics.OverlapSphere(g.transform.position, 0.15f, ~0, QueryTriggerInteraction.Ignore))
                    if (!c.transform.IsChildOf(g.transform)) sb.Append($" touch {c.name}[{LayerMask.LayerToName(c.gameObject.layer)}]");
            }
            Body("bobberCtl", rc.bobberController);
            Body("bobber", rc.bobber);
            Body("baitCtl", rc.baitController);
            Body("bait", rc.bait);
            Plugin.Log.LogInfo(sb.ToString());
        }
#endif

        static bool Lost(Vector3 p, Vector3 tip)
        {
            float d = (p - tip).sqrMagnitude;
            return float.IsNaN(d) || float.IsInfinity(d) || d > Limit * Limit;
        }

        static List<Transform> Bodies(Main main, RopeController rc)
        {
            var list = new List<Transform>();
            void Add(Component c) { if (c != null && !list.Contains(c.transform)) list.Add(c.transform); }
            void AddGo(GameObject g) { if (g != null) Add(g.transform); }
            AddGo(rc.bobberController);
            AddGo(rc.baitController);
            AddGo(rc.bobber);
            AddGo(rc.bait);
            Add(main.bobberController);
            Add(main.baitController);
            if (main.baitController != null) AddGo(main.baitController.joint);
            if (main.tackleController != null) { Add(main.tackleController.CurrentBobber); Add(main.tackleController.CurrentBait); }
            return list;
        }

        // the same particle reset the game's own ForceResetRope does, minus clearing the pins that hold the line to the tackle
        static void Settle(ObiRope rope, Vector3 tip)
        {
            if (rope == null || !rope.isLoaded || rope.solver == null) return;
            var at = new Vector4(tip.x, tip.y, tip.z, 1f);
            for (int i = 0; i < rope.activeParticleCount; i++)
            {
                int index = rope.solverIndices[i];
                rope.solver.positions[index] = at;
                rope.solver.velocities[index] = Vector4.zero;
            }
        }
    }
}

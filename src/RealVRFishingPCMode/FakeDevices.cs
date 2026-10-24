using UnityEngine;
using XRInputUtility;

namespace RealVRFishingPCMode
{
    // Stands in for a VR controller: the driver writes the wanted button state, Update() turns it into
    // the game's press/click/release state once per frame.
    public class FakeController : ControllerState
    {
        public bool Trigger, Grip, Primary, Secondary, Menu, StickClick;
        public float TriggerAmount = 1f;
        public Vector2 Stick;

        public FakeController(HAND_SIDE side) : base(side) { }

        public override void Update()
        {
            m_Trigger.Update(Trigger);
            m_Grip.Update(Grip);
            m_Primary.Update(Primary);
            m_Secondary.Update(Secondary);
            m_Menu.Update(Menu);
            m_Thumbstick.Update(StickClick);
            m_ThumbstickState.Update(Stick);
            m_TriggerValue = Trigger ? TriggerAmount : 0f;
            m_GripValue = Grip ? 1f : 0f;
            m_PrimaryTouch = Primary;
            m_SecondaryTouch = Secondary;
            m_ThumbstickTouch = Stick.sqrMagnitude > 0.01f;
            m_IsTrackedBatteryPercent = false;
            m_BatteryPercent = 100;
        }
    }

    public class FakeHaptics : HapticDevice
    {
        public FakeHaptics(HAND_SIDE side) : base(side) { }
        // every VR controller buzz in the game comes through here; Pad picks its own moments to rumble instead
        public override void SendHapticImpulse(float frequency, float amplitude, float duration) { }
    }

    // Head and hand poses for code that reads InputUtility.TrackingState, with velocity and
    // acceleration worked out from frame-to-frame motion.
    public class FakeTracking : InputTrackingState
    {
        public Pose Head = Pose.identity, Left = Pose.identity, Right = Pose.identity;
        bool started;

        public override void Update(float delta)
        {
            if (!started || !IsValidDelta(delta))
            {
                m_LocalHead = Head; m_LocalLeft = Left; m_LocalRight = Right;
                m_HeadVelocity = m_LeftVelocity = m_RightVelocity = default;
                m_HeadAcceleration = m_LeftAcceleration = m_RightAcceleration = default;
                m_IsHeadTracked = m_IsLeftTracked = m_IsRightTracked = true;
                started = true;
                return;
            }
            Step(ref m_LocalHead, ref m_HeadVelocity, ref m_HeadAcceleration, Head, delta);
            Step(ref m_LocalLeft, ref m_LeftVelocity, ref m_LeftAcceleration, Left, delta);
            Step(ref m_LocalRight, ref m_RightVelocity, ref m_RightAcceleration, Right, delta);
        }

        static void Step(ref Pose current, ref PoseVelocity velocity, ref PoseAcceleration acceleration, Pose next, float delta)
        {
            PoseVelocity v = Sanitize(GetVelocity(in current, in next, delta));
            acceleration = Sanitize(GetAcceleration(in velocity, in v, delta));
            velocity = v;
            current = next;
        }
    }
}

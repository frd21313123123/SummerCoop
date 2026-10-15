using System.Reflection;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // Keep the game's walking, collision and jump code. MSC's CharacterMotor
    // already implements moving supports, including rotation and jump velocity.
    public static class MscPlatforms
    {
        static Transform player;
        static Behaviour motor;
        static object platform;
        static FieldInfo enabled, grounded, active, hit, velocity;
        static bool originalEnabled, changed;
        public static Rigidbody SupportBody { get; private set; }
        public static string NativeState { get; private set; }

        public static void Reset()
        {
            Restore(); player = null; motor = null; platform = null;
            enabled = grounded = active = hit = velocity = null; SupportBody = null;
        }
        static void Restore()
        {
            if (changed && motor != null && enabled != null && platform != null) enabled.SetValue(platform, originalEnabled);
            changed = false; SupportBody = null;
        }
        public static void Update()
        {
            SupportBody = null;
            if (!Session.Active || !PlayerSync.InGame) { Restore(); return; }
            Transform current = Game.PlayerT;
            if (current == null) return;
            if (player != current || motor == null)
            {
                Reset(); player = current; motor = player.GetComponent("CharacterMotor") as Behaviour;
                if (motor == null) return;
                FieldInfo field = motor.GetType().GetField("movingPlatform");
                platform = field != null ? field.GetValue(motor) : null;
                if (platform == null) return;
                enabled = platform.GetType().GetField("enabled");
                active = platform.GetType().GetField("activePlatform");
                hit = platform.GetType().GetField("hitPlatform");
                velocity = platform.GetType().GetField("platformVelocity");
                grounded = motor.GetType().GetField("grounded");
                if (enabled == null || active == null || grounded == null) return;
                originalEnabled = (bool)enabled.GetValue(platform);
                FieldInfo fixedUpdate = motor.GetType().GetField("useFixedUpdate");
                FieldInfo transfer = platform.GetType().GetField("movementTransfer");
                NativeState = "enabled=" + originalEnabled + ";fixed=" + (fixedUpdate != null ? fixedUpdate.GetValue(motor).ToString() : "?")
                    + ";jumpTransfer=" + (transfer != null ? transfer.GetValue(platform).ToString() : "?");
                Log.Info("MSC moving supports: " + NativeState);
            }
            if (enabled == null || platform == null) return;
            // Bus already carries its guest explicitly and disables the native
            // platform while doing so. Never add a second copy of that motion.
            CharacterController controller = player.GetComponent<CharacterController>();
            if (Bus.CarriesLocalPlayer) { ClearContact(); return; }
            bool walking = motor.enabled && controller != null && controller.enabled && player.parent == null
                && !Seats.Seated && VehicleSync.LocalDriving < 0 && !Respawn.Choosing && !Game.GlobalBool("PlayerSleeps");
            if ((bool)enabled.GetValue(platform) != walking) enabled.SetValue(platform, walking);
            changed = true;
            if (!walking) { ClearContact(); return; }
            if (!(bool)grounded.GetValue(motor)) return;
            Transform contact = active.GetValue(platform) as Transform;
            Transform car = contact != null ? VehicleSync.CarRoot(contact) : null;
            if (car != null) SupportBody = car.GetComponent<Rigidbody>();
        }

        static void ClearContact()
        {
            // A seated player is already parented to the vehicle. Retaining
            // native support there doubles its movement and leaves old platform
            // velocity to be applied when the player gets back up.
            if (active != null) active.SetValue(platform, null);
            if (hit != null) hit.SetValue(platform, null);
            if (velocity != null) velocity.SetValue(platform, Vector3.zero);
        }

        public static SupportPose Capture(Vector3 feet, Vector3 head, float yaw)
        {
            Rigidbody body = SupportBody;
            int index = body != null ? VehicleSync.CarIndex(body) : -1;
            if (index < 0 || index >= 255) return new SupportPose();
            // Summon/respawn can move the player between the motor's contact
            // update and capture. Do not publish the old vehicle after a warp.
            if ((feet - body.position).sqrMagnitude > 900f || (head - body.position).sqrMagnitude > 900f) return new SupportPose();
            Transform t = body.transform;
            return new SupportPose { Vehicle = index + 1, Feet = t.InverseTransformPoint(feet), Head = t.InverseTransformPoint(head), Yaw = Mathf.DeltaAngle(t.eulerAngles.y, yaw) };
        }
    }
}

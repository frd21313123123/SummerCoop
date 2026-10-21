using System.Collections.Generic;
using SummerCoop.Game;
using UnityEngine;

namespace MWCoop
{
    // Only rigid installed parts of a remote Satsuma. Doors/hoods keep their
    // native hinge simulation and CarDoors ownership; cargo stays with Props.
    internal sealed class MscVehicleReplica
    {
        sealed class Part
        {
            public Rigidbody Body;
            public Transform Parent;
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Kinematic;
            public RigidbodyInterpolation Interpolation;
        }
        readonly Rigidbody car;
        readonly List<Part> parts = new List<Part>();
        Dictionary<Rigidbody, RigidbodyInterpolation> interpolation;
        bool active, dirty;
        internal int Count { get { return parts.Count; } }
        internal MscVehicleReplica(Rigidbody body) { car = body; }
        internal void Invalidate() { dirty = true; }
        internal void Begin(Dictionary<Rigidbody, RigidbodyInterpolation> original)
        {
            interpolation = original; active = dirty = true;
            Refresh();
        }
        bool Mounted(Rigidbody body)
        {
            if (body == null || body == car || !body.gameObject.activeInHierarchy || !body.transform.IsChildOf(car.transform)
                || MscVehicleControls.PlayerObject(body.transform) || MscBoltPositions.IsFastenerBody(body)
                || body.GetComponent<Wheel>() != null || !NativeAssembly.Installed(body.gameObject)) return false;
            // Removal/assembly may briefly replace a hinge with a fixed latch.
            // CarDoors owns these bodies even while that hinge is absent.
            foreach (PlayMakerFSM f in body.GetComponents<PlayMakerFSM>())
                if (CarDoors.Owns(f)) return false;
            foreach (Joint joint in body.GetComponents<Joint>())
                if (!(joint is FixedJoint) || joint.connectedBody == null
                    || joint.connectedBody != car && !joint.connectedBody.transform.IsChildOf(car.transform)) return false;
            return true;
        }
        void Refresh()
        {
            dirty = false;
            for (int i = parts.Count - 1; i >= 0; i--)
                if (!Mounted(parts[i].Body) || parts[i].Body.transform.parent != parts[i].Parent)
                { Restore(parts[i], car.velocity, car.angularVelocity); parts.RemoveAt(i); }
            foreach (Rigidbody body in car.GetComponentsInChildren<Rigidbody>(true))
            {
                if (!Mounted(body)) continue;
                bool known = false;
                foreach (Part part in parts) if (part.Body == body) { known = true; break; }
                if (known) continue;
                RigidbodyInterpolation original;
                if (interpolation == null || !interpolation.TryGetValue(body, out original)) original = body.interpolation;
                parts.Add(new Part { Body = body, Parent = body.transform.parent,
                    Position = body.transform.localPosition, Rotation = body.transform.localRotation,
                    Kinematic = body.isKinematic, Interpolation = original });
                body.isKinematic = true; body.interpolation = RigidbodyInterpolation.None;
            }
        }
        internal void Apply()
        {
            if (!active || car == null) return;
            if (dirty) Refresh();
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                Part part = parts[i];
                if (part.Body == null || part.Body.transform.parent != part.Parent)
                { Restore(part, car.velocity, car.angularVelocity); parts.RemoveAt(i); dirty = true; continue; }
                // Native assembly/LOD can finish after discovery and restore
                // dynamics. Remember its intended state, then keep this remote
                // rigid part on the same presentation clock as the chassis.
                if (!part.Body.isKinematic) { part.Kinematic = false; part.Body.isKinematic = true; }
                if (part.Body.interpolation != RigidbodyInterpolation.None)
                { part.Interpolation = part.Body.interpolation; part.Body.interpolation = RigidbodyInterpolation.None; }
                Transform t = part.Body.transform;
                t.localPosition = part.Position; t.localRotation = part.Rotation;
                part.Body.position = t.position; part.Body.rotation = t.rotation;
            }
        }
        void Restore(Part part, Vector3 velocity, Vector3 angular)
        {
            if (part.Body == null) return;
            part.Body.isKinematic = part.Kinematic; part.Body.interpolation = part.Interpolation;
            if (interpolation != null) interpolation.Remove(part.Body);
            if (!part.Kinematic && car != null)
            {
                part.Body.velocity = velocity + Vector3.Cross(angular, part.Body.worldCenterOfMass - car.worldCenterOfMass);
                part.Body.angularVelocity = angular;
            }
        }
        internal void Removing(GameObject removed, Vector3 velocity, Vector3 angular)
        {
            if (!active || removed == null) return;
            for (int i = parts.Count - 1; i >= 0; i--)
                if (parts[i].Body != null && (parts[i].Body.gameObject == removed || parts[i].Body.transform.IsChildOf(removed.transform)))
                { Restore(parts[i], velocity, angular); parts.RemoveAt(i); }
            dirty = true;
        }
        internal void End(Vector3 velocity, Vector3 angular)
        {
            foreach (Part part in parts) Restore(part, velocity, angular);
            parts.Clear(); active = dirty = false; interpolation = null;
        }
    }
}

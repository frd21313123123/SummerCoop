using System;
using System.Collections.Generic;
using UnityEngine;

namespace MWCoop
{
    // Preserve colliders for picking and assembly triggers, but remove their
    // physical response only while a local or remote player carries the dash.
    internal static class MscHeldDashboard
    {
        sealed class Saved { public Collider Collider; public bool Trigger; }
        static readonly Dictionary<GameObject, List<Saved>> held = new Dictionary<GameObject, List<Saved>>();
        static readonly List<GameObject> completed = new List<GameObject>();
        static GameObject local;
        internal static void Sweep()
        {
            // Native installation removes the Rigidbody before Props can
            // release its remote interpolation. The colliders themselves live on.
            completed.Clear();
            foreach (var entry in held) if (entry.Key == null || entry.Key.GetComponent<Rigidbody>() == null) completed.Add(entry.Key);
            foreach (GameObject go in completed)
            {
                foreach (Saved item in held[go]) if (item.Collider != null) item.Collider.isTrigger = item.Trigger;
                held.Remove(go);
            }
        }
        internal static void Local(GameObject go)
        {
            if (local != go) { Set(local, false); local = go; }
            Set(local, true);
        }
        internal static void Set(GameObject go, bool carrying)
        {
            if (go == null) return;
            List<Saved> saved;
            if (!carrying)
            {
                if (!held.TryGetValue(go, out saved)) return;
                foreach (Saved item in saved) if (item.Collider != null) item.Collider.isTrigger = item.Trigger;
                held.Remove(go); return;
            }
            if (!go.name.StartsWith("dashboard", StringComparison.OrdinalIgnoreCase)) return;
            if (!held.TryGetValue(go, out saved))
            {
                saved = new List<Saved>();
                Rigidbody body = go.GetComponent<Rigidbody>();
                if (body == null) return;
                foreach (Collider collider in go.GetComponentsInChildren<Collider>(true))
                {
                    // Independent fitted parts keep their own physics.
                    if (collider.attachedRigidbody != body) continue;
                    saved.Add(new Saved { Collider = collider, Trigger = collider.isTrigger });
                }
                held[go] = saved;
            }
            foreach (Saved item in saved) if (item.Collider != null) item.Collider.isTrigger = true;
        }
        internal static void Reset()
        {
            foreach (List<Saved> saved in held.Values)
                foreach (Saved item in saved) if (item.Collider != null) item.Collider.isTrigger = item.Trigger;
            held.Clear(); completed.Clear(); local = null;
        }
    }
}

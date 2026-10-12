using System;
using System.Collections.Generic;
using UnityEngine;
using SummerCoop.Core;

namespace SummerCoop.Game
{
    // Native loading destroys random bottle children. Visual stock follows the
    // authoritative count rather than the random selection of surviving IDs.
    public sealed class ConsumableVisuals
    {
        private readonly Dictionary<int, Transform> extras = new Dictionary<int, Transform>();
        private readonly Dictionary<Transform, Renderer[]> renderers = new Dictionary<Transform, Renderer[]>();
        public void Reconcile(GameObject owner, GameObject prefab, int remaining)
        {
            List<Transform> native = Bottles(owner.transform);
            List<Transform> source = prefab == null ? new List<Transform>() : Bottles(prefab.transform);
            foreach (Transform bottle in native) Visible(bottle, false);
            foreach (Transform bottle in extras.Values) if (bottle != null) Visible(bottle, false);
            if (source.Count == 0)
            {
                for (int i = 0; i < native.Count; i++) Visible(native[i], ConsumableStock.SlotVisible(i, remaining));
                return;
            }
            List<V3> positions = new List<V3>(), templates = new List<V3>();
            foreach (Transform bottle in native) positions.Add(WorldRegistry.From(bottle.localPosition));
            foreach (Transform bottle in source) templates.Add(WorldRegistry.From(bottle.localPosition));
            int[] slots = ConsumableStock.AssignSlots(positions, templates, remaining);
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] >= 0) { Visible(native[slots[i]], true); continue; }
                Transform visual;
                if (!extras.TryGetValue(i, out visual) || visual == null)
                {
                    Transform template = source[i];
                    GameObject copy = (GameObject)UnityEngine.Object.Instantiate(template.gameObject);
                    copy.name = "SummerCoop.BeerVisual"; copy.transform.parent = owner.transform;
                    copy.transform.localPosition = template.localPosition; copy.transform.localRotation = template.localRotation; copy.transform.localScale = template.localScale;
                    foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                    foreach (MonoBehaviour script in copy.GetComponentsInChildren<MonoBehaviour>(true)) script.enabled = false;
                    foreach (Rigidbody body in copy.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
                    copy.SetActive(true); visual = copy.transform; extras[i] = visual;
                }
                Visible(visual, true);
            }
        }
        private void Visible(Transform bottle, bool visible)
        {
            Renderer[] cached;
            if (!renderers.TryGetValue(bottle, out cached)) { cached = bottle.GetComponentsInChildren<Renderer>(true); renderers[bottle] = cached; }
            foreach (Renderer renderer in cached) if (renderer != null) renderer.enabled = visible;
        }
        private static List<Transform> Bottles(Transform owner)
        {
            List<Transform> result = new List<Transform>();
            for (int i = 0; i < owner.childCount; i++) { Transform child = owner.GetChild(i); if (child.name == "bottle" || child.name == "bottle(Clone)") result.Add(child); }
            return result;
        }
    }
}

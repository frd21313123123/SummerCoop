using System;
using System.Collections.Generic;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class NativeToolRecovery : IDisposable
    {
        private sealed class Case
        {
            public Transform Box;
            public GameObject Template;
        }
        private readonly Dictionary<GameObject, Case> cases = new Dictionary<GameObject, Case>();
        private readonly GameObject templates = new GameObject("SummerCoop.ToolTemplates");
        public NativeToolRecovery(WorldRegistry registry) { templates.SetActive(false); Discover(registry); }
        private void Discover(WorldRegistry registry)
        {
            foreach (ObjectBinding binding in registry.Objects.Values)
            {
                GameObject box = binding.Object;
                if (box == null || !ToolInventory.IsCase(box.name) || cases.ContainsKey(box)) continue;
                // Registered shop displays remain stock. Only the player's scene
                // items and newly spawned purchased cases get a recovery inventory.
                if (!binding.Key.StartsWith("scene/ITEMS[", StringComparison.Ordinal)
                    && !binding.Key.StartsWith("spawn/", StringComparison.Ordinal)) continue;
                Transform tools = box.transform.FindChild("Tools"); if (tools == null) continue;
                GameObject copy = (GameObject)UnityEngine.Object.Instantiate(tools.gameObject);
                copy.name = "Tools"; copy.transform.parent = templates.transform;
                copy.transform.localPosition = tools.localPosition; copy.transform.localRotation = tools.localRotation;
                copy.transform.localScale = tools.localScale; copy.SetActive(false);
                cases.Add(box, new Case { Box = box.transform, Template = copy });
            }
        }
        public int Restore(WorldRegistry registry)
        {
            Discover(registry); int restored = 0;
            foreach (Case item in cases.Values)
            {
                if (item.Box == null || item.Template == null) continue;
                Transform tools = item.Box.FindChild("Tools");
                if (tools == null)
                {
                    GameObject copy = (GameObject)UnityEngine.Object.Instantiate(item.Template);
                    copy.name = "Tools"; copy.transform.parent = item.Box; tools = copy.transform;
                }
                tools.localPosition = item.Template.transform.localPosition;
                tools.localRotation = item.Template.transform.localRotation;
                tools.localScale = item.Template.transform.localScale;
                RestoreChildren(item.Template.transform, tools, ref restored);
                tools.gameObject.SetActive(true);
            }
            return restored;
        }
        private static void RestoreChildren(Transform source, Transform target, ref int restored)
        {
            foreach (Transform child in source)
            {
                Transform local = target.FindChild(child.name);
                if (local == null)
                {
                    GameObject copy = (GameObject)UnityEngine.Object.Instantiate(child.gameObject);
                    copy.name = child.name; copy.transform.parent = target; local = copy.transform;
                }
                local.localPosition = child.localPosition; local.localRotation = child.localRotation; local.localScale = child.localScale;
                local.gameObject.layer = child.gameObject.layer;
                Collider[] originals = child.GetComponents<Collider>(), colliders = local.GetComponents<Collider>();
                for (int i = 0; i < colliders.Length && i < originals.Length; i++) colliders[i].enabled = originals[i].enabled;
                if (colliders.Length != 0) restored++;
                RestoreChildren(child, local, ref restored); local.gameObject.SetActive(true);
            }
        }
        public void Dispose() { if (templates != null) UnityEngine.Object.Destroy(templates); cases.Clear(); }
    }
}

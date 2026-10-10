using System;
using System.Collections.Generic;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class AvatarView : IDisposable
    {
        private sealed class Avatar { public GameObject Root; public Vector3 Position; public Quaternion Rotation; public TextMesh Label; }
        private readonly Dictionary<string, Avatar> avatars = new Dictionary<string, Avatar>();
        private Camera camera;
        public void Reconcile(World world, string local)
        {
            foreach (string id in new List<string>(avatars.Keys)) if (!world.Players.ContainsKey(id)) { UnityEngine.Object.Destroy(avatars[id].Root); avatars.Remove(id); }
            foreach (Player player in world.Players.Values)
            {
                if (player.Id == local || avatars.ContainsKey(player.Id)) continue;
                GameObject root = new GameObject("SummerCoop.Avatar." + player.Id);
                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule); body.transform.parent = root.transform; body.transform.localPosition = Vector3.up * 0.9f; body.transform.localScale = new Vector3(0.45f, 0.8f, 0.45f); UnityEngine.Object.Destroy(body.GetComponent<Collider>());
                Material material = new Material(Shader.Find("Diffuse")); material.color = new Color(0.2f, 0.55f, 0.75f); body.GetComponent<Renderer>().material = material;
                GameObject face = GameObject.CreatePrimitive(PrimitiveType.Cube); face.transform.parent = root.transform; face.transform.localPosition = new Vector3(0, 1.65f, 0.2f); face.transform.localScale = new Vector3(0.23f, 0.08f, 0.08f); UnityEngine.Object.Destroy(face.GetComponent<Collider>());
                GameObject label = new GameObject("Name"); label.transform.parent = root.transform; label.transform.localPosition = Vector3.up * 2.05f;
                TextMesh text = label.AddComponent<TextMesh>(); text.text = player.Name; text.characterSize = 0.04f; text.fontSize = 32; text.anchor = TextAnchor.MiddleCenter;
                Avatar avatar = new Avatar { Root = root, Position = WorldRegistry.To(player.Position), Rotation = WorldRegistry.To(player.Rotation), Label = text }; root.transform.position = avatar.Position; avatars.Add(player.Id, avatar);
            }
        }
        public void Pose(Pose pose) { Avatar a; if (avatars.TryGetValue(pose.Id, out a)) { a.Position = WorldRegistry.To(pose.Position); a.Rotation = WorldRegistry.To(pose.Rotation); } }
        public void Update(float dt)
        {
            if (camera == null || !camera.isActiveAndEnabled) camera = Camera.main; float blend = 1 - (float)Math.Exp(-dt * 20);
            foreach (Avatar a in avatars.Values)
            {
                a.Root.transform.position = Vector3.Distance(a.Root.transform.position, a.Position) > 3 ? a.Position : Vector3.Lerp(a.Root.transform.position, a.Position, blend);
                a.Root.transform.rotation = Quaternion.Slerp(a.Root.transform.rotation, a.Rotation, blend);
                if (camera != null) a.Label.transform.rotation = camera.transform.rotation;
            }
        }
        public void Dispose() { foreach (Avatar a in avatars.Values) UnityEngine.Object.Destroy(a.Root); avatars.Clear(); }
    }
}

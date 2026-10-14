using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class SpawnChoice
    {
        public string Id, Name;
        public bool Unique, HasCondition;
        internal GameObject Source;
        internal PlayMakerFSM Data;
        internal FsmFloat Wear;
        internal PlayMakerFSM WearOwner;
    }
    // MSC's installable parts are single native database slots. Recover that slot
    // rather than clone a second part whose bolts would still reference the first.
    // Repeatable native ITEM prefabs get independent spawn identities.
    public sealed class NativeSpawner
    {
        private readonly WorldRegistry registry;
        private readonly Dictionary<string, SpawnChoice> choices = new Dictionary<string, SpawnChoice>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> lastSpawn = new Dictionary<string, float>();
        public string CreatedItem { get; private set; }
        public void ClearCreatedItem() { CreatedItem = ""; }
        public readonly List<SpawnChoice> Choices = new List<SpawnChoice>();
        public NativeSpawner(WorldRegistry registry, Dictionary<string, GameObject> prefabs)
        {
            CreatedItem = "";
            this.registry = registry;
            PlayMakerFSM mechanicalWear = null;
            foreach (FsmBinding binding in registry.Fsms.Values)
                if (binding.Component != null && binding.Component.gameObject.name == "MechanicalWear" && binding.Component.FsmName == "Data") mechanicalWear = binding.Component;
            HashSet<string> partNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FsmBinding binding in registry.Fsms.Values)
            {
                if (binding.Component == null || binding.Component.FsmName != "Removal") continue;
                FsmGameObject database = binding.Component.FsmVariables.FindFsmGameObject("db_ThisPart");
                GameObject source = binding.Component.gameObject;
                PlayMakerFSM data = database == null || database.Value == null ? null : PlayMakerFSM.FindFsmOnGameObject(database.Value, "Data");
                if (data == null || data.FsmVariables.FindFsmBool("Installed") == null || choices.ContainsKey(binding.ObjectId)) continue;
                SpawnChoice choice = new SpawnChoice { Id = binding.ObjectId, Name = CleanName(source.name), Unique = true, Source = source, Data = data };
                if (mechanicalWear != null) foreach (FsmFloat wear in mechanicalWear.FsmVariables.FloatVariables)
                    if (wear.Name.Equals("Wear" + data.gameObject.name, StringComparison.OrdinalIgnoreCase)) { choice.Wear = wear; choice.WearOwner = mechanicalWear; break; }
                if (choice.Wear == null) { choice.Wear = data.FsmVariables.FindFsmFloat("Wear"); choice.WearOwner = data; }
                choice.HasCondition = choice.Wear != null || ConditionVariable(source) != null;
                choices.Add(choice.Id, choice); Choices.Add(choice); partNames.Add(choice.Name);
            }
            foreach (KeyValuePair<string, GameObject> entry in prefabs)
            {
                GameObject prefab = entry.Value;
                if (prefab == null || prefab.GetComponent<Rigidbody>() == null || WorldRegistry.PersonalObject(prefab)
                    || !(prefab.CompareTag("ITEM") || prefab.CompareTag("PART")) || partNames.Contains(CleanName(prefab.name))) continue;
                SpawnChoice choice = new SpawnChoice { Id = entry.Key, Name = CleanName(prefab.name), Source = prefab, HasCondition = ConditionVariable(prefab) != null };
                choices.Add(choice.Id, choice); Choices.Add(choice);
            }
            Choices.Sort(delegate(SpawnChoice a, SpawnChoice b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); });
        }
        private static string CleanName(string name) { return name.EndsWith("(Clone)", StringComparison.Ordinal) ? name.Substring(0, name.Length - 7) : name; }
        private static FsmFloat ConditionVariable(GameObject go)
        {
            foreach (PlayMakerFSM fsm in go.GetComponents<PlayMakerFSM>())
            {
                FsmFloat value = fsm.FsmVariables.FindFsmFloat("TireHealth"); if (value != null) return value;
            }
            return null;
        }
        public string Validate(string player, Command command, World world)
        {
            SpawnChoice choice;
            if (!choices.TryGetValue(command.Target, out choice) || choice.Source == null) return "Предмет недоступен в этой сцене";
            float last; if (lastSpawn.TryGetValue(player, out last) && Time.realtimeSinceStartup - last < 0.5f) return "Подождите полсекунды перед следующим предметом";
            if (choice.Unique)
            {
                choice.Source = registry.ObjectOf(choice.Id);
                if (choice.Source == null) return "Свободная деталь отсутствует в игровых данных";
                FsmBool installed = choice.Data.FsmVariables.FindFsmBool("Installed");
                if (installed != null && installed.Value) return "Деталь уже установлена. Сначала снимите её";
                foreach (Player other in world.Players.Values) if (other.Held == choice.Id) return "Деталь сейчас находится в руках игрока";
                foreach (Transform child in choice.Source.GetComponentsInChildren<Transform>(true))
                    if (child != choice.Source.transform && NativeAssembly.Installed(child.gameObject)) return "Сначала снимите установленные на этой детали части";
            }
            return null;
        }
        public void Create(string player, Command command, World world)
        {
            CreatedItem = "";
            SpawnChoice choice = choices[command.Target]; Player actor = world.Players[player];
            Vector3 position = WorldRegistry.To(actor.Position) + WorldRegistry.To(actor.Rotation) * Vector3.forward * 1.6f + Vector3.up * 0.8f;
            GameObject created;
            if (choice.Unique)
            {
                created = choice.Source; created.transform.parent = null;
                created.transform.position = position; created.transform.rotation = Quaternion.identity;
                Rigidbody body = created.GetComponent<Rigidbody>(); if (body == null) body = created.AddComponent<Rigidbody>();
                body.isKinematic = false; body.velocity = body.angularVelocity = Vector3.zero;
                created.tag = "PART"; created.layer = 19; created.SetActive(true); body.WakeUp();
                foreach (PlayMakerFSM fsm in created.GetComponents<PlayMakerFSM>()) if (fsm.FsmName == "Removal") fsm.enabled = true;
                Transform bolts = created.transform.FindChild("Bolts"); if (bolts != null) bolts.gameObject.SetActive(false);
            }
            else { created = registry.CreateNativeItem(choice.Id, choice.Source, position); CreatedItem = registry.IdOf(created); }
            float condition = PlayerControls.ClampPercent((float)command.Number);
            if (choice.HasCondition)
            {
                if (choice.Wear != null) choice.Wear.Value = condition;
                FsmFloat own = ConditionVariable(created); if (own != null) own.Value = condition;
                ObjectBinding binding; if (registry.Objects.TryGetValue(registry.IdOf(created), out binding)) binding.SandboxCondition = condition;
                FsmBool damaged = choice.Data == null ? null : choice.Data.FsmVariables.FindFsmBool("Damaged");
                if (damaged != null) damaged.Value = condition <= 0;
                if (choice.Data != null)
                {
                    FsmGameObject normal = choice.Data.FsmVariables.FindFsmGameObject("NewObject"), broken = choice.Data.FsmVariables.FindFsmGameObject("DamagedObject");
                    if (normal != null && normal.Value != null) normal.Value.SetActive(condition > 0);
                    if (broken != null && broken.Value != null) broken.Value.SetActive(condition <= 0);
                }
            }
            registry.CaptureRelated(world, registry.IdOf(created), "");
            if (choice.Data != null) registry.CaptureRelated(world, registry.IdOf(choice.Data.gameObject), "");
            if (choice.WearOwner != null) registry.CaptureRelated(world, registry.IdOf(choice.WearOwner.gameObject), "");
            lastSpawn[player] = Time.realtimeSinceStartup;
        }
    }
}

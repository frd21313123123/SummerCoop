using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using SummerCoop.Core;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class NativeActorContext : IDisposable
    {
        public static string Current;
        private readonly string previous;
        private readonly Dictionary<NamedVariable, object> locals = new Dictionary<NamedVariable, object>();
        private static readonly Dictionary<Type, PropertyInfo> values = new Dictionary<Type, PropertyInfo>();
        public NativeActorContext(string actor)
        {
            previous = Current; Current = actor;
            if (actor == "host") return;
            FsmVariables globals = FsmVariables.GlobalVariables;
            Remember(globals.GetAllNamedVariables());
        }
        private void Remember(IEnumerable<NamedVariable> variables)
        {
            foreach (NamedVariable variable in variables)
            {
                if (!NativeActionPolicy.PersonalVariable(variable.Name)) continue;
                PropertyInfo property = ValueProperty(variable);
                if (property != null && property.CanRead && property.CanWrite) locals[variable] = property.GetValue(variable, null);
            }
        }
        private static PropertyInfo ValueProperty(NamedVariable variable)
        {
            PropertyInfo property;
            if (!values.TryGetValue(variable.GetType(), out property)) { property = variable.GetType().GetProperty("Value"); values[variable.GetType()] = property; }
            return property;
        }
        public void Dispose()
        {
            try { foreach (KeyValuePair<NamedVariable, object> local in locals) ValueProperty(local.Key).SetValue(local.Key, local.Value, null); }
            finally { Current = previous; }
        }
    }
    internal static class NativePersonalReferences
    {
        private static readonly Dictionary<Fsm, HashSet<string>> names = new Dictionary<Fsm, HashSet<string>>();
        public static void Clear() { names.Clear(); }
        public static bool IsPersonal(object target, Fsm fsm)
        {
            HashSet<string> inferred;
            if (!names.TryGetValue(fsm, out inferred))
            {
                inferred = new HashSet<string>(StringComparer.Ordinal); names[fsm] = inferred;
                foreach (FsmGameObject variable in fsm.Variables.GameObjectVariables)
                    if (Direct(variable, fsm, inferred, 0)) inferred.Add(variable.Name);
                // A GetChild from FPSCamera stores Hand before SendEvent reads it.
                // Infer that reference even before the getter first executes.
                for (int pass = 0; pass < 3; pass++) foreach (FsmState state in fsm.States) foreach (FsmStateAction action in state.Actions)
                {
                    string type = action.GetType().Name;
                    if (type != "GetChild" && type != "GetParent" && type != "GetComponent") continue;
                    if (!Direct(NativeActionReader.Field(action, "gameObject"), fsm, inferred, 0)) continue;
                    NamedVariable output = NativeActionReader.Field(action, "storeResult") as NamedVariable;
                    if (output != null && output.Name != "" && !NativeActionPolicy.SharedHandReference(output.Name)) inferred.Add(output.Name);
                }
            }
            return Direct(target, fsm, inferred, 0);
        }
        private static bool Direct(object target, Fsm fsm, HashSet<string> inferred, int depth)
        {
            if (target == null || depth > 3) return false;
            GameObject go = target as GameObject; Component component = target as Component;
            if (component != null) go = component.gameObject;
            if (go != null) return !Shared(go) && WorldRegistry.PersonalObject(go);
            FsmGameObject gameObject = target as FsmGameObject;
            if (gameObject != null) return gameObject.Value != null && Shared(gameObject.Value) ? false : inferred.Contains(gameObject.Name) || NativeActionPolicy.PersonalVariable(gameObject.Name) || gameObject.Value != null && WorldRegistry.PersonalObject(gameObject.Value);
            FsmObject obj = target as FsmObject;
            if (obj != null) return inferred.Contains(obj.Name) || NativeActionPolicy.PersonalVariable(obj.Name) || Direct(obj.Value, fsm, inferred, depth + 1);
            FsmOwnerDefault owner = target as FsmOwnerDefault;
            if (owner != null) return owner.OwnerOption.ToString() == "UseOwner" ? fsm.Owner != null && Direct(fsm.Owner.gameObject, fsm, inferred, depth + 1) : Direct(owner.GameObject, fsm, inferred, depth + 1);
            FsmProperty property = target as FsmProperty;
            if (property != null) return Direct(property.TargetObject, fsm, inferred, depth + 1);
            object recipient = NativeActionReader.Field(target, "gameObject");
            return recipient != null && Direct(recipient, fsm, inferred, depth + 1);
        }
        private static bool Shared(GameObject go)
        {
            CoopRuntime runtime = CoopRuntime.Instance;
            if (runtime == null || runtime.Registry == null) return false;
            for (Transform cursor = go.transform; cursor != null; cursor = cursor.parent)
            {
                if (SummerCoop.Core.LocalHierarchy.IsNestedName(cursor.name)) return false;
                if (runtime.Registry.IdOf(cursor.gameObject) != "") return true;
            }
            return false;
        }
    }
}

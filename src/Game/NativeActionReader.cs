using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using SummerCoop.Core;

namespace SummerCoop.Game
{
    // The same reader is tested against the actual installed PlayMaker actions.
    public static class NativeActionReader
    {
        public static object Field(object value, string name)
        {
            if (value == null) return null;
            FieldInfo field = value.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null) return field.GetValue(value);
            PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return property == null ? null : property.GetValue(value, null);
        }
        public static NativeActionInfo Describe(FsmStateAction action, Func<object, bool> personal)
        {
            NativeActionInfo info = new NativeActionInfo { Name = action.GetType().Name };
            foreach (FieldInfo field in action.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = field.GetValue(action);
                if (field.Name.Equals("gameObject", StringComparison.OrdinalIgnoreCase) || field.Name == "eventTarget" || field.Name == "targetProperty")
                    if (personal(value)) info.PersonalTarget = true;
                NamedVariable variable = value as NamedVariable;
                bool destinationField = field.Name == "variable" || field.Name.EndsWith("Variable", StringComparison.Ordinal);
                if (!NativeActionPolicy.ReadOnly(info.Name) && destinationField && variable != null && NativeActionPolicy.PersonalVariable(variable.Name)) info.WritesPersonal = true;
                // Read actions can still write player velocity or other output
                // globals every frame after the synchronous actor context ends.
                bool output = field.Name.StartsWith("store", StringComparison.Ordinal)
                    || (info.Name == "GetVelocity" || info.Name == "GetPosition" || info.Name == "GetRotation" || info.Name == "GetScale")
                        && (field.Name == "vector" || field.Name == "quaternion" || field.Name == "x" || field.Name == "y" || field.Name == "z");
                if (output && variable != null && NativeActionPolicy.PersonalVariable(variable.Name)) info.WritesPersonal = true;
            }
            // SetBoolValue etc. are safe scratch writes, but global personal values
            // still must not be written on the host for a remote actor.
            object destination = Field(action, "boolVariable") ?? Field(action, "floatVariable") ?? Field(action, "intVariable") ?? Field(action, "stringVariable") ?? Field(action, "variable");
            NamedVariable named = destination as NamedVariable;
            if (named != null && NativeActionPolicy.PersonalVariable(named.Name) && !info.Name.StartsWith("Get", StringComparison.Ordinal) && info.Name != "BoolTest" && info.Name.IndexOf("Compare", StringComparison.Ordinal) < 0)
                info.WritesPersonal = true;
            return info;
        }
        public static HashSet<string> InputOutputs(IEnumerable<FsmStateAction> actions)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (FsmStateAction action in actions)
            {
                string type = action.GetType().Name;
                if (type != "MousePick" && type != "Raycast" && type != "TriggerEvent" && !(NativeActionPolicy.ReadOnly(type) && type.StartsWith("Get", StringComparison.Ordinal))) continue;
                foreach (FieldInfo field in action.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!field.Name.StartsWith("store", StringComparison.Ordinal) && field.Name != "storeResult") continue;
                    NamedVariable variable = field.GetValue(action) as NamedVariable;
                    if (variable != null && variable.Name != "") names.Add(variable.Name);
                }
            }
            return names;
        }
    }
}

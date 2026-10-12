using System;
using System.Collections.Generic;
using System.Reflection;

namespace SummerCoop.Game
{
    public static class NativeVehicleBindings
    {
        // CarController.GetInput is an abstract virtual slot in this game.
        // Harmony needs the concrete overrides, including non-axis controllers.
        public static MethodInfo[] InputMethods()
        {
            Type[] types;
            try { types = typeof(CarController).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            HashSet<MethodInfo> methods = new HashSet<MethodInfo>();
            foreach (Type type in types)
            {
                if (type == null || !typeof(CarController).IsAssignableFrom(type)) continue;
                MethodInfo input = type.GetMethod("GetInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (input != null && !input.IsAbstract && input.GetMethodBody() != null) methods.Add(input);
            }
            MethodInfo[] result = new MethodInfo[methods.Count]; methods.CopyTo(result); return result;
        }
    }
}

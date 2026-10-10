using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;

class InspectTypes
{
    static string managed;
    static void Main(string[] args)
    {
        managed = args[0];
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            string path = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.ReflectionOnlyLoadFrom(path) : Assembly.ReflectionOnlyLoad(e.Name);
        };
        Assembly a = Assembly.ReflectionOnlyLoadFrom(Path.Combine(managed, args[1]));
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "*") { foreach (Type t in a.GetTypes()) Console.WriteLine(t.FullName); continue; }
            string[] query = args[i].Split(':'); Type type = a.GetType(query[0], true); Console.WriteLine(type.FullName);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (MethodInfo m in type.GetMethods(flags)) if (m.DeclaringType == type && (query.Length == 1 || query[1] == m.Name)) { Console.WriteLine(m); foreach (ParameterInfo p in m.GetParameters()) Console.WriteLine("PARAM " + p.Position + " " + p.Name); if (query.Length > 1) Dump(m); }
            if (query.Length > 1) continue;
            foreach (FieldInfo f in type.GetFields(flags)) Console.WriteLine("FIELD " + f);
            foreach (ConstructorInfo c in type.GetConstructors(flags)) Console.WriteLine("CTOR " + c);
        }
    }
    static void Dump(MethodInfo method)
    {
        MethodBody body = method.GetMethodBody(); if (body == null) return;
        Dictionary<short, OpCode> codes = new Dictionary<short, OpCode>();
        foreach (FieldInfo f in typeof(OpCodes).GetFields()) if (f.FieldType == typeof(OpCode)) { OpCode c = (OpCode)f.GetValue(null); codes[c.Value] = c; }
        byte[] il = body.GetILAsByteArray(); int pos = 0;
        while (pos < il.Length)
        {
            int start = pos; short code = il[pos++]; if (code == 254) code = (short)(0xfe00 | il[pos++]); OpCode op = codes[code]; object value = "";
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: value = il[pos++]; break;
                case OperandType.InlineVar: value = BitConverter.ToUInt16(il, pos); pos += 2; break;
                case OperandType.InlineI8: value = BitConverter.ToInt64(il, pos); pos += 8; break;
                case OperandType.ShortInlineR: value = BitConverter.ToSingle(il, pos); pos += 4; break;
                case OperandType.InlineR: value = BitConverter.ToDouble(il, pos); pos += 8; break;
                case OperandType.InlineSwitch: int n = BitConverter.ToInt32(il, pos); pos += 4 + n * 4; value = n; break;
                default:
                    int token = BitConverter.ToInt32(il, pos); pos += 4; value = token;
                    try { if (op.OperandType == OperandType.InlineString) value = method.Module.ResolveString(token); else if (op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineField || op.OperandType == OperandType.InlineType || op.OperandType == OperandType.InlineTok) value = method.Module.ResolveMember(token); } catch { }
                    break;
            }
            Console.WriteLine(start.ToString("x4") + " " + op.Name + " " + value);
        }
    }
}

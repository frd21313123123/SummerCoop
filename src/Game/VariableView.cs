using HutongGames.PlayMaker;

namespace SummerCoop.Game
{
    // Enumerate the native typed arrays directly. Refresh also handles PlayMaker Start
    // replacing templates or variables without allocating a concatenated list.
    public sealed class VariableView
    {
        private readonly NamedVariable[][] groups = new NamedVariable[13][];
        public VariableView(FsmVariables variables) { Refresh(variables); }
        public void Refresh(FsmVariables v)
        {
            groups[0] = v.FloatVariables; groups[1] = v.IntVariables; groups[2] = v.BoolVariables;
            groups[3] = v.StringVariables; groups[4] = v.Vector2Variables; groups[5] = v.Vector3Variables;
            groups[6] = v.RectVariables; groups[7] = v.QuaternionVariables; groups[8] = v.GameObjectVariables;
            groups[9] = v.ObjectVariables; groups[10] = v.MaterialVariables; groups[11] = v.TextureVariables; groups[12] = v.ColorVariables;
        }
        public Enumerator GetEnumerator() { return new Enumerator(groups); }
        public struct Enumerator
        {
            private readonly NamedVariable[][] groups;
            private int group, item;
            public Enumerator(NamedVariable[][] groups) { this.groups = groups; group = 0; item = -1; }
            public NamedVariable Current { get { return groups[group][item]; } }
            public bool MoveNext()
            {
                item++;
                while (group < groups.Length) { if (item < groups[group].Length) return true; group++; item = 0; }
                return false;
            }
        }
    }
}

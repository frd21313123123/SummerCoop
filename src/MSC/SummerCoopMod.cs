using MSCLoader;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class SummerCoopMod : Mod
    {
        public override string ID { get { return "SummerCoop"; } }
        public override string Name { get { return "SummerCoop / MWCoop for MSC"; } }
        public override string Author { get { return "MWCoop contributors; SummerCoop contributors"; } }
        public override string Version { get { return Core.Protocol.ModVersion; } }
        public override void ModSetup()
        {
            SetupFunction(Setup.OnMenuLoad, Menu);
            SetupFunction(Setup.PreLoad, BeforeGame);
        }
        private void Menu()
        {
            if (MWCoop.Entry.Root != null) return;
            MWCoop.MscRuntime.PrepareEnvironment();
            MWCoop.Entry.Init();
            if (MWCoop.Entry.Root != null) MWCoop.Entry.Root.AddComponent<MWCoop.MscConnectPanel>();
            if (MWCoop.Entry.Root != null && MWCoop.MscRuntime.Argument("--summercoop-test-report").Length > 0) MWCoop.Entry.Root.AddComponent<MWCoop.MscSmokeProbe>();
            ModConsole.Print("SummerCoop " + Version + " — MWCoop engine, MSC bindings");
        }
        private void BeforeGame() { MWCoop.MscRuntime.ConfigureGame(); MWCoop.MscParts.Index(); }
    }
}

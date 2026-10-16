using MSCLoader;
using UnityEngine;

namespace SummerCoop.Game
{
    public sealed class SummerCoopMod : Mod
    {
        public override string ID { get { return "SummerCoop"; } }
        public override string Name { get { return "SummerCoop"; } }
        public override string Author { get { return "SummerCoop contributors"; } }
        public override string Version { get { return Core.Protocol.ModVersion; } }
        public override void ModSetup()
        {
            MWCoop.MscTestEnvironment.Install();
            SetupFunction(Setup.OnMenuLoad, Menu);
            SetupFunction(Setup.PreLoad, BeforeGame);
        }
        private void Menu()
        {
            if (MWCoop.Entry.Root != null) return;
            MWCoop.MscRuntime.PrepareEnvironment();
            MWCoop.MscRuntime.TouchLoaderTrace();
            MWCoop.Entry.Init();
            if (MWCoop.Entry.Root != null) MWCoop.Entry.Root.AddComponent<MWCoop.MscConnectPanel>();
            if (MWCoop.Entry.Root != null && MWCoop.MscRuntime.Argument("--summercoop-test-report").Length > 0) MWCoop.Entry.Root.AddComponent<MWCoop.MscSmokeProbe>();
            ModConsole.Print("SummerCoop " + Version);
        }
        private void BeforeGame() { MWCoop.MscRuntime.TouchLoaderTrace(); MWCoop.MscRuntime.ConfigureGame(); MWCoop.MscParts.Index(); }
    }
}

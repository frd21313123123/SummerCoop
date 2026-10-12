namespace SummerCoop.Core
{
    public sealed class SceneReadiness
    {
        private int readyFrames;
        private int lastFrame = -1;
        public bool Observe(int frame, bool sceneLoaded, bool playerReady, bool cameraReady)
        {
            if (!sceneLoaded || !playerReady || !cameraReady) { readyFrames = 0; lastFrame = frame; return false; }
            if (frame != lastFrame) { readyFrames++; lastFrame = frame; }
            return readyFrames >= 2;
        }
    }
}

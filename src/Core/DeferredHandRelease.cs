namespace SummerCoop.Core
{
    // The native hand and assembly FSMs see the same click in either order.
    // Keep the hand intact until the next frame can claim an attachment.
    public sealed class DeferredHandRelease<T> where T : class
    {
        private T hand;
        private string state, target;
        private int queuedFrame;
        public bool Pending { get { return hand != null; } }
        public void Queue(T owner, string nextState, string held, int frame)
        {
            if (Pending) return;
            hand = owner; state = nextState; target = held; queuedFrame = frame;
        }
        public void Cancel() { hand = null; state = target = null; }
        public bool Take(int frame, string held, bool attaching, out T owner, out string nextState, out string item)
        {
            owner = null; nextState = item = null;
            if (!Pending) return false;
            if (attaching || held != target) { Cancel(); return false; }
            if (frame <= queuedFrame) return false;
            owner = hand; nextState = state; item = target; Cancel(); return true;
        }
    }
}

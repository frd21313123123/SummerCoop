namespace SummerCoop.Core
{
    // Only lobby creation is retried. A failed lobby must not close the host world.
    // Keep one outstanding API call: a late result is explicitly declined by Complete.
    public sealed class LobbyCreation
    {
        public const int MaxAttempts = 3;
        public const double WaitSeconds = 18;
        public bool Running { get; private set; }
        public bool Pending { get; private set; }
        public bool Ready { get; private set; }
        public int Attempts { get; private set; }
        public string Error { get; private set; }
        private double deadline, next;

        public bool CanRetry { get { return !Running && !Pending && !Ready; } }
        public bool Start(double now)
        {
            if (Pending || Ready || Running) return false;
            Running = true; Attempts = 0; Error = ""; deadline = now + WaitSeconds; next = now;
            return true;
        }
        public bool Request(double now, bool connected)
        {
            if (!Running) return false;
            if (now >= deadline) { Running = false; if (Error == "") Error = "Steam не восстановил соединение за 18 секунд"; return false; }
            if (Pending || !connected || now < next) return false;
            Pending = true; Attempts++; return true;
        }
        public bool Complete(double now, bool success, bool retryable, string error)
        {
            if (!Pending) return false;
            Pending = false;
            if (!Running || now >= deadline) { Running = false; if (Error == "") Error = "Steam не ответил за 18 секунд"; return false; }
            if (success) { Ready = true; Running = false; Error = ""; return true; }
            Error = error;
            if (!retryable || Attempts >= MaxAttempts) Running = false;
            else next = now + Attempts * 2;
            return false;
        }
    }
}

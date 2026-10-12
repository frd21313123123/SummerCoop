using System;
using SummerCoop.Core;

internal static class LobbyCreationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        test("Steam lobby waits for backend and sends one request when connection recovers", delegate
        {
            LobbyCreation lobby = new LobbyCreation(); Check(lobby.Start(100), "Could not start");
            for (int i = 0; i < 100; i++) Check(!lobby.Request(100 + i * .01, false), "Sent an offline request");
            Check(lobby.Attempts == 0 && lobby.Running, "Offline polling consumed attempts");
            Check(lobby.Request(101, true), "Did not request on reconnect");
            Check(!lobby.Request(102, true) && !lobby.Start(102), "Overlapping API requests");
            Check(lobby.Complete(103, true, false, "") && lobby.Ready && !lobby.CanRetry, "Success not published");
            Check(!lobby.Request(104, true) && !lobby.Complete(104, true, false, ""), "Duplicate callback created a second lobby");
        });
        test("Steam transient failures retry with backoff and stop after three attempts", delegate
        {
            LobbyCreation lobby = new LobbyCreation(); lobby.Start(0); Check(lobby.Request(0, true), "Missing first request");
            lobby.Complete(0, false, true, "NoConnection");
            Check(!lobby.Request(1.99, true) && lobby.Request(2, true), "First backoff wrong");
            lobby.Complete(2, false, true, "Timeout");
            Check(!lobby.Request(5.99, true) && lobby.Request(6, true), "Second backoff wrong");
            lobby.Complete(6, false, true, "NoConnection");
            Check(lobby.Attempts == 3 && lobby.CanRetry && !lobby.Request(7, true), "Unbounded automatic retries");
            Check(lobby.Error == "NoConnection", "Lost diagnostic");
        });
        test("Steam permanent refusal is not retried and manual retry resets the attempt budget", delegate
        {
            LobbyCreation lobby = new LobbyCreation(); lobby.Start(0); lobby.Request(0, true);
            lobby.Complete(1, false, false, "AccessDenied");
            Check(lobby.CanRetry && !lobby.Request(2, true), "Permanent refusal retried automatically");
            Check(lobby.Start(50) && lobby.Request(50, true) && lobby.Attempts == 1, "Manual restart failed");
            Check(lobby.Complete(51, true, false, ""), "Restart did not recover");
        });
        test("Steam offline wait expires and can be retried without restarting the world", delegate
        {
            LobbyCreation lobby = new LobbyCreation(); lobby.Start(10);
            Check(!lobby.Request(27.99, false) && lobby.Running, "Expired early");
            Check(!lobby.Request(28, true) && lobby.CanRetry && lobby.Attempts == 0, "Wait was not bounded");
            Check(lobby.Start(30) && lobby.Request(30, true) && lobby.Complete(31, true, false, ""), "Recovery failed");
        });
        test("Steam late success is declined for cleanup before another lobby can be requested", delegate
        {
            LobbyCreation lobby = new LobbyCreation(); lobby.Start(0); lobby.Request(0, true);
            Check(!lobby.Request(18, true) && !lobby.Running && lobby.Pending, "Outstanding request forgotten");
            Check(!lobby.CanRetry && !lobby.Start(19), "Restart overlapped a late callback");
            Check(!lobby.Complete(20, true, false, "") && !lobby.Ready && lobby.CanRetry, "Late lobby published instead of left");
            Check(lobby.Start(21) && lobby.Request(21, true), "Late callback blocked recovery");
        });
        test("Steam success received after deadline is declined even before the next update", delegate
        {
            LobbyCreation lobby = new LobbyCreation(); lobby.Start(0); lobby.Request(0, true);
            Check(!lobby.Complete(18, true, false, "") && lobby.CanRetry && !lobby.Ready, "Callback bypassed deadline");
        });
    }
}

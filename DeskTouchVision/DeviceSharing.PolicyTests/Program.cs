using DeskTouch.DeviceShare;

static void Check(bool test, string message)
{
    if (!test) throw new Exception("Input priority regression: " + message);
}

long fakeClock = 100000;
var priority = new LocalInputPriority(() => fakeClock);
int nativePreemptions = 0;
priority.PhysicalOverride += () => nativePreemptions++;

Check(!priority.RemoteGranted, "Local should be owner when app starts");
Check(!priority.TryClaim(), "Cannot take control immediately after launching app");

fakeClock += LocalInputPriority.IdleBeforeRemoteMilliseconds;
Check(priority.TryClaim(), "Idle receiver must allow explicit claim");
Check(priority.CanApplyRemote, "A granted remote peer may inject input");
Check(priority.TryClaim(), "Re-claim while granted must not revoke");

priority.RecordPhysicalActivity();
Check(!priority.RemoteGranted, "Physical keyboard/mouse must instantly revoke remote");
Check(!priority.CanApplyRemote, "Remote input must be rejected after native event");
Check(nativePreemptions == 1, "Native override must notify exactly once");

priority.RecordPhysicalActivity();
Check(nativePreemptions == 1, "Repeated physical movement must not flood revoke callbacks");
Check(!priority.TryClaim(), "Must reject reclaim while local keyboard/mouse active");

fakeClock += LocalInputPriority.IdleBeforeRemoteMilliseconds - 1;
Check(!priority.TryClaim(), "Must wait until idle threshold");
fakeClock++;
Check(priority.TryClaim(), "Should permit a new deliberate claim after idleness");
Check(priority.CanApplyRemote, "New remote session permitted only after claim");
priority.ReleaseRemote();
Check(!priority.CanApplyRemote, "Releasing control must keep local authority");

Console.WriteLine("PASS: native Windows keyboard/mouse priority, immediate revoke, idle gating and explicit re-claim.");

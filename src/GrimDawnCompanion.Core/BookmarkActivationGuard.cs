namespace GrimDawnCompanion.Core;

// Monotonic milliseconds, UI-thread owned. No queued activations or timers that move a player.
public sealed class BookmarkActivationGuard
{
    public const long CooldownMs=2000, ReadyWindowMs=1000, LeaseMs=750, MaxReadMs=250;
    private long _readySince=-1, _lastReady=-1, _blockedUntil;
    private string? _identity;
    private bool _active;
    public void InvalidateReadiness() { _readySince=_lastReady=-1; _identity=null; }
    public void ObserveReady(long started,long finished,string identity)
    {
        if(finished<started || finished-started>MaxReadMs || _active) { InvalidateReadiness(); return; }
        if(_identity!=identity || _lastReady<0 || finished-_lastReady>LeaseMs) _readySince=finished;
        _identity=identity; _lastReady=finished;
    }
    public bool IsReady(long now)=>!_active && now>=_blockedUntil && _readySince>=0 &&
        now>=_lastReady && now-_lastReady<=LeaseMs && _lastReady-_readySince>=ReadyWindowMs;
    public bool TryBegin(long now,bool requireReady)
    {
        if(_active || now<_blockedUntil || (requireReady && !IsReady(now))) return false;
        _active=true; InvalidateReadiness(); return true;
    }
    public void Complete(long now)
    {
        _active=false; _blockedUntil=now+CooldownMs; InvalidateReadiness();
    }
}

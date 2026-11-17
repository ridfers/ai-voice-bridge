namespace AiVoiceBridge.Core;

// User intent is separate from audio state and is never persisted to disk.
public sealed class TransmissionControl
{
    private bool _localPressed, _rawPressed;
    public bool Latched { get; private set; }
    public bool Requested => Latched || _localPressed || _rawPressed;
    public void SetLocal(bool pressed) => _localPressed=pressed;
    public void SetRaw(bool pressed) => _rawPressed=pressed;
    public void ToggleLatch()
    {
        if(Latched) Reset();
        else Latched=true;
    }
    public void Reset() { Latched=false; _localPressed=false; _rawPressed=false; }
}

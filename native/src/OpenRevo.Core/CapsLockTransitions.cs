namespace OpenRevo.Core;

// The observer retains only the Caps toggle and its current down/up latch.
public sealed class CapsLockTransitions(bool initialLocked)
{
    private readonly object gate=new();
    private bool locked=initialLocked,pressed;
    public bool Locked{get{lock(gate)return locked;}}
    public bool? Accept(uint virtualKey,bool down)
    {
        if(virtualKey!=0x14)return null;
        lock(gate)
        {
            if(!down){pressed=false;return null;}
            if(pressed)return null;
            pressed=true;locked=!locked;return locked;
        }
    }
}

namespace OpenRevo.Core.Hal;
public sealed partial class HardwareSession
{
    public object SendMonochromeLevel(int level)=>Serialized(()=>
    {
        Guard();if(!Active||!CoreDelegated)throw new InvalidOperationException("当前通道不提供单色背光命令。");
        audit(new{operation="monochrome-command-intent",level,hardwareReadback=false});
        CoreMachine.Library.SendMonochromeLevel(level);
        var receipt=new{operation="monochrome-command-sent",requestedLevel=level,hardwareReadback=false};
        audit(receipt);return receipt;
    });
}

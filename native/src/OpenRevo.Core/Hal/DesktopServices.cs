namespace OpenRevo.Core.Hal;
public sealed partial class HardwareSession
{
    public void ApplyDesktopService(string service,Action operation,bool needsTakeover=false)=>Serialized(()=>
    {
        if(needsTakeover&&!Active)throw new InvalidOperationException("请先连接设备。");
        Guard();audit(new{at=DateTimeOffset.Now,operation="desktop-service-intent",service});
        try{operation();audit(new{at=DateTimeOffset.Now,operation="desktop-service-confirmed",service});}
        catch(Exception error){audit(new{at=DateTimeOffset.Now,operation="desktop-service-failed",service,error=error.Message});throw;}
        return true;
    });
    public void SetDesktopPowerOption(IDesktopPowerCore core,string key,bool enabled)=>Serialized(()=>
    {
        if(!Active)throw new InvalidOperationException("请先连接设备。");
        if(key is not("usb" or "recovery"))throw new ArgumentOutOfRangeException(nameof(key));
        Guard();using var ec=open();var control=Read(ec);
        ushort address=key=="usb"?Vault.Trigger:Vault.AcRecovery;
        byte mask=key=="usb"?Vault.UsbChargeMask:Vault.AcRecoveryMask;
        byte before=ec.Read(address)??throw new IOException("供电状态未能读取。");
        byte desired=enabled?(byte)(before|mask):(byte)(before&~mask);
        audit(new{at=DateTimeOffset.Now,operation="power-option-intent",key,before,desired});
        try
        {
            core.SetDesktopPowerOption(key,before,enabled);
            if(ec.Read(address)!=desired||Read(ec)!=control)throw new IOException("供电状态或无关硬件位回读不一致。");
            audit(new{at=DateTimeOffset.Now,operation="power-option-confirmed",key,desired});
        }
        catch{faulted=true;throw;}
        return true;
    });
}

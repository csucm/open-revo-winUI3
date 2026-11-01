using OpenRevo.Core;
using OpenRevo.Core.Hal;
using System.Text.Json;
internal static class CoreAdmissionChecks
{
    public static void Run(string path)
    {
        var checks=new List<object>();
        void Check(string name,Action run){try{run();checks.Add(new{name,passed=true});}catch(Exception e){checks.Add(new{name,passed=false,error=e.Message});}}
        void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
        var standard=new CoreCompatibility(true,true,true,"TongfangAcpiKernel","Tongfang-ACPI",null,73,"Core已连接",2,true,100,true,true,true,true,true,true);
        var reportDevice=new DeviceIdentity("MECHREVO","yilong15 Pro Series GM5HG0A","GM5HG0A","AMD Ryzen 7 8745H",["NVIDIA RTX 4060"]);
        foreach(string board in new[]{"GM5HG0A","GM6IQ0C","GM6IX9B","OTHER_CORE_SUPPORTED_BOARD"})
        Check(board+" Core admission does not depend on local DMI",()=>
        {
            using var transport=new Transport(73);var result=EcReader.Read(reportDevice with{Board=board},()=>transport,standard);
            Assert(result.CpuFan==0&&transport.Opens==1,"Supported Core platform rejected by name");
        });
        Check("Unknown Core never opens transport",()=>{int opens=0;EcReader.Read(reportDevice,()=>{opens++;throw new Exception();},CoreCompatibility.Unknown("未适配"));Assert(opens==0,"Unknown opened transport");});
        Check("Core unavailable never admits take over",()=>{int opens=0;var s=new HardwareSession(()=>{opens++;throw new Exception();},()=>null,_=>{},fixtureAdmission:standard with{Ready=false});try{s.Takeover();throw new Exception("admitted");}catch(InvalidOperationException){}Assert(!s.Active&&opens==0,"Unavailable admitted");});
        Check("Non-16 Core project accepted and read only",()=>{var t=new Transport(73);var s=new HardwareSession(()=>t,()=>null,_=>{},fixtureAdmission:standard);Assert(s.Takeover()?.Project==73&&s.Active&&t.Writes==0,"Pilot project restriction remains");});
        Check("Changed EC project rejected before mutation",()=>{var t=new Transport(73);var s=new HardwareSession(()=>t,()=>null,_=>{},fixtureAdmission:standard);s.Takeover();t.Data[Vault.ProjectId]=74;try{s.SetFanBoost(true);throw new Exception("wrote");}catch(InvalidOperationException){}Assert(t.Writes==0,"Project mismatch wrote");});
        Check("Disabled Core capability rejected before mutation",()=>{var t=new Transport(73);var s=new HardwareSession(()=>t,()=>null,_=>{},fixtureAdmission:standard with{FanBoost=false});s.Takeover();try{s.SetFanBoost(true);throw new Exception("wrote");}catch(InvalidOperationException){}Assert(t.Writes==0,"Unavailable feature wrote");});
        foreach(string channel in new[]{"WmiMailbox","Ip3Wmi","LegacyTongfangMulong"})
        Check(channel+" connects without classic register access",()=>{int opens=0;var s=new HardwareSession(()=>{opens++;throw new Exception();},()=>null,_=>{},fixtureAdmission:standard with{ClassicEc=false,Channel=channel,Project=null});Assert(s.Takeover()==null&&s.Active&&opens==0,"Wrong transport chosen");s.Release();Assert(!s.Active,"Release lost");});
        Check("AMD 95 C uses literal byte DF",()=>Assert(HardwareSession.EncodeTemperature(true,100,true,95,95)==0xdf,"Wrong AMD encoding"));
        Check("Intel 95 C uses offset 85",()=>Assert(HardwareSession.EncodeTemperature(false,100,true,95,5)==0x85,"Wrong Intel encoding"));
        Check("Intel 105 TjMax uses offset 8A",()=>Assert(HardwareSession.EncodeTemperature(false,105,true,95,10)==0x8a,"Wrong Core Ultra encoding"));
        Check("AMD disable restores factory Celsius",()=>Assert(HardwareSession.EncodeTemperature(true,100,false,95,85)==85,"AMD factory lost"));
        Check("AMD invalid factory never writes zero Celsius",()=>Assert(HardwareSession.EncodeTemperature(true,100,false,95,0)==95,"AMD thermal zero"));
        Check("Intel disable restores valid offset",()=>Assert(HardwareSession.EncodeTemperature(false,105,false,95,0)==10,"Intel offset lost"));
        var failed=checks.Count(c=>!JsonSerializer.SerializeToElement(c).GetProperty("passed").GetBoolean());
        File.WriteAllText(path,JsonSerializer.Serialize(new{allPassed=failed==0,count=checks.Count,failed,realHardwareWrites=0,remoteMachineVerified=false,checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Core admission: {checks.Count-failed}/{checks.Count}");Environment.ExitCode=failed==0?0:1;
    }
    private sealed class Transport: IHardwareTransport
    {
        public int Opens,Writes;public Dictionary<ushort,byte> Data=new();
        public Transport(byte project){Data[Vault.ProjectId]=project;Data[Vault.BatteryUpper]=80;Data[Vault.BatteryLower]=75;Data[Vault.BatteryMode]=17;Data[Vault.FanClamp]=0;Data[Vault.PlatformId]=0;Data[Vault.Support1]=128;Data[Vault.Support2]=64;Data[Vault.PowerStatus]=1;}
        public byte? Read(ushort address){if(address==Vault.ProjectId)Opens++;return Data.TryGetValue(address,out byte value)?value:(byte)0;}
        public bool Write(ushort address,byte value){Writes++;Data[address]=value;return true;}
        public void Dispose(){}
    }
}

using OpenRevo.Native;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class OsdFrameChecks
{
    internal static void Run(string output)
    {
        var checks=new List<object>();var samples=new List<object>();
        void Assert(bool ok,string reason){if(!ok)throw new Exception(reason);}
        void Check(string name,Action test){try{test();checks.Add(new{name,passed=true});}catch(Exception error){checks.Add(new{name,passed=false,error=error.ToString()});}}
        Check("Recorded production styles lose the reintroduced frame",()=>
        {
            Assert(OsdFramePolicy.Style(0x94480000)==0x94000000,"Dialog or system frame remains");
            Assert(OsdFramePolicy.Extended(0x080001A8,false)==0x080000A8,"Window edge remains");
        });
        Check("Visibility and sibling clipping survive normalization",()=>Assert((OsdFramePolicy.Style(0x94480000)&0x14000000)==0x14000000,"Unrelated bits removed"));
        Check("Production stays passive and out of app switching",()=>
        {
            uint value=OsdFramePolicy.Extended(uint.MaxValue,false);
            Assert((value&0x080000A0)==0x080000A0&&(value&0x40000)==0,"Passive tool window flags lost");
        });
        Check("Review switching flag never leaks into the production tool window",()=>
        {
            uint value=OsdFramePolicy.Extended(0,true);
            Assert((value&0x40000)!=0&&(value&0x80)==0,"Review app/window flags conflict");
        });
        Check("STYLESTRUCT rewriting preserves old style and ignores other indexes",()=>
        {
            nint pointer=Marshal.AllocHGlobal(Marshal.SizeOf<OsdFramePolicy.Change>());
            try
            {
                Marshal.StructureToPtr(new OsdFramePolicy.Change{Old=0x12345678,New=0x94480000},pointer,false);
                Assert(OsdFramePolicy.Rewrite(-16,pointer,false),"Style change not handled");
                var value=Marshal.PtrToStructure<OsdFramePolicy.Change>(pointer);
                Assert(value.Old==0x12345678&&value.New==0x94000000,"STYLESTRUCT damaged");
                Assert(!OsdFramePolicy.Rewrite(-4,pointer,false)&&!OsdFramePolicy.Rewrite(-16,0,false),"Unrelated pointer handled");
            }
            finally{Marshal.FreeHGlobal(pointer);}
        });
        foreach(bool review in new[]{false,true})
        Check($"Hidden native window rejects downstream frame injection (review={review})",()=>
        {
            nint before=GetForegroundWindow();
            nint window=CreateWindowEx(0,"STATIC","Open-Revo hidden frame check",0x80480000,0,0,320,100,0,0,0,0);
            if(window==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            int attempts=0;bool hiddenThroughout=true;
            Hook framework=(hwnd,msg,wp,lp,id,data)=>
            {
                nint result=DefSubclassProc(hwnd,msg,wp,lp);
                if(msg==OsdFramePolicy.StyleChanging&&lp!=0)
                {
                    var value=Marshal.PtrToStructure<OsdFramePolicy.Change>(lp);
                    if(unchecked((int)wp)==-16)value.New|=0x00480000;
                    if(unchecked((int)wp)==-20)value.New|=0x00000100;
                    Marshal.StructureToPtr(value,lp,false);attempts++;
                }
                return result;
            };
            Hook policy=(hwnd,msg,wp,lp,id,data)=>
            {
                if(msg==OsdFramePolicy.StyleChanging&&lp!=0){DefSubclassProc(hwnd,msg,wp,lp);OsdFramePolicy.Rewrite(unchecked((int)wp),lp,review);return 0;}
                if(msg==OsdFramePolicy.CalculateClient)return 0;
                return DefSubclassProc(hwnd,msg,wp,lp);
            };
            try
            {
                GetWindowRect(window,out var initialOuter);GetClientRect(window,out var initialClient);
                Assert(SetWindowSubclass(window,framework,1,0)&&SetWindowSubclass(window,policy,2,0),"Native subclass hook failed");
                foreach(uint requested in new uint[]{0x80480000,0x80CF0000,0x84000000})
                for(int repeat=0;repeat<4;repeat++)
                {
                    SetWindowLongPtr(window,-16,(nint)requested);
                    SetWindowLongPtr(window,-20,(nint)0x080003A8);
                    Assert(SetWindowPos(window,0,0,0,320,100,0x34),"Frame refresh failed");
                    uint style=unchecked((uint)GetWindowLongPtr(window,-16)),ex=unchecked((uint)GetWindowLongPtr(window,-20));
                    GetWindowRect(window,out var outer);GetClientRect(window,out var client);
                    hiddenThroughout&=!IsWindowVisible(window);
                    Assert(OsdFramePolicy.Borderless(style,ex),"Framework restored its frame");
                    Assert(outer.Right-outer.Left==client.Right-client.Left&&outer.Bottom-outer.Top==client.Bottom-client.Top,"Non-client inset remains");
                }
                Assert(attempts>=24,"Injection path was not exercised");
                Assert(hiddenThroughout&&GetForegroundWindow()==before,"Fixture became visible or took focus");
                GetClientRect(window,out var finalClient);
                samples.Add(new{review,downstreamInjections=attempts,initialOuterWidth=initialOuter.Right-initialOuter.Left,initialClientWidth=initialClient.Right-initialClient.Left,finalClientWidth=finalClient.Right-finalClient.Left,finalClientHeight=finalClient.Bottom-finalClient.Top,hiddenThroughout});
            }
            finally{DestroyWindow(window);GC.KeepAlive(framework);GC.KeepAlive(policy);}
        });
        int failed=checks.Count(c=>!JsonSerializer.SerializeToElement(c).GetProperty("passed").GetBoolean());
        File.WriteAllText(output,JsonSerializer.Serialize(new{allPassed=failed==0,count=checks.Count,failed,winUiLaunched=false,visibleWindowsCreated=0,userWindowsChanged=false,hardwareWrites=0,checks,samples},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"OSD frame checks: {checks.Count-failed}/{checks.Count}");Environment.ExitCode=failed==0?0:1;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate nint Hook(nint window,uint message,nuint wp,nint lp,nuint id,nuint data);
    [DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("comctl32.dll",SetLastError=true)]private static extern bool SetWindowSubclass(nint window,Hook callback,nuint id,nuint data);
    [DllImport("comctl32.dll")]private static extern nint DefSubclassProc(nint window,uint message,nuint wp,nint lp);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern nint SetWindowLongPtr(nint window,int index,nint value);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool GetClientRect(nint window,out Rect rect);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
}

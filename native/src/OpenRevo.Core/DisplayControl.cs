using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OpenRevo.Core;

public sealed record DisplayState(int Width, int Height, int Hertz, int[] Rates, string DeviceName, string Panel = "未知", bool Active = true);
public static class DisplayControl
{
    public static object[] EnumerateDiagnosticDevices()
    {
        var list = new List<object>();
        for (uint i = 0; i < 64; i++)
        {
            var adapter = NewDevice(); if (!EnumDisplayDevicesW(null, i, ref adapter, 0)) break;
            list.Add(new { adapter = adapter.Name, description = adapter.Description, id = adapter.Id, flags = adapter.Flags });
            for (uint j = 0; j < 16; j++)
            {
                var monitor = NewDevice(); if (!EnumDisplayDevicesW(adapter.Name, j, ref monitor, 1)) break;
                list.Add(new { adapter = adapter.Name, description = monitor.Description, id = monitor.Id, flags = monitor.Flags });
            }
        }
        return list.ToArray();
    }
    internal static string AdapterDescription(string name)
    {
        for(uint i=0;i<64;i++){var adapter=NewDevice();if(!EnumDisplayDevicesW(null,i,ref adapter,0))break;if(adapter.Name.Equals(name,StringComparison.OrdinalIgnoreCase))return adapter.Description;}
        return "";
    }
    private static DisplayDevice NewDevice() => new() { Size = Marshal.SizeOf<DisplayDevice>(), Name = "", Description = "", Id = "", Key = "" };
    private static byte[] Mode(string device, int index)
    {
        var data = new byte[220];
        if (!TryReadMode(device, index, data)) throw new IOException("无法读取内置显示模式。");
        return data;
    }
    private static void InitializeModeBuffer(byte[] data)
    {
        Array.Clear(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(68), (ushort)data.Length);
    }
    private static bool TryReadMode(string device, int index, byte[] data)
    {
        // Same extended enumeration as upstream; includes supported rotated
        // orientations, without EDS_RAWMODE (which ignores panel capability).
        InitializeModeBuffer(data);
        if (EnumDisplaySettingsExW(device, index, data, 4)) return true;
        InitializeModeBuffer(data);
        return EnumDisplaySettingsW(device, index, data);
    }
    public static (int Hertz, string Panel) ReadCurrentRefresh()
    {
        var mode = ReadCurrentMode();
        return (mode.Hertz, mode.Panel.Panel);
    }

    private static (InternalDisplayCandidate Panel, byte[] Buffer, int Width, int Height, int Bits, int Hertz) ReadCurrentMode()
    {
        var selected = DisplayTopology.Internal();
        var buffer = Mode(selected.DeviceName, -1);
        int width = BitConverter.ToInt32(buffer, 172);
        int height = BitConverter.ToInt32(buffer, 176);
        int bits = BitConverter.ToInt32(buffer, 168);
        int hertz = BitConverter.ToInt32(buffer, 184);
        DiagnosticLog.Record("display.mode", new { selected, width, height, bits, hertz });
        if (width <= 0 || height <= 0 || hertz <= 1)
            throw new IOException("Windows未提供有效的当前内置屏显示模式。");
        return (selected, buffer, width, height, bits, hertz);
    }

    public static DisplayState Read()
    {
        var (selected, mode, width, height, bits, hertz) = ReadCurrentMode();
        string device = selected.DeviceName;
        int Get(int offset) => BitConverter.ToInt32(mode, offset);
        var rates = new SortedSet<int>();
        for (int i = 0; i < 1024 && TryReadMode(device, i, mode); i++)
        {
            bool resolution=(Get(172)==width&&Get(176)==height)||(Get(172)==height&&Get(176)==width);
            int rate = Get(184);
            if (resolution && Get(168) == bits && rate > 1) rates.Add(rate);
        }
        if(rates.Count==0)rates.Add(hertz);
        return new(width, height, hertz, rates.ToArray(), device,selected.Panel);
    }
    public static void SetRefresh(int expected, int target) => SetRefresh(expected, target, null);

    public static void SetRefresh(DisplayState expectedPanel, int target) =>
        SetRefresh(expectedPanel.Hertz, target, expectedPanel);

    private static void SetRefresh(int expected, int target, DisplayState? expectedPanel)
    {
        var current = Read();
        if (!current.Active || current.DeviceName.Length == 0) throw new InvalidOperationException("内置屏当前未激活，未更改远程虚拟显示器。");
        if (expectedPanel != null && (current.DeviceName != expectedPanel.DeviceName || current.Panel != expectedPanel.Panel ||
            current.Width != expectedPanel.Width || current.Height != expectedPanel.Height))
            throw new InvalidOperationException("内置屏或当前显示模式已变化，未更改刷新率。");
        if (current.Hertz != expected || !current.Rates.Contains(target)) throw new InvalidOperationException("刷新率已变化，或目标值不受当前屏幕支持。");
        var mode = Mode(current.DeviceName, -1); BitConverter.GetBytes(0x400000).CopyTo(mode, 72); BitConverter.GetBytes(target).CopyTo(mode, 184);
        if (ChangeDisplaySettingsExW(current.DeviceName, mode, IntPtr.Zero, 2, IntPtr.Zero) != 0) throw new IOException("Windows 拒绝此内置屏模式。");
        int result=ChangeDisplaySettingsExW(current.DeviceName, mode, IntPtr.Zero, 0, IntPtr.Zero);var after=Read();
        if(result!=0||after.Hertz!=target||after.DeviceName!=current.DeviceName||after.Panel!=current.Panel)throw new IOException("内置屏刷新率切换未确认成功。Windows结果："+result);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? adapter, uint index, ref DisplayDevice device, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string? device, int mode, [In, Out] byte[] data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsExW(string? device,int mode,[In,Out] byte[] data,uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, [In] byte[] data, IntPtr window, uint flags, IntPtr parameter);
}

public static class ScreenBrightness
{
    private static dynamic Connect()
    {
        var type=Type.GetTypeFromProgID("WbemScripting.SWbemLocator")??throw new IOException("Windows WMI 不可用。");
        dynamic locator=Activator.CreateInstance(type)!;
        try{return locator.ConnectServer(".",@"root\wmi");}finally{Release(locator);}
    }
    private static void Release(object? item){if(item!=null&&Marshal.IsComObject(item))Marshal.FinalReleaseComObject(item);}
    private static List<object> Candidates(dynamic service,string name,string panel)
    {
        if(panel=="未知")throw new IOException("无法确认内置面板标识，未选择亮度通道。");
        var matches=new List<object>();var ids=new List<string>();object? results=null;
        try
        {
            results=service.ExecQuery("SELECT * FROM "+name+" WHERE Active=TRUE");
            foreach(dynamic item in (dynamic)results)
            {
                bool retained=false;
                try{string id=DisplayTopology.PanelCode((string)item.Properties_.Item("InstanceName").Value);ids.Add(id);if(id.Equals(panel,StringComparison.OrdinalIgnoreCase)){matches.Add(item);retained=true;}}
                finally{if(!retained)Release(item);}
            }
            DiagnosticLog.Record("brightness.candidates",new{query=name,panel,candidatePanels=ids,matched=matches.Count});
            return matches;
        }
        catch{foreach(var item in matches)Release(item);throw;}
        finally{Release(results);}
    }
    public static int Read()
    {
        using var trace=DiagnosticLog.Begin("brightness.query");object? service=null;List<object> matches=[];
        try
        {
            string panel=DisplayTopology.Internal().Panel;service=Connect();matches=Candidates((dynamic)service,"WmiMonitorBrightness",panel);
            if(matches.Count!=1)throw new IOException($"匹配的活动内屏亮度通道数量为{matches.Count}，未使用其他屏幕替代。");
            int value=Convert.ToInt32(((dynamic)matches[0]).Properties_.Item("CurrentBrightness").Value);
            if(value is <0 or >100)throw new IOException("亮度返回值无效。");trace.Complete(new{panel,value});return value;
        }
        catch(Exception error){trace.Fail(error);throw;}
        finally{foreach(var item in matches)Release(item);Release(service);}
    }
    public static void Set(int brightness)
    {
        using var trace=DiagnosticLog.Begin("brightness.set",new{target=brightness});
        object? service=null,definition=null,parameters=null,output=null;List<object> matches=[];
        try
        {
            if(brightness is <10 or >100)throw new ArgumentOutOfRangeException(nameof(brightness));
            string panel=DisplayTopology.Internal().Panel;service=Connect();matches=Candidates((dynamic)service,"WmiMonitorBrightnessMethods",panel);
            if(matches.Count!=1)throw new IOException($"匹配的亮度写入通道数量为{matches.Count}，未执行写入。");
            dynamic item=matches[0];definition=item.Methods_.Item("WmiSetBrightness");parameters=((dynamic)definition).InParameters.SpawnInstance_();
            ((dynamic)parameters).Properties_.Item("Timeout").Value=(uint)0;((dynamic)parameters).Properties_.Item("Brightness").Value=(byte)brightness;
            DiagnosticLog.Record("brightness.write",new{panel,target=brightness});output=item.ExecMethod_("WmiSetBrightness",parameters);
            for(int attempt=0;attempt<10;attempt++){Thread.Sleep(100);if(Read()==brightness){trace.Complete(new{panel,actual=brightness,attempt=attempt+1});return;}}
            throw new IOException("亮度回读未确认成功。");
        }
        catch(Exception error){trace.Fail(error);throw;}
        finally{Release(output);Release(parameters);Release(definition);foreach(var item in matches)Release(item);Release(service);}
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenRevo.Core;

public sealed record PowerPlan(Guid Id, string Name) { public override string ToString() => Name; }
public interface IPowerPlans { IReadOnlyList<PowerPlan> List(); Guid Active(); void Set(Guid id); }

public sealed class WindowsPowerPlans : IPowerPlans
{
    public Guid Active()
    {
        uint error = PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer);
        if (error != 0) throw new Win32Exception((int)error);
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }
    public IReadOnlyList<PowerPlan> List()
    {
        var plans = new List<PowerPlan>();
        for (uint index = 0; index < 256; index++)
        {
            uint size = 16;
            uint error = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, index, out Guid guid, ref size);
            if (error == 259) break;
            if (error != 0) throw new Win32Exception((int)error);
            plans.Add(new(guid, ReadName(guid)));
        }
        return plans;
    }
    private static string ReadName(Guid guid)
    {
        uint size = 0;
        uint result = PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if ((result != 0 && result != 234) || size is 0 or > 65536) return guid.ToString();
        byte[] data = new byte[size];
        result = PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, data, ref size);
        return result == 0 ? Encoding.Unicode.GetString(data).TrimEnd('\0') : guid.ToString();
    }
    public void Set(Guid id)
    {
        // Enumerate again at execution time. No imported/custom command text is accepted.
        if (!List().Any(p => p.Id == id)) throw new InvalidOperationException("电源方案已不存在，未执行修改。");
        uint error = PowerSetActiveScheme(IntPtr.Zero, ref id);
        if (error != 0) throw new Win32Exception((int)error);
    }
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup, uint access, uint index, out Guid buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[]? buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}

public sealed class PowerChangeSession(IPowerPlans backend)
{
    public Guid? Previous { get; private set; }
    public Guid? Applied { get; private set; }
    public bool CanUndo => Previous.HasValue && Applied.HasValue;
    public void Apply(Guid expected, Guid requested)
    {
        if (backend.Active() != expected) throw new InvalidOperationException("电源方案已被其他程序改变，请刷新后再试。");
        if (!backend.List().Any(p => p.Id == requested)) throw new InvalidOperationException("目标方案不可用。");
        if (expected == requested) return;
        // Keep recovery information even if readback fails after a successful set.
        backend.Set(requested);
        Previous = expected; Applied = requested;
        if (backend.Active() != requested) throw new InvalidOperationException("修改后的回读不一致，无法确认成功。请检查 Windows 电源设置。");
    }
    public void Undo()
    {
        if (Previous is not Guid previous || Applied is not Guid applied) throw new InvalidOperationException("没有可撤销的操作。");
        if (backend.Active() != applied) throw new InvalidOperationException("电源方案已被其他程序改变，撤销已停止。");
        backend.Set(previous);
        if (backend.Active() != previous) throw new InvalidOperationException("撤销回读失败，请检查 Windows 电源设置。");
        Previous = Applied = null;
    }
}

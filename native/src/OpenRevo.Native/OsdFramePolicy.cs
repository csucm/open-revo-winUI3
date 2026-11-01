using System.Runtime.InteropServices;
namespace OpenRevo.Native;

// Shared with headless Win32 checks. Apply after the framework processes
// WM_STYLECHANGING so it cannot reintroduce its dialog/window edge.
internal static class OsdFramePolicy
{
    internal const uint StyleChanging=0x7C,CalculateClient=0x83;
    internal const uint FrameBits=0x00CF0000,EdgeBits=0x00020301;
    [StructLayout(LayoutKind.Sequential)]internal struct Change{public uint Old,New;}
    internal static uint Style(uint value)=>(value&~FrameBits)|0x80000000;
    internal static uint Extended(uint value,bool review)
        =>(value&~(EdgeBits|0x00040080u))|0x08000020u|(review?0x00040000u:0x00000080u);
    internal static bool Rewrite(int index,nint pointer,bool review)
    {
        if(pointer==0||index is not(-16 or -20))return false;
        var change=Marshal.PtrToStructure<Change>(pointer);
        change.New=index==-16?Style(change.New):Extended(change.New,review);
        Marshal.StructureToPtr(change,pointer,false);return true;
    }
    internal static bool Borderless(uint style,uint extended)=>(style&FrameBits)==0&&(extended&EdgeBits)==0;
}

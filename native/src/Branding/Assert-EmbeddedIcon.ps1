param([Parameter(Mandatory=$true)][string]$Executable, [string]$IconPath = (Join-Path $PSScriptRoot 'App.ico'))
$ErrorActionPreference='Stop'
if (-not ('OpenRevoEmbeddedIconCheck' -as [type])) {
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class OpenRevoEmbeddedIconCheck {
  delegate bool EnumName(IntPtr h, IntPtr t, IntPtr n, IntPtr p);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string f,IntPtr z,uint flags);
  [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr h);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool EnumResourceNames(IntPtr h,IntPtr t,EnumName cb,IntPtr p);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindResource(IntPtr h,IntPtr n,IntPtr t);
  [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr h,IntPtr r);
  [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr h,IntPtr r);
  [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr r);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr h,string n,uint type,int x,int y,uint flags);
  [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
  public static byte[][] Read(string path) {
    IntPtr h=LoadLibraryEx(path,IntPtr.Zero,0x22);
    if(h==IntPtr.Zero) throw new Exception("Cannot load PE resources: "+path);
    var result=new List<byte[]>();
    try {
      EnumName callback=(m,t,n,p)=> { var r=FindResource(m,n,t); var b=new byte[SizeofResource(m,r)]; Marshal.Copy(LockResource(LoadResource(m,r)),b,0,b.Length); result.Add(b); return true; };
      if(!EnumResourceNames(h,new IntPtr(3),callback,IntPtr.Zero)) throw new Exception("Missing embedded icons");
      return result.ToArray();
    } finally { FreeLibrary(h); }
  }
  public static void Load(string path,int size) {
    var h=LoadImage(IntPtr.Zero,path,1,size,size,0x10);
    if(h==IntPtr.Zero) throw new Exception("Cannot load ICO at size "+size);
    DestroyIcon(h);
  }
}
'@

}
$bytes=[IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $IconPath))
$expected=for($i=0;$i -lt [BitConverter]::ToUInt16($bytes,4);$i++) {
    $length=[BitConverter]::ToUInt32($bytes,6+16*$i+8)
    $offset=[BitConverter]::ToUInt32($bytes,6+16*$i+12)
    $frame=[byte[]]::new($length)
    [Array]::Copy($bytes,$offset,$frame,0,$length)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($frame)) } finally { $sha.Dispose() }
}
$actual=@([OpenRevoEmbeddedIconCheck]::Read((Resolve-Path -LiteralPath $Executable)) | ForEach-Object {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($_)) } finally { $sha.Dispose() }
})
if(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)) {
    throw "Embedded icon mismatch: $Executable. Rebuild with src/Branding/App.ico."
}

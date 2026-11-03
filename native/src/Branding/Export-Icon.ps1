param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# User approved exact geometric clipping on 2026-11-02. Preserve RGB pixels.
if (-not ('OpenRevoRoundedMask' -as [type])) {
$drawingReferences = @([Drawing.Bitmap].Assembly.Location,[Drawing.Rectangle].Assembly.Location)
$drawingReferences += @(Get-ChildItem -LiteralPath $PSHOME -Filter 'System.Private.Windows*.dll' | ForEach-Object FullName)
Add-Type -ReferencedAssemblies $drawingReferences @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class OpenRevoRoundedMask {
    public static void Export(string input, string output) {
        using (var original = new Bitmap(input))
        using (var bitmap = original.Clone(new Rectangle(0,0,original.Width,original.Height),PixelFormat.Format32bppArgb)) {
            int w=bitmap.Width,h=bitmap.Height;
            double radius=Math.Min(w,h)*0.18;
            var data=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
            try {
                var bytes=new byte[data.Stride*h]; Marshal.Copy(data.Scan0,bytes,0,bytes.Length);
                for(int y=0;y<h;y++) for(int x=0;x<w;x++) {
                    if((x>=radius && x<w-radius)||(y>=radius && y<h-radius)) continue;
                    int coverage=0;
                    for(int sy=0;sy<8;sy++) for(int sx=0;sx<8;sx++) {
                        double px=x+(sx+0.5)/8,py=y+(sy+0.5)/8;
                        double cx=Math.Max(radius,Math.Min(w-radius,px)),cy=Math.Max(radius,Math.Min(h-radius,py));
                        double dx=px-cx,dy=py-cy;
                        if(dx*dx+dy*dy<=radius*radius) coverage++;
                    }
                    int offset=y*data.Stride+x*4+3;
                    bytes[offset]=(byte)((bytes[offset]*coverage+32)/64);
                }
                Marshal.Copy(bytes,0,data.Scan0,bytes.Length);
            } finally { bitmap.UnlockBits(data); }
            bitmap.Save(output,ImageFormat.Png);
        }
    }
}
'@
}
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'OpenRevo-square-source.png')){
[OpenRevoRoundedMask]::Export((Join-Path $PSScriptRoot 'OpenRevo-square-source.png'),(Join-Path $PSScriptRoot 'OpenRevo.png'))
} # Source distributions can rebuild from the final full-resolution RGBA PNG.
$source = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'OpenRevo.png'))
$sizes = @(16,20,24,32,40,48,64,96,128,256)
$frames = [Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size,$size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source,[Drawing.Rectangle]::new(0,0,$size,$size))
            $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
$output = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
    [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'App.ico'),$output.ToArray())
} finally { $writer.Dispose(); $output.Dispose() }
foreach ($project in @('OpenRevo.Native','OpenRevo.Cpp')) {
    if(Test-Path -LiteralPath (Join-Path $PSScriptRoot "../$project")){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'App.ico') -Destination (Join-Path $PSScriptRoot "../$project/App.ico") -Force
    }
}


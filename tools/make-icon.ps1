param(
  [string]$Out = "$PSScriptRoot/../src/Kanban.Desktop/Assets/kanban.ico"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing.Common
$refs = @(
  [System.Drawing.Graphics].Assembly.Location,
  [System.Drawing.Color].Assembly.Location
)

Add-Type -ReferencedAssemblies $refs -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
public static class IconDrawer {
    static GraphicsPath Rounded(float x, float y, float w, float h, float r) {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
    public static void Draw(Graphics g, float s) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // rounded square background
        using (var bg = Rounded(0, 0, s, s, s * 0.22f))
        using (var b = new SolidBrush(Color.FromArgb(255, 0x25, 0x63, 0xEB)))
            g.FillPath(b, bg);

        // three columns
        float m = s * 0.17f;
        float gap = s * 0.06f;
        float colW = (s - 2 * m - 2 * gap) / 3f;
        float colTop = m;
        float colH = s - 2 * m;
        int[] cards = { 3, 2, 1 };
        for (int i = 0; i < 3; i++) {
            float x = m + i * (colW + gap);
            using (var col = Rounded(x, colTop, colW, colH, s * 0.06f))
            using (var cb = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                g.FillPath(cb, col);

            float cardH = s * 0.11f;
            float cardGap = s * 0.05f;
            float cy = colTop + s * 0.07f;
            for (int c = 0; c < cards[i]; c++) {
                using (var card = Rounded(x + colW * 0.16f, cy, colW * 0.68f, cardH, s * 0.028f)) {
                    Color cc = (i == 0 && c == 0) ? Color.FromArgb(255, 0xF5, 0x9E, 0x0B)
                                                  : Color.FromArgb(255, 0xBF, 0xDB, 0xFE);
                    using (var cb2 = new SolidBrush(cc)) g.FillPath(cb2, card);
                }
                cy += cardH + cardGap;
            }
        }
    }
}
"@

$sizes = @(16, 24, 32, 48, 64, 256)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    [IconDrawer]::Draw($g, [float]$s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += ,$ms.ToArray()
    $ms.Dispose()
}

# assemble .ico (PNG-compressed entries)
$dir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Out))
[System.IO.Directory]::CreateDirectory($dir) | Out-Null
$fs = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Out))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([Byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Output "wrote $([System.IO.Path]::GetFullPath($Out)) ($((Get-Item $Out).Length) bytes, $($sizes.Count) sizes)"

Add-Type -AssemblyName System.Drawing
$master = New-Object System.Drawing.Bitmap 1024,1024,([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($master)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
function P([int]$x,[int]$y) { return (New-Object System.Drawing.Point $x,$y) }
function Face($points, $color) { $b = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($color)); $g.FillPolygon($b,[System.Drawing.Point[]]$points); $b.Dispose() }
Face @((P 512 68),(P 892 278),(P 512 488),(P 132 278)) '#92D8EE'
Face @((P 132 278),(P 512 488),(P 512 940),(P 132 730)) '#2869A4'
Face @((P 892 278),(P 512 488),(P 512 940),(P 892 730)) '#164A79'
$pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#F6FCFF')),18
$pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$g.DrawPolygon($pen,[System.Drawing.Point[]]@((P 512 68),(P 892 278),(P 892 730),(P 512 940),(P 132 730),(P 132 278)))
$g.DrawLines($pen,[System.Drawing.Point[]]@((P 132 278),(P 512 488),(P 892 278)))
$g.DrawLine($pen,512,488,512,940)
$accent = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#51D6C7')),28
$accent.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $accent.EndCap = [System.Drawing.Drawing2D.LineCap]::Round; $accent.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$g.DrawLines($accent,[System.Drawing.Point[]]@((P 238 570),(P 332 625),(P 425 575)))
$accent.Dispose(); $pen.Dispose(); $g.Dispose()
$sizes = @(16,20,24,32,40,48,64,128,256)
$images = @()
foreach ($size in $sizes) {
 $img = New-Object System.Drawing.Bitmap $size,$size,([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
 $canvas = [System.Drawing.Graphics]::FromImage($img); $canvas.Clear([System.Drawing.Color]::Transparent)
 $canvas.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
 $canvas.DrawImage($master,0,0,$size,$size)
 $stream = New-Object System.IO.MemoryStream; $img.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
 $images += ,$stream.ToArray(); $stream.Dispose(); $canvas.Dispose(); $img.Dispose()
}
$path = Join-Path (Get-Location) 'tools\bcf-manager.ico'
$writer = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($path))
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
 $dim = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
 $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
 $offset += $images[$i].Length
}
foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
$writer.Dispose(); $master.Dispose()

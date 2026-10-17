# PickLight 编译脚本（.NET Framework 4.x / WPF）
# 图标方案：从 LinkExtractor.cs 内置的 ICON_BASE64 提取 PNG -> 临时组装 ICO -> 编译嵌入 -> 自动清理
# 项目根目录无需保留任何 .ico 文件；运行 exe 也完全自足（窗口图标 + 文件图标均已嵌入）
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $dir

$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$csPath = Join-Path $dir "LinkExtractor.cs"

# 1. 从源码提取图标 base64（PNG 256）
$cs = [System.IO.File]::ReadAllText($csPath)
$m = [regex]::Match($cs, 'ICON_BASE64 = "([^"]+)"')
if (-not $m.Success) { Write-Host "ERROR: ICON_BASE64 not found in LinkExtractor.cs"; exit 1 }
$png = [Convert]::FromBase64String($m.Groups[1].Value)

# 2. 组装单条目 256 PNG ICO（ICONDIR 6B + ENTRY 16B + PNG 数据）
$ico = Join-Path $env:TEMP ("PickLight_" + [Guid]::NewGuid().ToString("N") + ".ico")
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)          # reserved
$bw.Write([UInt16]1)          # type: icon
$bw.Write([UInt16]1)          # count: 1
$bw.Write([Byte]0)            # width  (0 = 256)
$bw.Write([Byte]0)            # height (0 = 256)
$bw.Write([Byte]0)            # colors
$bw.Write([Byte]0)            # reserved
$bw.Write([UInt16]1)          # planes
$bw.Write([UInt16]32)         # bitcount
$bw.Write([UInt32]$png.Length)# bytes in res
$bw.Write([UInt32]22)         # image offset
$bw.Write($png)
$bw.Flush()
[System.IO.File]::WriteAllBytes($ico, $ms.ToArray())
$bw.Close(); $ms.Close()

# 3. 编译（先结束可能运行的实例，避免文件占用）
Get-Process -Name PickLight, ffplay -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
try {
    & "$fw\csc.exe" /nologo /target:winexe /win32icon:$ico /out:PickLight.exe `
      /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Xml.Linq.dll `
      /r:System.Windows.Forms.dll /r:System.Xaml.dll /r:System.Net.dll /r:System.Drawing.dll `
      /r:$wpf\PresentationCore.dll /r:$wpf\PresentationFramework.dll /r:$wpf\WindowsBase.dll LinkExtractor.cs
    Write-Host ("ExitCode: " + $LASTEXITCODE + "  (0 = OK)")
} finally {
    Remove-Item $ico -Force -ErrorAction SilentlyContinue
}

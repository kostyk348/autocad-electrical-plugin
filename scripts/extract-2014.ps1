# Извлекает managed API DLL AutoCAD 2014 из ISO (для сборки El.Plugin.2014)
# Использование: .\extract-2014.ps1 -Iso "C:\path\to\Autodesk.AutoCAD.2014.SP1.ru-en.x86-x64.iso"
# Результат: refs\2014\ (acmgd.dll, acdbmgd.dll, accoremgd.dll, AcWindows.dll, AdWindows.dll)

param(
    [Parameter(Mandatory = $true)]
    [string]$Iso,
    [string]$OutDir = "..\refs\2014"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $scriptDir $OutDir
New-Item -ItemType Directory -Path $out -Force | Out-Null

# монтируем ISO
$img = Mount-DiskImage -ImagePath $Iso -PassThru
$partition = Get-Partition -DiskNumber $img.Number | Select-Object -First 1
if (-not $partition.DriveLetter) {
    Set-Partition -DiskNumber $img.Number -PartitionNumber $partition.PartitionNumber -NewDriveLetter "Z"
    Start-Sleep -Seconds 2
}
$vol = Get-Volume -DiskImage $img
$drive = $vol.DriveLetter + ":"

Write-Host "ISO смонтирован: $drive"
$cab = "$drive\x64\acad\Data1.cab"
if (-not (Test-Path $cab)) { throw "CAB не найден: $cab (ожидался x64-дистрибутив AutoCAD 2014)" }

$targets = @("RDF_COMP_acmgd.dll", "RDF_COMP_acdbmgd.dll", "RDF_COMP_accoremgd.dll",
             "RDF_COMP_AdWindows.dll", "RDF_COMP_AcWindows.dll")
foreach ($t in $targets) {
    & expand $cab -F:$t $out | Out-Null
    Write-Host "  извлечено: $t"
}
Get-ChildItem $out -Filter "RDF_COMP_*" | ForEach-Object {
    Rename-Item $_.FullName ($_.Name -replace "RDF_COMP_", "") -Force
}

Dismount-DiskImage -ImagePath $Iso | Out-Null
Write-Host "Готово. API 2014: $out"
Write-Host "Сборка: dotnet build src/El.Plugin.2014 -c Release -p:Acad2014Path=`"$out`""

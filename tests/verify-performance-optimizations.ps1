$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot '..\src\TrayContext.cs'
$deviceSourcePath = Join-Path $PSScriptRoot '..\src\Gpw2Device.cs'

if ($null -eq (Select-String -LiteralPath $sourcePath -Pattern 'HotplugDebounceMs = 250')) {
    throw 'Missing 250ms debounce setting'
}
if ($null -eq (Select-String -LiteralPath $sourcePath -Pattern 'Interval = 500')) {
    throw 'Initial poll is not 500ms'
}
if ($null -eq (Select-String -LiteralPath $sourcePath -Pattern '_hotplugDebounceTimer')) {
    throw 'Hotplug debounce timer is missing'
}
if ($null -eq (Select-String -LiteralPath $sourcePath -Pattern 'HotplugDebounce')) {
    throw 'Debounce refresh marker is missing'
}
if ($null -eq (Select-String -LiteralPath $sourcePath -Pattern 'ImmediateStartupRefresh')) {
    throw 'Immediate startup refresh is missing'
}
if ($null -eq (Select-String -LiteralPath $sourcePath -Pattern 'BusyRefreshPolicy')) {
    throw 'Busy refresh policy is missing'
}
if ($null -eq (Select-String -LiteralPath $sourcePath -Pattern 'ReceiverPresenceIntervalSec')) {
    throw 'Receiver presence interval is missing'
}
if ($null -eq (Select-String -LiteralPath $deviceSourcePath -Pattern 'WiredGroupFirst')) {
    throw 'Wired group priority is missing'
}

Write-Output 'Performance optimization regression check passed'

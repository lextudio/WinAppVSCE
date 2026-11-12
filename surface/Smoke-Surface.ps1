#requires -Version 7.0
<#
.SYNOPSIS
  Launches a built Surface.exe with no IDE, performs the Hello/Ready handshake, sends one LoadXaml and checks
  that a PNG frame comes back. Exit code 0 = pass.
.DESCRIPTION
  This is the check that the package is usable by a client other than Visual Studio or VS Code: it speaks the
  line-delimited JSON wire directly (surface/protocol/README.md). The surface must run from a SHORT directory
  (the Windows App SDK bootstrap fails with "filename too long" from deep paths), so copy it first when in doubt.
#>
param(
    [Parameter(Mandatory)][string]$SurfaceExe,
    [string]$Arguments = '',
    [string]$OutPng = (Join-Path ([IO.Path]::GetTempPath()) 'surface-smoke.png'),
    [int]$TimeoutSec = 90
)
$ErrorActionPreference = 'Stop'
$token = -join ((1..16) | ForEach-Object { '{0:x2}' -f (Get-Random -Max 256) })
$psi = [Diagnostics.ProcessStartInfo]::new($SurfaceExe)
$psi.Arguments = $Arguments
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.WorkingDirectory = Split-Path $SurfaceExe
$psi.Environment['SURFACE_AUTH_TOKEN'] = $token
$proc = [Diagnostics.Process]::Start($psi)
$stderr = $proc.StandardError.ReadToEndAsync()
$client = $null
try {
    $port = $null
    while (-not $port) {
        $read = $proc.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($TimeoutSec * 1000)) { throw "No SURFACE_PORT within ${TimeoutSec}s." }
        if ($null -eq $read.Result) { throw "Surface exited before announcing a port (exit $($proc.ExitCode))." }
        if ($read.Result -match '^SURFACE_PORT=(\d+)$') { $port = [int]$Matches[1] }
    }
    $client = [Net.Sockets.TcpClient]::new('127.0.0.1', $port)
    $stream = $client.GetStream()
    $utf8 = [Text.UTF8Encoding]::new($false)
    $reader = [IO.StreamReader]::new($stream, $utf8)
    $writer = [IO.StreamWriter]::new($stream, $utf8)
    $writer.NewLine = "`n"; $writer.AutoFlush = $true
    function Send($message) { $writer.WriteLine(($message | ConvertTo-Json -Compress -Depth 5)) }
    function Receive {
        $line = $reader.ReadLineAsync()
        if (-not $line.Wait($TimeoutSec * 1000)) { throw 'Timed out waiting for a message.' }
        if ($null -eq $line.Result) { throw 'Connection closed.' }
        $line.Result | ConvertFrom-Json
    }
    Send @{ type = 'Hello'; client = 'smoke'; caps = @('frame-stream'); protocol = 1; minProtocol = 1; token = $token }
    $ready = Receive
    if ($ready.type -ne 'Ready') { throw "Expected Ready, got $($ready.type): $($ready.message)" }
    Write-Host "Ready: protocol=$($ready.protocol) caps=$($ready.caps -join ',') wasdk=$($ready.wasdk)"
    if ($ready.caps -notcontains 'frame-stream') { throw 'The surface does not advertise frame-stream.' }
    $xaml = '<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><StackPanel Padding="16" Spacing="8"><TextBlock Text="smoke"/><Button Content="b"/></StackPanel></Page>'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Send @{ type = 'LoadXaml'; xaml = $xaml; width = 480; height = 320; scale = 1.0; sampleData = $false; documentPath = $null }
    while ($true) {
        $message = Receive
        if ($message.type -eq 'Frame') {
            $bytes = [Convert]::FromBase64String($message.data)
            if ($bytes.Length -lt 100 -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50) { throw 'The frame is not a PNG.' }
            [IO.File]::WriteAllBytes($OutPng, $bytes)
            Write-Host ("Frame: {0}x{1}px, {2} bytes, {3} ms -> {4}" -f $message.width, $message.height, $bytes.Length, $watch.ElapsedMilliseconds, $OutPng)
            break
        }
        if ($message.type -eq 'Error') { throw "Surface error ($($message.phase)): $($message.message)" }
    }
    Write-Host 'SMOKE PASS'
}
catch {
    Write-Host "SMOKE FAIL: $($_.Exception.Message)"
    if ($stderr.IsCompleted) { Write-Host $stderr.Result }
    exit 1
}
finally {
    if ($client) { $client.Close() }
    if (-not $proc.HasExited) { $proc.Kill($true) }
}

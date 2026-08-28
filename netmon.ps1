<#
Network Monitor for gaming (Dota 2 / CS2)
Pings every active physical network interface (Ethernet, USB tether, Wi-Fi, ...)
in parallel using source-IP binding, and shows rolling latency/jitter/loss
stats over 30s / 1h / whole session so you can tell which connection is better.
#>

param(
    [string]$Target = '1.1.1.1',
    [int]$IntervalMs = 1000,
    [int]$TimeoutMs = 1000,
    [int]$RediscoverEverySec = 5,
    [int]$MaxIterations = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ---------- interface discovery ----------

function Get-ActiveInterfaces {
    $adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -eq $false }
    $result = @()
    foreach ($a in $adapters) {
        $ip = Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
              Where-Object { $_.IPAddress -notlike '169.254.*' } |
              Select-Object -First 1 -ExpandProperty IPAddress
        if ($ip) {
            $result += [PSCustomObject]@{
                Name        = $a.Name
                Description = $a.InterfaceDescription
                SourceIp    = $ip
                IfIndex     = $a.ifIndex
            }
        }
    }
    return $result
}

# ---------- monitor state ----------

$Monitors = [ordered]@{}

function Sync-Monitors {
    $active = Get-ActiveInterfaces
    $activeNames = @($active | ForEach-Object { $_.Name })

    foreach ($key in @($Monitors.Keys)) {
        if ($activeNames -notcontains $key) { $Monitors.Remove($key) }
    }
    foreach ($a in $active) {
        if (-not $Monitors.Contains($a.Name)) {
            $Monitors[$a.Name] = [PSCustomObject]@{
                Name          = $a.Name
                Description   = $a.Description
                SourceIp      = $a.SourceIp
                IfIndex       = $a.IfIndex
                Samples       = New-Object System.Collections.Generic.List[object]
                TotalAttempts = 0
                TotalSuccess  = 0
                SumRtt        = 0.0
                SumAbsDiff    = 0.0
                DiffCount     = 0
                PrevRtt       = $null
            }
        }
        else {
            $Monitors[$a.Name].SourceIp = $a.SourceIp
            $Monitors[$a.Name].IfIndex = $a.IfIndex
        }
    }
}

function Record-Sample {
    param($Mon, [bool]$Ok, [Nullable[double]]$Rtt, [DateTime]$Time)

    $Mon.Samples.Add([PSCustomObject]@{ T = $Time; Ok = $Ok; Rtt = $Rtt })
    $Mon.TotalAttempts++
    if ($Ok) {
        $Mon.TotalSuccess++
        $Mon.SumRtt += $Rtt
        if ($null -ne $Mon.PrevRtt) {
            $Mon.SumAbsDiff += [Math]::Abs($Rtt - $Mon.PrevRtt)
            $Mon.DiffCount++
        }
        $Mon.PrevRtt = $Rtt
    }

    $cutoff = $Time.AddHours(-1)
    while ($Mon.Samples.Count -gt 0 -and $Mon.Samples[0].T -lt $cutoff) {
        $Mon.Samples.RemoveAt(0)
    }
}

function Get-WindowStats {
    param($Mon, [int]$Seconds, [DateTime]$Now)

    $cutoff = $Now.AddSeconds(-$Seconds)
    $subset = @($Mon.Samples | Where-Object { $_.T -ge $cutoff })
    $attempts = $subset.Count
    $ok = @($subset | Where-Object { $_.Ok })
    $successCount = $ok.Count

    $avg = $null
    $jitter = $null
    if ($successCount -gt 0) {
        $avg = ($ok | Measure-Object -Property Rtt -Average).Average
        $rtts = @($ok | Sort-Object T | ForEach-Object { $_.Rtt })
        $jSum = 0.0; $jCount = 0
        for ($i = 1; $i -lt $rtts.Count; $i++) {
            $jSum += [Math]::Abs($rtts[$i] - $rtts[$i - 1])
            $jCount++
        }
        if ($jCount -gt 0) { $jitter = $jSum / $jCount }
    }
    $loss = if ($attempts -gt 0) { 100.0 * (1 - ($successCount / $attempts)) } else { 0.0 }

    [PSCustomObject]@{ Avg = $avg; Jitter = $jitter; Loss = $loss; Attempts = $attempts }
}

function Get-SessionStats {
    param($Mon)

    $avg = if ($Mon.TotalSuccess -gt 0) { $Mon.SumRtt / $Mon.TotalSuccess } else { $null }
    $jitter = if ($Mon.DiffCount -gt 0) { $Mon.SumAbsDiff / $Mon.DiffCount } else { $null }
    $loss = if ($Mon.TotalAttempts -gt 0) { 100.0 * (1 - ($Mon.TotalSuccess / $Mon.TotalAttempts)) } else { 0.0 }

    [PSCustomObject]@{ Avg = $avg; Jitter = $jitter; Loss = $loss; Attempts = $Mon.TotalAttempts }
}

# ---------- OS routing preference ----------

function Get-OsPreferredInterface {
    param($MonitorsMap)

    $routes = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 -ErrorAction SilentlyContinue
    $bestName = $null
    $bestMetric = [double]::MaxValue

    foreach ($r in $routes) {
        $mon = $MonitorsMap.Values | Where-Object { $_.IfIndex -eq $r.InterfaceIndex } | Select-Object -First 1
        if (-not $mon) { continue }

        $ifMetric = 0
        try {
            $ipIf = Get-NetIPInterface -InterfaceIndex $r.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
            if ($ipIf) { $ifMetric = $ipIf.InterfaceMetric }
        } catch {}

        $effective = [double]$r.RouteMetric + [double]$ifMetric
        if ($effective -lt $bestMetric) {
            $bestMetric = $effective
            $bestName = $mon.Name
        }
    }
    return $bestName
}

# ---------- ping ----------

function Start-PingProcess {
    param([string]$SourceIp, [string]$Target, [int]$TimeoutMs)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'ping.exe'
    $psi.Arguments = "-n 1 -w $TimeoutMs -S $SourceIp $Target"
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    [void]$p.Start()
    return $p
}

function Parse-PingOutput {
    param([string]$Text)

    if ($Text -match 'time[=<](\d+)ms') {
        $t = [double]$Matches[1]
        if ($Text -match 'time<1ms') { $t = 0.5 }
        return @{ Ok = $true; Rtt = $t }
    }
    return @{ Ok = $false; Rtt = $null }
}

# ---------- rendering ----------

function Format-Ms {
    param($v)
    if ($null -eq $v) { return '  -- ' }
    return ('{0,5:N1}' -f $v)
}

function Color-For-Latency {
    param($v)
    if ($null -eq $v) { return 'DarkGray' }
    if ($v -lt 40) { return 'Green' }
    if ($v -lt 80) { return 'Yellow' }
    return 'Red'
}

function Write-Row {
    param([string]$Name, [string]$Desc, $Now, $S30, $H1, $Sess, [bool]$Best, [bool]$IsOsPreferred)

    $marker = if ($Best) { '*' } else { ' ' }
    $displayName = if ($IsOsPreferred) { "$Name [OS]" } else { $Name }
    $label = "{0}{1,-19}" -f $marker, $displayName
    Write-Host $label -NoNewline -ForegroundColor ($(if ($Best) { 'White' } else { 'Gray' }))

    $descTrim = if ($Desc.Length -gt 32) { $Desc.Substring(0, 29) + '...' } else { $Desc }
    Write-Host (" {0,-32}" -f $descTrim) -NoNewline -ForegroundColor DarkGray

    Write-Host (" {0}ms" -f (Format-Ms $Now.Avg)) -NoNewline -ForegroundColor (Color-For-Latency $Now.Avg)

    Write-Host ("   {0}/{1}ms" -f (Format-Ms $S30.Avg), (Format-Ms $S30.Jitter)) -NoNewline -ForegroundColor (Color-For-Latency $S30.Avg)
    Write-Host ("  {0,4:N0}%" -f $S30.Loss) -NoNewline -ForegroundColor $(if ($S30.Loss -gt 0) { 'Red' } else { 'DarkGray' })

    Write-Host ("   {0}/{1}ms" -f (Format-Ms $H1.Avg), (Format-Ms $H1.Jitter)) -NoNewline -ForegroundColor (Color-For-Latency $H1.Avg)
    Write-Host ("  {0,4:N0}%" -f $H1.Loss) -NoNewline -ForegroundColor $(if ($H1.Loss -gt 0) { 'Red' } else { 'DarkGray' })

    Write-Host ("   {0}/{1}ms" -f (Format-Ms $Sess.Avg), (Format-Ms $Sess.Jitter)) -NoNewline -ForegroundColor (Color-For-Latency $Sess.Avg)
    Write-Host ("  {0,4:N0}%" -f $Sess.Loss) -ForegroundColor $(if ($Sess.Loss -gt 0) { 'Red' } else { 'DarkGray' })
}

function Render {
    param([DateTime]$Now, [DateTime]$Start, [switch]$Final)

    try { Clear-Host } catch {}
    $elapsed = $Now - $Start
    $title = if ($Final) { 'Network Monitor - FINAL SUMMARY' } else { 'Network Monitor' }
    Write-Host "$title   target: $Target   session: $($elapsed.ToString('hh\:mm\:ss'))   (Ctrl+C to stop)" -ForegroundColor Cyan
    Write-Host ""

    if ($Monitors.Count -eq 0) {
        Write-Host "No active network interfaces found." -ForegroundColor Red
        return
    }

    # figure out the currently-best interface (lowest session avg+jitter combo) once we have data
    $best = $null
    $bestScore = [double]::MaxValue
    foreach ($mon in $Monitors.Values) {
        $s = Get-SessionStats $mon
        if ($null -ne $s.Avg) {
            $jitterVal = if ($null -eq $s.Jitter) { 0 } else { $s.Jitter }
            $score = $s.Avg + (2 * $jitterVal) + (10 * $s.Loss)
            if ($score -lt $bestScore) { $bestScore = $score; $best = $mon.Name }
        }
    }

    $osPreferred = Get-OsPreferredInterface -MonitorsMap $Monitors

    $header = "{0,-20} {1,-32} {2,8}   {3,-16}{4,6}   {5,-16}{6,6}   {7,-16}{8,6}" -f `
        'Interface', 'Adapter', 'Now', '30s avg/jit', 'loss', '1h avg/jit', 'loss', 'session avg/jit', 'loss'
    Write-Host $header -ForegroundColor White
    Write-Host ('-' * $header.Length) -ForegroundColor DarkGray

    foreach ($mon in $Monitors.Values) {
        $nowStat = Get-WindowStats -Mon $mon -Seconds 3 -Now $Now
        $s30 = Get-WindowStats -Mon $mon -Seconds 30 -Now $Now
        $h1 = Get-WindowStats -Mon $mon -Seconds 3600 -Now $Now
        $sess = Get-SessionStats $mon
        Write-Row -Name $mon.Name -Desc $mon.Description -Now $nowStat -S30 $s30 -H1 $h1 -Sess $sess `
            -Best ($mon.Name -eq $best) -IsOsPreferred ($mon.Name -eq $osPreferred)
    }

    Write-Host ""
    Write-Host "* = currently the better connection (lower session avg/jitter/loss)    [OS] = the connection Windows is currently routing internet traffic through" -ForegroundColor DarkGray
}

# ---------- main loop ----------

$startTime = Get-Date
$lastDiscovery = $startTime
Sync-Monitors

if ($Monitors.Count -eq 0) {
    Write-Host "No active, non-virtual network adapters found. Connect Ethernet / hotspot and re-run." -ForegroundColor Red
    exit 1
}

try {
    $iteration = 0
    while ($true) {
        $iteration++
        $tickStart = Get-Date

        if (((Get-Date) - $lastDiscovery).TotalSeconds -ge $RediscoverEverySec) {
            Sync-Monitors
            $lastDiscovery = Get-Date
        }

        $procs = @{}
        foreach ($name in @($Monitors.Keys)) {
            $mon = $Monitors[$name]
            try {
                $procs[$name] = Start-PingProcess -SourceIp $mon.SourceIp -Target $Target -TimeoutMs $TimeoutMs
            } catch {
                $procs[$name] = $null
            }
        }

        $deadline = $tickStart.AddMilliseconds($TimeoutMs + 300)
        foreach ($kv in $procs.GetEnumerator()) {
            if ($null -eq $kv.Value) { continue }
            $remain = [Math]::Max(50, [int](($deadline - (Get-Date)).TotalMilliseconds))
            [void]$kv.Value.WaitForExit($remain)
        }

        $sampleTime = Get-Date
        foreach ($kv in $procs.GetEnumerator()) {
            $name = $kv.Key
            $p = $kv.Value
            if (-not $Monitors.Contains($name)) {
                if ($null -ne $p) { try { if (-not $p.HasExited) { $p.Kill() } } catch {}; $p.Dispose() }
                continue
            }
            $mon = $Monitors[$name]
            if ($null -eq $p) {
                Record-Sample -Mon $mon -Ok $false -Rtt $null -Time $sampleTime
                continue
            }
            if (-not $p.HasExited) {
                try { $p.Kill() } catch {}
                Record-Sample -Mon $mon -Ok $false -Rtt $null -Time $sampleTime
            }
            else {
                $out = $p.StandardOutput.ReadToEnd()
                $r = Parse-PingOutput -Text $out
                Record-Sample -Mon $mon -Ok $r.Ok -Rtt $r.Rtt -Time $sampleTime
            }
            $p.Dispose()
        }

        Render -Now $sampleTime -Start $startTime

        $elapsedMs = ((Get-Date) - $tickStart).TotalMilliseconds
        $sleepMs = $IntervalMs - $elapsedMs
        if ($sleepMs -gt 0) { Start-Sleep -Milliseconds $sleepMs }

        if ($MaxIterations -gt 0 -and $iteration -ge $MaxIterations) { break }
    }
}
finally {
    Render -Now (Get-Date) -Start $startTime -Final
}

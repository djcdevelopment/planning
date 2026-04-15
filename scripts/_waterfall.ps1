param(
    [string]$service = 'Farmer',
    [int]$limit = 3,
    [string]$operationContains = 'trigger|workflow'
)
$traces = (Invoke-RestMethod "http://localhost:16686/api/traces?service=$service&limit=$limit&lookback=10m").data
foreach ($t in $traces | Sort-Object -Property @{e={$_.spans[0].startTime}} -Descending) {
    $ops = ($t.spans | Select-Object -ExpandProperty operationName | Sort-Object -Unique) -join ', '
    if ($ops -notmatch $operationContains) { continue }
    Write-Host ""
    Write-Host ("traceID: " + $t.traceID) -ForegroundColor Cyan
    Write-Host ("spans:   " + $t.spans.Count)
    $procs = $t.processes
    $min = ($t.spans | Measure-Object startTime -Minimum).Minimum
    $t.spans | Sort-Object startTime | ForEach-Object {
        $svc = $procs.($_.processID).serviceName
        $off = [int](($_.startTime - $min) / 1000)
        $dur = [int]($_.duration / 1000)
        "  {0,6}ms  +{1,5}ms  {2,-12}  {3}" -f $dur, $off, $svc, $_.operationName
    }
}

param([Parameter(Mandatory)][string]$traceId)
$t = Invoke-RestMethod "http://localhost:16686/api/traces/$traceId"
if (-not $t.data -or $t.data.Count -eq 0) { Write-Host "no trace found"; return }
$spans = $t.data[0].spans
$procs = $t.data[0].processes
$minStart = ($spans | Measure-Object startTime -Minimum).Minimum
Write-Host ("spans: {0}" -f $spans.Count)
Write-Host ""
Write-Host "  duration   offset   service                   operation"
$spans | Sort-Object startTime | ForEach-Object {
    $svc = $procs.($_.processID).serviceName
    $off = [int](($_.startTime - $minStart) / 1000)
    $dur = [int]($_.duration / 1000)
    "  {0,6} ms  +{1,5}ms  {2,-24}  {3}" -f $dur, $off, $svc, $_.operationName
}

# Find the most recent /trigger trace (not /health), print Dispatch + Collect + worker.prompt waterfall.
param([int]$LookbackMin = 10)
$traces = (Invoke-RestMethod "http://localhost:16686/api/traces?service=Farmer&operation=POST%20%2Ftrigger&limit=10&lookback=${LookbackMin}m").data
if (-not $traces) {
    Write-Host "No POST /trigger traces in the last $LookbackMin minutes." -ForegroundColor Yellow
    return
}

$t = $traces | Sort-Object @{e={$_.spans[0].startTime}} -Descending | Select-Object -First 1
Write-Host ""
Write-Host ("traceID: " + $t.traceID) -ForegroundColor Cyan
Write-Host ("Jaeger:  http://localhost:16686/trace/" + $t.traceID)
Write-Host ("total spans: " + $t.spans.Count)
Write-Host ""
Write-Host "Dispatch + Collect + worker.prompt children (sorted by start time):"

$ofInterest = $t.spans | Where-Object { $_.operationName -in @('workflow.stage.Dispatch', 'workflow.stage.Collect', 'worker.prompt') }
if (-not $ofInterest) {
    Write-Host "  (no spans of interest in this trace)" -ForegroundColor Yellow
    return
}
$minStart = ($ofInterest | Measure-Object startTime -Minimum).Minimum
$ofInterest | Sort-Object startTime | ForEach-Object {
    $tags = @{}
    foreach ($tag in $_.tags) { $tags[$tag.key] = $tag.value }
    $off = [int](($_.startTime - $minStart) / 1000)
    $dur = [int]($_.duration / 1000)
    $extra = ""
    if ($_.operationName -eq 'worker.prompt') {
        $extra = " idx=$($tags['farmer.prompt_index']) file=$($tags['farmer.prompt_filename']) exit=$($tags['farmer.exit_code'])"
    }
    "  {0,7} ms  +{1,6}ms  {2,-26}{3}" -f $dur, $off, $_.operationName, $extra
}

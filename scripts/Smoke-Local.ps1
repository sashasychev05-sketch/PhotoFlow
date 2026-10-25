$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectWork = [IO.Path]::GetFullPath((Join-Path $repoRoot '..\work'))
$smokeRoot = Join-Path $projectWork ('smoke-' + [Guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $smokeRoot 'source'
$portableDotnet = Join-Path $projectWork 'dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $portableDotnet) { $portableDotnet } else { 'dotnet' }
$probeDll = Join-Path $repoRoot 'src\PhotoFlow.Probe\bin\Release\net10.0\PhotoFlow.Probe.dll'
if (-not (Test-Path -LiteralPath $probeDll)) { throw 'Build Release first.' }
New-Item -ItemType Directory -Path "$sourceRoot\summer", "$sourceRoot\winter" -Force | Out-Null
$pngBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jXioAAAAASUVORK5CYII=')
[IO.File]::WriteAllBytes("$sourceRoot\summer\test.png", $pngBytes)
[IO.File]::WriteAllBytes("$sourceRoot\winter\test.dng", [byte[]](1, 2, 3)) # Extension-only synthetic fixture, not a decodable RAW.
[IO.File]::WriteAllText("$sourceRoot\notes.txt", 'Not media.')
$beforeHash = (Get-FileHash -LiteralPath "$sourceRoot\summer\test.png" -Algorithm SHA256).Hash
& $dotnetExe $probeDll scan --folder $sourceRoot --output "$smokeRoot\scan.ndjson" 2> "$smokeRoot\scan.log"
if ($LASTEXITCODE -ne 0) { throw 'scan failed' }
$records = Get-Content -LiteralPath "$smokeRoot\scan.ndjson" | ForEach-Object { $_ | ConvertFrom-Json }
if (($records | Where-Object type -eq 'media').Count -ne 2) { throw 'Incorrect media count.' }
& $dotnetExe $probeDll scan --folder $sourceRoot --limit 1 --output "$smokeRoot\limited.ndjson" 2> "$smokeRoot\limited.log"
if ($LASTEXITCODE -ne 0) { throw 'Limited scan failed.' }
$limited = Get-Content -LiteralPath "$smokeRoot\limited.ndjson" | ForEach-Object { $_ | ConvertFrom-Json }
if (-not ($limited | Where-Object type -eq 'summary').stoppedAtLimit) { throw 'Missing limit marker.' }
& $dotnetExe $probeDll copy --folder $sourceRoot --key 'summer/test.png' --output "$smokeRoot\import\test.png" > "$smokeRoot\copy.json" 2> "$smokeRoot\copy.log"
if ($LASTEXITCODE -ne 0) { throw 'copy failed' }
if ((Get-FileHash -LiteralPath "$smokeRoot\import\test.png" -Algorithm SHA256).Hash -ne $beforeHash) { throw 'Hash mismatch.' }
if ((Get-FileHash -LiteralPath "$sourceRoot\summer\test.png" -Algorithm SHA256).Hash -ne $beforeHash) { throw 'Source changed.' }
& $dotnetExe $probeDll copy --folder $sourceRoot --key 'summer/test.png' --output "$smokeRoot\import\test.png" > "$smokeRoot\conflict.json" 2> "$smokeRoot\conflict.log"
if ($LASTEXITCODE -ne 2) { throw 'Existing destination was not rejected.' }
& $dotnetExe $probeDll thumbnail --folder $sourceRoot --key 'summer/test.png' --output "$smokeRoot\thumb.bin" > "$smokeRoot\thumbnail.json" 2> "$smokeRoot\thumbnail.log"
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath "$smokeRoot\thumb.bin")) { throw 'Strict thumbnail policy failed.' }
if (Get-ChildItem -LiteralPath $smokeRoot -Recurse -Filter '*.part') { throw 'Unexpected part file.' }
@{ status = 'passed'; checks = 6; root = $smokeRoot } | ConvertTo-Json

# Zephyr quality gate for Windows PowerShell: same steps, same order and same output as scripts/check (see its header).
# Usage: powershell -File scripts/check.ps1   (exit code 1 if any step failed)
$ErrorActionPreference = 'Continue'
Set-StrictMode -Version Latest

$SpectralVersion = '6.15.0'
$OpenApiFile = 'docs/api/openapi.yaml'
$OpenApiGenerator = 'tools/openapi-from-metadata/generate.mjs'
$ApiUrl = 'http://127.0.0.1:18080'
$Steps = @('lint', 'tests', 'seeds', 'openapi-regenerate', 'spectral', 'oasdiff', 'schemathesis', 'vulnerabilities')
$Results = New-Object System.Collections.Generic.List[object]

# A step returns $true (pass), $false (fail) or Skip (kind: na | tool | keycloak).
function Skip([string]$Kind, [string]$Note) { return [pscustomobject]@{ Kind = $Kind; Note = $Note } }

function Invoke-Native([string]$File, [string[]]$CmdArgs = @()) {
    & $File @CmdArgs | Out-Host
    return ($LASTEXITCODE -eq 0)
}

function Get-EnvValue([string]$Key) {
    $line = Get-Content .env | Where-Object { $_ -like "$Key=*" } | Select-Object -First 1
    if ($line) { return $line.Substring($Key.Length + 1).Trim() }
    return $null
}

function Test-NpmScript([string]$Dir, [string]$Name) {
    $scripts = (Get-Content (Join-Path $Dir 'package.json') -Raw | ConvertFrom-Json).scripts
    return ($null -ne $scripts) -and ($null -ne $scripts.PSObject.Properties[$Name])
}

function Install-Deps([string]$Dir) {
    if (Test-Path (Join-Path $Dir 'node_modules')) { return $true }
    $command = 'install'
    if (Test-Path (Join-Path $Dir 'package-lock.json')) { $command = 'ci' }
    return (Invoke-Native npm @($command, '--prefix', $Dir))
}

# Sets ZEPHYR_TEST_DB. Locally the Compose db service is started and awaited; in CI the workflow's service container is used.
function Initialize-TestDb {
    if ($env:ZEPHYR_TEST_DB) { return $true }
    if (-not (Test-Path .env)) { if (-not (Invoke-Native node @('scripts/setup-env.mjs'))) { return $false } }
    if ($env:CI -ne 'true') {
        if (-not (Invoke-Native docker @('compose', 'up', '-d', '--wait', 'db'))) {
            Write-Host 'Could not start the Compose db service. Start Docker, or set ZEPHYR_TEST_DB.'
            return $false
        }
    }
    $db = Get-EnvValue 'ZEPHYR_DB'
    if (-not $db) { Write-Host 'ZEPHYR_DB is missing in .env.'; return $false }
    $env:ZEPHYR_TEST_DB = $db -replace 'Host=db;', 'Host=localhost;'
    return $true
}

# 1. Lint/type
function Step-lint {
    $ok = Install-Deps .
    if (-not $ok) { return $false }
    if (-not (Invoke-Native npm @('run', 'typecheck', '--silent'))) { $ok = $false }
    if (-not (Invoke-Native npm @('run', 'lint', '--silent'))) { $ok = $false }
    foreach ($script in @('lint', 'typecheck')) {
        if (Test-NpmScript 'src/frontend' $script) {
            if (-not ((Install-Deps 'src/frontend') -and (Invoke-Native npm @('--prefix', 'src/frontend', 'run', $script, '--silent')))) { $ok = $false }
        } else {
            Write-Host "frontend: no '$script' script yet, nothing to run"
        }
    }
    return $ok
}

# 2. Tests
function Step-tests {
    if (-not (Install-Deps .)) { return $false }
    if (-not (Initialize-TestDb)) { return $false }
    return (Invoke-Native npm @('test', '--silent'))
}

# 3. Seeds
function Step-seeds {
    $python = Get-Command python3, python -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $python) { return (Skip 'tool' 'python is not installed') }
    return (Invoke-Native $python.Source @('scripts/validate_seeds.py'))
}

# 4. OpenAPI regenerate: the generator writes a temporary file; any difference from the committed file fails (line ends ignored).
function Step-openapi-regenerate {
    if (-not (Test-Path $OpenApiGenerator)) { return (Skip 'na' "not yet applicable: no generator at $OpenApiGenerator") }
    $generated = New-TemporaryFile
    try {
        if (-not (Invoke-Native node @($OpenApiGenerator, '--out', $generated.FullName))) { return $false }
        $committedText = ''
        if (Test-Path $OpenApiFile) { $committedText = ((Get-Content $OpenApiFile -Raw) -replace "`r`n", "`n") }
        $generatedText = ((Get-Content $generated.FullName -Raw) -replace "`r`n", "`n")
        if ($committedText -cne $generatedText) {
            Write-Host "$OpenApiFile is not what the generator produces: run generate (node $OpenApiGenerator) and commit the result. Never edit the file by hand."
            return $false
        }
        return $true
    } finally {
        Remove-Item $generated -ErrorAction SilentlyContinue
    }
}

# 5. Spectral
function Step-spectral {
    if (-not (Test-Path $OpenApiFile)) { return (Skip 'na' "not yet applicable: no $OpenApiFile") }
    return (Invoke-Native npx @('--yes', "@stoplight/spectral-cli@$SpectralVersion", 'lint', $OpenApiFile))
}

# 6. oasdiff
function Step-oasdiff {
    if (-not (Test-Path $OpenApiFile)) { return (Skip 'na' "not yet applicable: no $OpenApiFile") }
    $base = New-TemporaryFile
    try {
        $found = $false
        foreach ($ref in @('origin/main', 'main')) {
            $content = git show "${ref}:$OpenApiFile" 2>$null
            if ($LASTEXITCODE -eq 0) { Set-Content -Path $base -Value $content; $found = $true; break }
        }
        if (-not $found) { return (Skip 'na' "not yet applicable: main has no $OpenApiFile") }
        if (-not (Get-Command oasdiff -ErrorAction SilentlyContinue)) { return (Skip 'tool' 'oasdiff is not installed') }
        return (Invoke-Native oasdiff @('breaking', $base.FullName, $OpenApiFile, '--fail-on', 'ERR'))
    } finally {
        Remove-Item $base -ErrorAction SilentlyContinue
    }
}

# 7. Schemathesis
function Step-schemathesis {
    if (-not (Test-Path $OpenApiFile)) { return (Skip 'na' "not yet applicable: no $OpenApiFile") }
    if ((Select-String -Path $OpenApiFile -Pattern '^  securitySchemes:' -Quiet) -and -not $env:ZEPHYR_CHECK_TOKEN) {
        return (Skip 'keycloak' 'needs Keycloak')
    }
    if (-not (Get-Command schemathesis -ErrorAction SilentlyContinue)) { return (Skip 'tool' 'schemathesis is not installed') }
    if (-not (Initialize-TestDb)) { return $false }
    $headers = @()
    if ($env:ZEPHYR_CHECK_TOKEN) { $headers = @('-H', "Authorization: Bearer $env:ZEPHYR_CHECK_TOKEN") }
    $previous = @{ Db = $env:ZEPHYR_DB; Urls = $env:ASPNETCORE_URLS }
    # A dedicated database, so fuzzing never touches the development data.
    $env:ZEPHYR_DB = $env:ZEPHYR_TEST_DB -replace 'Database=[^;]*', 'Database=zephyr_check'
    $env:ASPNETCORE_URLS = $ApiUrl
    $api = Start-Process dotnet -ArgumentList @('run', '--project', 'src/backend/Zephyr.Api', '--no-launch-profile') -PassThru -NoNewWindow
    $env:ZEPHYR_DB = $previous.Db
    $env:ASPNETCORE_URLS = $previous.Urls
    try {
        $healthy = $false
        for ($i = 0; $i -lt 60 -and -not $api.HasExited -and -not $healthy; $i++) {
            try { $healthy = (Invoke-WebRequest "$ApiUrl/health" -UseBasicParsing).StatusCode -eq 200 } catch { Start-Sleep -Seconds 2 }
        }
        if (-not $healthy) { Write-Host 'The API did not become healthy.'; return $false }
        return (Invoke-Native schemathesis (@('run', $OpenApiFile, '--url', $ApiUrl) + $headers))
    } finally {
        if (-not $api.HasExited) { taskkill /PID $api.Id /T /F | Out-Null }
    }
}

# 8. Vulnerabilities (dotnet list exits 0 even when it finds some, so its output is checked)
function Step-vulnerabilities {
    $ok = $true
    $out = & dotnet list src/backend/Zephyr.Api.Tests package --vulnerable --include-transitive 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { $ok = $false }
    Write-Host $out
    if ($out -match '(?i)has the following vulnerable packages') { $ok = $false }
    foreach ($dir in @('.', 'src/frontend')) {
        if (Test-Path (Join-Path $dir 'package-lock.json')) {
            Push-Location $dir
            try { if (-not (Invoke-Native npm @('audit', '--audit-level=high'))) { $ok = $false } } finally { Pop-Location }
        } else {
            Write-Host "${dir}: no package-lock.json yet, npm audit not run"
        }
    }
    return $ok
}

function Add-Result([string]$Status, [string]$Name, [string]$Note) {
    $Results.Add([pscustomobject]@{ Status = $Status; Name = $Name; Note = $Note })
    if ($Note) { Write-Host "$Status $Name ($Note)" } else { Write-Host "$Status $Name" }
}

Set-Location (Split-Path -Parent $PSScriptRoot)
if ($env:CI -eq 'true') { $env:ZEPHYR_REQUIRE_DB = '1' }

foreach ($name in $Steps) {
    Write-Host ''
    Write-Host "=== $name"
    $result = @(& "Step-$name") | Select-Object -Last 1
    if ($result -is [bool] -and $result) {
        Add-Result 'PASS' $name ''
    } elseif ($result -is [bool]) {
        Add-Result 'FAIL' $name ''
    } elseif ($result.Kind -eq 'tool' -and $env:REQUIRE_ALL -eq '1') {
        Add-Result 'FAIL' $name "REQUIRE_ALL=1 and the step was skipped: $($result.Note)"
    } else {
        Add-Result 'SKIP' $name $result.Note
    }
}

Write-Host ''
Write-Host '=== Summary'
foreach ($r in $Results) { Write-Host ('{0,-5} {1,-20} {2}' -f $r.Status, $r.Name, $r.Note) }
$failed = @($Results | Where-Object { $_.Status -eq 'FAIL' }).Count
$skipped = @($Results | Where-Object { $_.Status -eq 'SKIP' }).Count
Write-Host ''
if ($failed -gt 0) {
    Write-Host "FAILED: $failed of $($Results.Count) steps failed ($skipped skipped)."
    exit 1
}
Write-Host "OK: no step failed ($skipped skipped)."

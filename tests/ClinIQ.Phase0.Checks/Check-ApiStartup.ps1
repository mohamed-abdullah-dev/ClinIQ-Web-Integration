$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$checkRoot = Join-Path $repositoryRoot '.artifacts/phase0'
New-Item -ItemType Directory -Path $checkRoot -Force | Out-Null
$apiRoot = Join-Path $repositoryRoot 'src/ClinIQ.API'
$apiDll = Join-Path $apiRoot 'bin/Debug/net10.0/ClinIQ.API.dll'
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$apiProcess = $null
try {
    $apiProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @(('"' + $apiDll + '"'), '--urls', 'http://127.0.0.1:0') -WorkingDirectory $apiRoot -RedirectStandardOutput (Join-Path $checkRoot 'startup.log') -RedirectStandardError (Join-Path $checkRoot 'startup-errors.log') -WindowStyle Hidden -PassThru
    $apiUrl = $null
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($apiProcess.HasExited) { throw 'API exited before startup.' }
        $startupText = Get-Content -Raw -LiteralPath (Join-Path $checkRoot 'startup.log') -ErrorAction SilentlyContinue
        if ($startupText -match 'Now listening on: (http://127\.0\.0\.1:\d+)') { $apiUrl = $Matches[1]; break }
    }
    if (-not $apiUrl) { throw 'API startup timed out.' }
    $openApiResponse = Invoke-WebRequest -Uri ($apiUrl + '/openapi/v1.json')
    $openApiDocument = $openApiResponse.Content | ConvertFrom-Json
    if ($openApiResponse.StatusCode -ne 200 -or -not $openApiDocument.openapi) { throw 'OpenAPI check failed.' }
    $notFoundResponse = Invoke-WebRequest -Uri ($apiUrl + '/phase0-nonexistent') -SkipHttpErrorCheck
    # PowerShell may classify application/problem+json as bytes rather than text.
    $notFoundJson = if ($notFoundResponse.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($notFoundResponse.Content)
    } else { [string]$notFoundResponse.Content }
    $notFoundProblem = $notFoundJson | ConvertFrom-Json
    if ($notFoundResponse.StatusCode -ne 404 -or $notFoundProblem.status -ne 404 -or -not $notFoundProblem.traceId) { throw '404 ProblemDetails check failed.' }
    [pscustomobject]@{ OpenApiStatus = $openApiResponse.StatusCode; OpenApiVersion = $openApiDocument.openapi; MissingRouteStatus = $notFoundResponse.StatusCode; TraceIdIncluded = [bool]$notFoundProblem.traceId; DatabaseAccessAttempted = $false } | ConvertTo-Json
} finally {
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) { Stop-Process -Id $apiProcess.Id; $apiProcess.WaitForExit() }
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
}

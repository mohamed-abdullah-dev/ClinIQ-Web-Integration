param(
    [string]$ConnectionString = 'Server=.\SQLEXPRESS;Database=ClinIQDb;Integrated Security=True;TrustServerCertificate=True',
    [int]$ExpectedCount = 10,
    [ValidateSet(200, 500)][int]$ExpectedStatus = 200
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$apiRoot = Join-Path $repositoryRoot 'src/ClinIQ.API'
$apiDll = Join-Path $apiRoot 'bin/Debug/net10.0/ClinIQ.API.dll'
$probeRoot = Join-Path $repositoryRoot ('.artifacts/specialties-http/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
$apiProcess = $null

function Read-ResponseText($response) {
    if ($response.Content -is [byte[]]) { return [Text.Encoding]::UTF8.GetString($response.Content) }
    return [string]$response.Content
}

try {
    $arguments = @(
        ('"' + $apiDll + '"'), '--environment', 'Development',
        '--urls', 'http://127.0.0.1:0', '--ConnectionStrings:DefaultConnection',
        ('"' + $ConnectionString.Replace('"', '\"') + '"')
    )
    $apiProcess = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $arguments -WorkingDirectory $apiRoot `
        -RedirectStandardOutput (Join-Path $probeRoot 'api.log') `
        -RedirectStandardError (Join-Path $probeRoot 'api-errors.log') -WindowStyle Hidden -PassThru

    $apiUrl = $null
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($apiProcess.HasExited) { throw "API exited before startup. Logs: $probeRoot" }
        $startupText = Get-Content -LiteralPath (Join-Path $probeRoot 'api.log') -Raw -ErrorAction SilentlyContinue
        if ($startupText -match 'Now listening on: (http://127\.0\.0\.1:\d+)') {
            $apiUrl = $Matches[1]
            break
        }
    }
    if (-not $apiUrl) { throw "API startup timed out. Logs: $probeRoot" }

    $response = Invoke-WebRequest -Uri ($apiUrl + '/api/specialties') -SkipHttpErrorCheck -TimeoutSec 45
    $body = Read-ResponseText $response
    if ($response.StatusCode -ne $ExpectedStatus) { throw "Unexpected HTTP status: $($response.StatusCode). Logs: $probeRoot" }

    if ($ExpectedStatus -eq 200) {
        $items = ConvertFrom-Json -InputObject $body -NoEnumerate
        if ($items -isnot [Array] -or $items.Count -ne $ExpectedCount) { throw 'Expected a JSON array with the configured count.' }
        foreach ($item in $items) {
            $fields = @($item.PSObject.Properties.Name | Sort-Object)
            if (($fields -join ',') -cne 'id,nameAr,nameEn') { throw 'Unexpected DTO fields.' }
            $parsedId = [Guid]::Empty
            if (-not [Guid]::TryParse([string]$item.id, [ref]$parsedId) -or $parsedId -eq [Guid]::Empty) { throw 'Invalid specialty ID.' }
            if ([string]::IsNullOrWhiteSpace($item.nameAr) -or [string]::IsNullOrWhiteSpace($item.nameEn)) { throw 'Missing display name.' }
        }
        if (@($items.id | Select-Object -Unique).Count -ne $items.Count) { throw 'Repeated specialty IDs.' }
        for ($index = 1; $index -lt $items.Count; $index++) {
            if ([StringComparer]::Ordinal.Compare($items[$index - 1].nameEn.ToUpperInvariant(), $items[$index].nameEn.ToUpperInvariant()) -gt 0) {
                throw 'Unstable specialty ordering.'
            }
        }
        $repeated = Invoke-WebRequest -Uri ($apiUrl + '/api/specialties') -TimeoutSec 45
        if ((Read-ResponseText $repeated) -cne $body) { throw 'Repeated GET changed the JSON response.' }
        Set-Content -LiteralPath (Join-Path $probeRoot 'response.json') -Value $body -Encoding utf8
    } else {
        $problem = ConvertFrom-Json -InputObject $body
        if ($problem.status -ne 500 -or $problem.title -cne 'Server.Error' -or
            $problem.detail -cne 'An unexpected error occurred.' -or -not $problem.traceId) {
            throw 'Expected safe HTTP 500 ProblemDetails.'
        }
        if ($body -match 'SqlException|ConnectionStrings|ClinIQ_P01_Unavailable') { throw 'Internal database details leaked.' }
    }

    $openApi = Invoke-RestMethod -Uri ($apiUrl + '/openapi/v1.json') -TimeoutSec 30
    if (-not $openApi.paths.'/api/specialties'.get.responses.'200') { throw 'GET contract is absent from OpenAPI.' }

    [PSCustomObject]@{
        HttpStatus = $response.StatusCode
        AnonymousRequest = $true
        Count = $(if ($ExpectedStatus -eq 200) { $items.Count } else { $null })
        OpenApiContainsRoute = $true
        RepeatedResponseStable = $(if ($ExpectedStatus -eq 200) { $true } else { $null })
        LogsPath = $probeRoot
    } | ConvertTo-Json
} finally {
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id
        $apiProcess.WaitForExit()
    }
}

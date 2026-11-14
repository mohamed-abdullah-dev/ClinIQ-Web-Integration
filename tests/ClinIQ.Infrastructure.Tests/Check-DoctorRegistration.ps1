param(
    [Parameter(Mandatory)][string]$ConnectionString,
    [Parameter(Mandatory)][Guid]$SpecialtyId,
    [Parameter(Mandatory)][string]$ProbeRoot
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$apiRoot = Join-Path $repositoryRoot 'src/ClinIQ.API'
$apiDll = Join-Path $apiRoot 'bin/Release/net10.0/ClinIQ.API.dll'
$probeRoot = [IO.Path]::GetFullPath($ProbeRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $probeRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $probeRoot)) { throw 'Expected a fresh probe folder inside workspace artifacts.' }
$connection = $ConnectionString
if ($connection -notmatch '(?i)(?:Database|Initial Catalog)=ClinIQ_P01_Tests_[0-9a-f]{32}(?:;|$)') { throw 'This probe requires a fixture-owned test database.' }
$privateRoot = Join-Path $probeRoot 'private'
$apiProcess = $null
$checks = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Path $probeRoot | Out-Null
function Check([bool]$condition, [string]$name) {
    if (-not $condition) { throw ("Failed check: " + $name + ". Logs: " + $probeRoot) }
    $checks.Add($name)
}
function Stop-OwnedApi {
    if ($null -ne $script:apiProcess -and -not $script:apiProcess.HasExited) {
        Stop-Process -Id $script:apiProcess.Id
        $script:apiProcess.WaitForExit()
    }
}
function Start-OwnedApi([int]$permitLimit, [string]$logPrefix) {
    $arguments = @(('"' + $apiDll + '"'), '--environment', 'Development', '--urls', 'http://127.0.0.1:0', '--ConnectionStrings:DefaultConnection', ('"' + $connection + '"'), '--Auth:PrivateStorageRoot', ('"' + $privateRoot + '"'), '--Auth:RateLimit:PermitLimit', [string]$permitLimit)
    $logPath = Join-Path $probeRoot ($logPrefix + '.log')
    $startOptions = @{
        FilePath = (Get-Command dotnet).Source; ArgumentList = $arguments; WorkingDirectory = $apiRoot
        RedirectStandardOutput = $logPath; RedirectStandardError = (Join-Path $probeRoot ($logPrefix + '-errors.log'))
        WindowStyle = 'Hidden'; PassThru = $true
    }
    $script:apiProcess = Start-Process @startOptions
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($script:apiProcess.HasExited) { throw ('API exited before startup. Logs: ' + $probeRoot) }
        $startup = Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
        if ($startup -match 'Now listening on: (http://127\.0\.0\.1:\d+)') { return $Matches[1] }
    }
    throw 'API startup timed out.'
}
function Post-Json([string]$path, $body) {
    Invoke-WebRequest -Uri ($script:apiUrl + $path) -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Compress) -SkipHttpErrorCheck -TimeoutSec 45
}
function Register-Doctor([string]$email, [Guid]$specialty, [string]$password = 'Synthetic-Test1!', [bool]$includeGrade = $true, [string]$fileName = 'evidence.pdf') {
    $content = [Net.Http.MultipartFormDataContent]::new()
    try {
        $fields = @{ FullName='Synthetic Doctor'; Email=$email; Password=$password; PhoneNumber='01012345678'; RegistrationNumber='TEST-HTTP'; SpecialtyId=$specialty.ToString(); ProfessionalGrade='Specialist'; Role='Admin'; CreatedByUserId=[Guid]::NewGuid().ToString() }
        foreach ($entry in $fields.GetEnumerator()) { $content.Add([Net.Http.StringContent]::new([string]$entry.Value), $entry.Key) }
        $fileFields = @('PracticeLicense', 'SyndicateRegistration', 'IdentityEvidence')
        if ($includeGrade) { $fileFields += 'ProfessionalGradeEvidence' }
        foreach ($field in $fileFields) {
            $file = [Net.Http.ByteArrayContent]::new([Text.Encoding]::ASCII.GetBytes('%PDF-1.4 Synthetic test evidence only. %%EOF'))
            $file.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/pdf')
            $content.Add($file, $field, $fileName)
        }
        $client = [Net.Http.HttpClient]::new()
        try {
            $response = $client.PostAsync($script:apiUrl + '/api/auth/doctors/register', $content).GetAwaiter().GetResult()
            try { return [pscustomobject]@{ Status=[int]$response.StatusCode; Body=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult(); HasCookie=$response.Headers.Contains('Set-Cookie') } }
            finally { $response.Dispose() }
        } finally { $client.Dispose() }
    } finally { $content.Dispose() }
}
try {
    $script:apiUrl = Start-OwnedApi 100 'api'
    $specialty = $SpecialtyId
    $email = 'synthetic-' + [Guid]::NewGuid().ToString('N') + '@example.test'
    $registered = Register-Doctor $email $specialty
    Check ($registered.Status -eq 201) 'Multipart registration returns 201'
    $receipt = $registered.Body | ConvertFrom-Json
    Check ($receipt.status -ceq 'PendingReview' -and $receipt.emailConfirmationRequired) 'Registration stays pending'
    Check ($receipt.emailDeliveryStatus -ceq 'DevelopmentFile') 'Development email delivery is explicit'
    Check (-not $registered.HasCookie -and $registered.Body -notmatch 'accessToken|refreshToken|password|storageKey') 'No credentials or private paths in receipt'
    Check ((Register-Doctor $email.ToUpperInvariant() $specialty).Status -eq 409) 'Duplicate email returns 409'
    Check ((Register-Doctor ('missing-' + $email) $specialty 'Synthetic-Test1!' $false).Status -eq 400) 'Missing grade evidence returns 400'
    Check ((Register-Doctor ('file-' + $email) $specialty 'Synthetic-Test1!' $true 'evidence.exe').Status -eq 400) 'Unsupported file type returns 400'
    Check ((Register-Doctor ('signature-' + $email) $specialty 'Synthetic-Test1!' $true 'evidence.jpg').Status -eq 400) 'File signature mismatch returns 400'
    Check ((Register-Doctor ('weak-' + $email) $specialty 'weak').Status -eq 400) 'Weak password returns 400'
    Check ((Register-Doctor ('specialty-' + $email) ([Guid]::NewGuid())).Status -eq 400) 'Unknown specialty returns 400'
    $knownResend = Post-Json '/api/auth/resend-confirmation' @{ Email=$email }
    $unknownResend = Post-Json '/api/auth/resend-confirmation' @{ Email='unknown@example.test' }
    Check ($knownResend.StatusCode -eq 202 -and $unknownResend.StatusCode -eq 202) 'Resend accepts existing and unknown emails alike'
    $mail = Get-ChildItem -LiteralPath (Join-Path $privateRoot 'development-emails') -Filter '*.json' | Select-Object -First 1
    $confirmation = Get-Content -LiteralPath $mail.FullName -Raw | ConvertFrom-Json
    $query = [System.Web.HttpUtility]::ParseQueryString(([Uri]$confirmation.ConfirmationUrl).Query)
    Check ((Post-Json '/api/auth/confirm-email' @{ UserId=$receipt.userId; Token='invalid' }).StatusCode -eq 400) 'Invalid confirmation is rejected'
    Check ((Post-Json '/api/auth/confirm-email' @{ UserId=$receipt.userId; Token=$query['token'] }).StatusCode -eq 204) 'Email confirmation returns 204'
    Check ((Post-Json '/api/auth/confirm-email' @{ UserId=$receipt.userId; Token=$query['token'] }).StatusCode -eq 204) 'Repeated confirmation is idempotent'
    $wrongLogin = Post-Json '/api/auth/mobile/login' @{ Email=$email; Password='Wrong1!' }
    $wrongBody = if ($wrongLogin.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($wrongLogin.Content) } else { [string]$wrongLogin.Content }
    Check ($wrongLogin.StatusCode -eq 401 -and ($wrongBody | ConvertFrom-Json).title -ceq 'Auth.InvalidCredentials') 'Wrong password does not disclose review status'
    $pendingLogin = Post-Json '/api/auth/mobile/login' @{ Email=$email; Password='Synthetic-Test1!' }
    $pendingBody = if ($pendingLogin.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($pendingLogin.Content) } else { [string]$pendingLogin.Content }
    Check ($pendingLogin.StatusCode -eq 403 -and ($pendingBody | ConvertFrom-Json).title -ceq 'Auth.DoctorPendingReview' -and -not $pendingLogin.Headers.ContainsKey('Set-Cookie') -and $pendingBody -notmatch 'accessToken|refreshToken') 'Pending doctor login has review message without session credentials'
    $userId = ([Guid]$receipt.userId).ToString()
    Check ((Invoke-WebRequest -Uri ($apiUrl + '/doctor-evidence/test.pdf') -SkipHttpErrorCheck).StatusCode -eq 404) 'Evidence has no public static route'
    $openApi = Invoke-RestMethod -Uri ($apiUrl + '/openapi/v1.json')
    Check ([bool]$openApi.paths.'/api/auth/doctors/register'.post.responses.'201') 'Registration appears in OpenAPI'
    $allowedCors = Invoke-WebRequest -Uri ($apiUrl + '/api/auth/doctors/register') -Method Options -Headers @{ Origin='http://localhost:5173'; 'Access-Control-Request-Method'='POST' } -SkipHttpErrorCheck
    Check ($allowedCors.Headers['Access-Control-Allow-Origin'] -contains 'http://localhost:5173') 'Configured web origin is allowed'
    $blockedCors = Invoke-WebRequest -Uri ($apiUrl + '/api/auth/doctors/register') -Method Options -Headers @{ Origin='https://untrusted.example'; 'Access-Control-Request-Method'='POST' } -SkipHttpErrorCheck
    Check (-not $blockedCors.Headers.ContainsKey('Access-Control-Allow-Origin')) 'Unknown web origin is not allowed'
    Stop-OwnedApi
    $script:apiUrl = Start-OwnedApi 2 'rate-limit'
    Check ((Post-Json '/api/auth/resend-confirmation' @{ Email='unknown@example.test' }).StatusCode -eq 202) 'Rate limit accepts first request'
    Check ((Post-Json '/api/auth/resend-confirmation' @{ Email='unknown@example.test' }).StatusCode -eq 202) 'Rate limit accepts second request'
    Check ((Post-Json '/api/auth/resend-confirmation' @{ Email='unknown@example.test' }).StatusCode -eq 429) 'Rate limit rejects excess request'
    $report = [pscustomobject]@{ Passed=$checks.Count; Checks=$checks; LogsPath=$probeRoot; UserId=$userId; UsesFixtureOwnedDatabase=$true }
    $report | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $probeRoot 'report.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 3
} finally {
    Stop-OwnedApi
}

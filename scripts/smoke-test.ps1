<#
.SYNOPSIS
    Exercises every important path of a running API and reports pass/fail per check.

.DESCRIPTION
    Covers the happy path (register three poses, verify, mark attendance) and, just as
    importantly, the guards: an imposter, an unknown face, a double tap, a face already
    registered under another id, a device key trying to enrol, and a text file posing as a JPEG.

    Written for Windows PowerShell 5.1, so it uses System.Net.Http directly rather than the
    PowerShell 7 only -Form and -SkipHttpErrorCheck parameters of Invoke-WebRequest.

    Assumes the DEVELOPMENT provider, which derives identity from the upload file name. Under
    onnx or aws the synthetic sample images contain no detectable face, so most checks will
    report no_face_detected - that is correct behaviour, and you should re-run with real photos.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5080',
    [string]$AdminKey = 'dev-admin-key',
    [string]$DeviceKey = 'dev-device-key',
    # Resolved after the param block: $PSScriptRoot is empty inside a param default on
    # Windows PowerShell 5.1.
    [string]$SampleDirectory,
    # Fixed on purpose. A per-run prefix would collide with itself: the sample faces stay the
    # same, so the duplicate-identity guard refuses to register them under a second id.
    # Reset-TestPerson below clears the previous run instead.
    [string]$Prefix = 'SMOKE-TEST'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$scriptDirectory = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $SampleDirectory) { $SampleDirectory = Join-Path $scriptDirectory '..\docs\samples' }

$script:Passed = 0
$script:Failed = 0
$script:Client = New-Object System.Net.Http.HttpClient
$script:Client.Timeout = [TimeSpan]::FromSeconds(60)

function Check {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')

    if ($Condition) {
        Write-Host ('  PASS  ' + $Name) -ForegroundColor Green
        $script:Passed++
    }
    else {
        Write-Host ('  FAIL  ' + $Name) -ForegroundColor Red
        if ($Detail) { Write-Host ('        ' + $Detail) -ForegroundColor DarkGray }
        $script:Failed++
    }
}

function Resolve-Sample {
    param([string]$Name)
    $path = Join-Path $SampleDirectory ($Name + '.jpg')
    if (-not (Test-Path $path)) { throw "Sample image not found: $path" }
    (Get-Item $path).FullName
}

# Returns status and parsed body for every response, including 4xx, because a 409 or a 415 is
# frequently the expected answer in this script.
function Send-Api {
    param(
        [string]$Method = 'GET',
        [string]$Path,
        [hashtable]$Fields,
        [hashtable]$Files,
        [string]$Json,
        [string]$Key = $AdminKey
    )

    $request = New-Object System.Net.Http.HttpRequestMessage(
        (New-Object System.Net.Http.HttpMethod($Method)), ($BaseUrl + $Path))

    if ($Key) { $request.Headers.Add('X-Api-Key', $Key) }

    if ($Fields -or $Files) {
        $multipart = New-Object System.Net.Http.MultipartFormDataContent

        if ($Fields) {
            foreach ($name in $Fields.Keys) {
                $multipart.Add((New-Object System.Net.Http.StringContent([string]$Fields[$name])), $name)
            }
        }

        if ($Files) {
            foreach ($name in $Files.Keys) {
                $bytes = [System.IO.File]::ReadAllBytes($Files[$name])
                $part = New-Object System.Net.Http.ByteArrayContent(, $bytes)
                $part.Headers.ContentType =
                    [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('image/jpeg')
                $multipart.Add($part, $name, [System.IO.Path]::GetFileName($Files[$name]))
            }
        }

        $request.Content = $multipart
    }
    elseif ($Json) {
        $request.Content = New-Object System.Net.Http.StringContent(
            $Json, [System.Text.Encoding]::UTF8, 'application/json')
    }

    $response = $script:Client.SendAsync($request).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

    $parsed = $null
    if ($body) { try { $parsed = $body | ConvertFrom-Json } catch { } }

    [pscustomobject]@{ Status = [int]$response.StatusCode; Data = $parsed; Raw = $body }
}

# Clears a previous run. Purging biometrics is the important part: it releases the face, so the
# duplicate-identity guard will not refuse to register the same sample image again. The person
# row itself survives - there is no delete-person endpoint, which is why Register-TestPerson
# handles the already-exists case.
function Reset-TestPerson {
    param([string]$ExternalId)

    $existing = Send-Api GET ('/api/v1/persons/by-external-id/' + $ExternalId)
    if ($existing.Status -ne 200) { return $null }

    Send-Api DELETE ('/api/v1/persons/' + $existing.Data.id + '/biometrics') | Out-Null
    return $existing.Data.id
}

# Registers three poses whether or not the person already exists, so the script is idempotent.
# A first run exercises POST /registrations; later runs exercise POST /persons/{id}/faces/batch.
function Register-TestPerson {
    param([string]$ExternalId, [string]$FullName, [string]$SamplePrefix)

    $files = @{
        front = Resolve-Sample ($SamplePrefix + '-front')
        left  = Resolve-Sample ($SamplePrefix + '-left')
        right = Resolve-Sample ($SamplePrefix + '-right')
    }

    $existing = Send-Api GET ('/api/v1/persons/by-external-id/' + $ExternalId)

    if ($existing.Status -eq 200) {
        $batch = Send-Api POST ('/api/v1/persons/' + $existing.Data.id + '/faces/batch') -Files $files
        return [pscustomobject]@{
            Ok       = ($batch.Status -eq 200)
            PersonId = $existing.Data.id
            Detail   = "batch onto the existing person returned $($batch.Status) $($batch.Data.detail)"
        }
    }

    $created = Send-Api POST '/api/v1/registrations' `
        -Fields @{ externalId = $ExternalId; fullName = $FullName; department = 'Operations' } `
        -Files $files

    return [pscustomobject]@{
        Ok       = ($created.Status -eq 201)
        PersonId = $created.Data.person.id
        Detail   = "POST /registrations returned $($created.Status) $($created.Data.detail)"
    }
}

Write-Host ''
Write-Host "Smoke test against $BaseUrl" -ForegroundColor Cyan
Write-Host ''

# --- Reachability and configuration --------------------------------------------------------
Write-Host 'Reachability' -ForegroundColor White

$health = $null
try { $health = Send-Api GET '/health' -Key '' } catch { }

if (-not $health -or $health.Status -ne 200) {
    Write-Host '  FAIL  the API is not reachable' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Start it with:  dotnet run --project src/Api' -ForegroundColor Yellow
    exit 1
}
Check 'health endpoint returns 200' ($health.Status -eq 200)

$anonymous = Send-Api GET '/api/v1/persons' -Key ''
Check 'a request with no API key is rejected (401)' ($anonymous.Status -eq 401) "status $($anonymous.Status)"

$provider = Send-Api GET '/api/v1/diagnostics/provider'
Check 'diagnostics reports a provider' ($provider.Status -eq 200 -and $provider.Data.providerId)

$providerId = $provider.Data.providerId
Write-Host ('        provider: ' + $providerId) -ForegroundColor DarkGray

if ($providerId -notlike 'development:*') {
    Write-Host ''
    Write-Host 'NOTE: a real provider is active. The synthetic sample images contain no face, so' -ForegroundColor Yellow
    Write-Host '      the checks below will fail with no_face_detected. Use real photographs.' -ForegroundColor Yellow
    Write-Host ''
}

Check 'exactly three poses are required' ($provider.Data.requiredPoses.Count -eq 3) `
    "got $($provider.Data.requiredPoses.Count) - more than three usually means config array binding appended to the defaults"
Check 'the 1:N threshold is at least the 1:1 threshold' `
    ($provider.Data.identificationThreshold -ge $provider.Data.verificationThreshold)

# --- Registration --------------------------------------------------------------------------
Write-Host ''
Write-Host 'Registration' -ForegroundColor White

$aliceId = "$Prefix-1001"
$bobId = "$Prefix-1002"
$fraudId = "$Prefix-9999"

foreach ($id in @($aliceId, $bobId, $fraudId)) { Reset-TestPerson $id | Out-Null }

$alice = Register-TestPerson $aliceId 'Alice Kumar' 'alice'
Check 'three poses register in one call' $alice.Ok $alice.Detail
$personGuid = $alice.PersonId

$bob = Register-TestPerson $bobId 'Bob Singh' 'bob'
Check 'a second person registers' $bob.Ok $bob.Detail

$aliceState = Send-Api GET "/api/v1/persons/$personGuid/enrollment"
Check 'the person is Enrolled' ($aliceState.Data.enrollmentStatus -eq 'Enrolled') `
    "status $($aliceState.Data.enrollmentStatus)"
Check 'all three poses are stored' ($aliceState.Data.poses.Count -eq 3) `
    "got $($aliceState.Data.poses.Count)"

$enrollment = Send-Api GET "/api/v1/persons/$personGuid/enrollment"
Check 'enrollment reports no missing poses' ($enrollment.Data.missingPoses.Count -eq 0)
Check 'every stored template belongs to the active provider' `
    (@($enrollment.Data.poses | Where-Object { -not $_.providerMatchesCurrent }).Count -eq 0)

# Alice is guaranteed to exist by now, so this can only be a conflict - it never creates a row.
$clash = Send-Api POST '/api/v1/persons' -Json (@{ externalId = $aliceId; fullName = 'Clash' } | ConvertTo-Json)
Check 'a repeated employee id is a conflict (409)' ($clash.Status -eq 409) "status $($clash.Status)"

# --- Attendance ----------------------------------------------------------------------------
Write-Host ''
Write-Host 'Attendance' -ForegroundColor White

# A re-run inside the duplicate-punch window will legitimately be collapsed rather than
# accepted, so work out which answer is correct before asserting one.
$window = [int]$provider.Data.duplicatePunchWindowMinutes
$recent = Send-Api GET ("/api/v1/attendance?personId=$personGuid&decision=Accepted&pageSize=1")
$insideWindow = $false

if ($window -gt 0 -and $recent.Data.items.Count -gt 0) {
    $last = [datetimeoffset]::Parse($recent.Data.items[0].createdAtUtc)
    $insideWindow = ($last -gt [datetimeoffset]::UtcNow.AddMinutes(-$window))
}

$punch = Send-Api POST '/api/v1/attendance/mark' -Key $DeviceKey `
    -Fields @{ externalId = $aliceId; deviceId = 'SMOKE-GATE' } `
    -Files @{ image = Resolve-Sample 'alice-front' }

if ($insideWindow) {
    Write-Host ("        a punch from a previous run is still inside the ${window}-minute window") -ForegroundColor DarkGray
    Check '1:1 verification matches, and the punch is collapsed as Duplicate' `
        ($punch.Data.decision -eq 'Duplicate') "decision $($punch.Data.decision)"
    Check 'the collapsed punch reports a score above the threshold' `
        ($punch.Data.score -ge $punch.Data.threshold) "score $($punch.Data.score) vs $($punch.Data.threshold)"
}
else {
    Check '1:1 verification is accepted' ($punch.Data.decision -eq 'Accepted') "decision $($punch.Data.decision)"
    Check 'the first punch of the day is an In' ($punch.Data.direction -eq 'In') "direction $($punch.Data.direction)"
}

Check 'the matched person is returned' ($punch.Data.externalId -eq $aliceId)

$repeat = Send-Api POST '/api/v1/attendance/mark' -Key $DeviceKey `
    -Fields @{ externalId = $aliceId } `
    -Files @{ image = Resolve-Sample 'alice-left' }

Check 'an immediate repeat is collapsed as Duplicate' ($repeat.Data.decision -eq 'Duplicate') `
    "decision $($repeat.Data.decision) - this is the guard that stops a double tap clocking someone out"

$imposter = Send-Api POST '/api/v1/attendance/mark' -Key $DeviceKey `
    -Fields @{ externalId = $aliceId } `
    -Files @{ image = Resolve-Sample 'bob-front' }

Check 'the wrong face against a claimed id is Rejected' ($imposter.Data.decision -eq 'Rejected')
Check 'a non-match is still HTTP 200' ($imposter.Status -eq 200) `
    'a failed match is a valid request; only malformed requests get a 4xx'

$identify = Send-Api POST '/api/v1/attendance/mark' -Key $DeviceKey `
    -Fields @{ deviceId = 'SMOKE-GATE-B' } `
    -Files @{ image = Resolve-Sample 'bob-left' }

Check '1:N identification finds the right person' ($identify.Data.externalId -eq $bobId) `
    "matched '$($identify.Data.externalId)', expected '$bobId'"

$unknown = Send-Api POST '/api/v1/attendance/mark' -Key $DeviceKey `
    -Files @{ image = Resolve-Sample 'carol-front' }

Check 'an unregistered face is Rejected' ($unknown.Data.decision -eq 'Rejected')
Check 'no person is attached to a failed identification' (-not $unknown.Data.personId)

$register = Send-Api GET '/api/v1/attendance?pageSize=50'
Check 'the register records failures as well as successes' ($register.Data.total -ge 5) `
    "total $($register.Data.total) - rejected attempts are the audit trail for a disputed punch"

# --- Guards --------------------------------------------------------------------------------
Write-Host ''
Write-Host 'Guards' -ForegroundColor White

$fraud = Send-Api POST '/api/v1/registrations' `
    -Fields @{ externalId = "$Prefix-9999"; fullName = 'Carol Impostor' } `
    -Files @{
        front = Resolve-Sample 'alice-front'
        left  = Resolve-Sample 'alice-left'
        right = Resolve-Sample 'alice-right'
    }

Check 'an already-registered face under a new id is refused (409)' ($fraud.Status -eq 409) "status $($fraud.Status)"
Check 'the refusal is reported as duplicate_identity' ($fraud.Data.code -eq 'duplicate_identity') "code $($fraud.Data.code)"

$orphan = Send-Api GET "/api/v1/persons/by-external-id/$Prefix-9999"
Check 'the rejected registration left no orphan person (404)' ($orphan.Status -eq 404) `
    "status $($orphan.Status) - a person row with no usable face would occupy the employee id"

$sameFrame = Send-Api POST "/api/v1/persons/$personGuid/faces" `
    -Fields @{ pose = 'Front' } `
    -Files @{ image = Resolve-Sample 'alice-left' }

Check 'the same frame reused for another pose is refused (409)' ($sameFrame.Status -eq 409) `
    "status $($sameFrame.Status) - otherwise one photo could fill all three angles"

$deviceEnrol = Send-Api POST '/api/v1/persons' -Key $DeviceKey `
    -Json (@{ externalId = "$Prefix-7777"; fullName = 'Nope' } | ConvertTo-Json)
Check 'a device key cannot enrol anybody (403)' ($deviceEnrol.Status -eq 403) "status $($deviceEnrol.Status)"

$textFile = Join-Path $env:TEMP 'smoke-not-an-image.jpg'
Set-Content -Path $textFile -Value 'this is definitely not an image' -Encoding ascii
$badUpload = Send-Api POST '/api/v1/diagnostics/analyze' -Files @{ image = $textFile }
Check 'a text file named .jpg is refused (415)' ($badUpload.Status -eq 415) `
    "status $($badUpload.Status) - the file signature decides, not the declared content type"
Remove-Item $textFile -ErrorAction SilentlyContinue

# --- Summary -------------------------------------------------------------------------------
Write-Host ''
if ($script:Failed -eq 0) {
    Write-Host "All $script:Passed checks passed." -ForegroundColor Green
}
else {
    Write-Host "$script:Passed passed, $script:Failed FAILED." -ForegroundColor Red
}

Write-Host ''
Write-Host "Test data remains under employee ids prefixed $Prefix." -ForegroundColor DarkGray
Write-Host 'To clear it: DELETE /api/v1/persons/{id}/biometrics, then remove the person rows.' -ForegroundColor DarkGray

if ($script:Failed -gt 0) { exit 1 } else { exit 0 }

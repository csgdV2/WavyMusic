param(
    [Parameter(Mandatory = $true)][string]$CertificateThumbprint,
    [Parameter(Mandatory = $true)][string[]]$Files,
    [string]$SignToolPath = 'signtool.exe',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$thumbprint = $CertificateThumbprint.Replace(' ', '')
$certificate = Get-Item -LiteralPath "Cert:/CurrentUser/My/$thumbprint" -ErrorAction Stop
if ($certificate.NotAfter -lt (Get-Date)) { throw 'The signing certificate has expired.' }
if (-not $certificate.HasPrivateKey) { throw 'This certificate has no accessible signing key.' }
foreach ($file in $Files) {
    $resolved = (Resolve-Path -LiteralPath $file).Path
    if ([IO.Path]::GetExtension($resolved) -notin @('.exe', '.dll', '.msi')) { throw "Unsupported signing input: $resolved" }
    & $SignToolPath sign /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $resolved
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $resolved" }
    & $SignToolPath verify /pa /all $resolved
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $resolved" }
}

[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')]
    [string]$KeyId = 'focusir-release-2026',

    [string]$PublicKeyPath,

    [string]$PrivateKeyPath,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublicKeyPath)) {
    $PublicKeyPath = Join-Path $repositoryRoot "eng\keys\$KeyId.pem"
}
if ([string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    $PrivateKeyPath = Join-Path $repositoryRoot 'artifacts\signing\update-signing-key.pkcs8.b64'
}

$PublicKeyPath = [IO.Path]::GetFullPath($PublicKeyPath)
$PrivateKeyPath = [IO.Path]::GetFullPath($PrivateKeyPath)

foreach ($path in @($PublicKeyPath, $PrivateKeyPath)) {
    if ((Test-Path -LiteralPath $path) -and -not $Force) {
        throw "文件已存在，拒绝覆盖：$path。确需轮换密钥时请显式使用 -Force。"
    }

    $parent = Split-Path -Parent $path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

function ConvertTo-Pem {
    param(
        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    $base64 = [Convert]::ToBase64String($Bytes)
    $lines = for ($offset = 0; $offset -lt $base64.Length; $offset += 64) {
        $length = [Math]::Min(64, $base64.Length - $offset)
        $base64.Substring($offset, $length)
    }

    return "-----BEGIN $Label-----`n$($lines -join "`n")`n-----END $Label-----`n"
}

$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
$curve = [Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256')
$ecdsa = [Security.Cryptography.ECDsa]::Create($curve)
try {
    $privateKeyBase64 = [Convert]::ToBase64String($ecdsa.ExportPkcs8PrivateKey())
    $publicKeyPem = ConvertTo-Pem -Label 'PUBLIC KEY' -Bytes $ecdsa.ExportSubjectPublicKeyInfo()

    [IO.File]::WriteAllText($PrivateKeyPath, $privateKeyBase64, $utf8WithoutBom)
    [IO.File]::WriteAllText($PublicKeyPath, $publicKeyPem, $utf8WithoutBom)
}
finally {
    $ecdsa.Dispose()
}

Write-Host "已生成 keyId '$KeyId' 的 ECDSA P-256 密钥。"
Write-Host "可提交公钥：$PublicKeyPath"
Write-Host "私钥（不得提交，请转存到 Gitee 加密变量 UPDATE_SIGNING_KEY_PKCS8）：$PrivateKeyPath"

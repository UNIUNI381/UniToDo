param([switch]$WhatIf, [switch]$CorrectionScriptOnly)

# Load the UTF-8 implementation explicitly for Windows PowerShell 5.1.
$projectRoot = Split-Path -Parent $PSScriptRoot
$implementationPath = Join-Path $PSScriptRoot "install-typewhisper-integration.implementation.ps1"
$implementationText = Get-Content -Raw -Encoding utf8 $implementationPath
$implementationBlock = [ScriptBlock]::Create($implementationText)
& $implementationBlock -ProjectRoot $projectRoot -WhatIf:$WhatIf -CorrectionScriptOnly:$CorrectionScriptOnly

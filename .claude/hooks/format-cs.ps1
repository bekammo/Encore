# PostToolUse (Edit|Write): runs dotnet format whitespace on the edited .cs file only.

$ErrorActionPreference = 'Stop'
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
$hook = $reader.ReadToEnd() | ConvertFrom-Json
$path = [string]$hook.tool_input.file_path
if (-not $path -or [IO.Path]::GetExtension($path) -ne '.cs') { exit 0 }

$root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } elseif ($hook.cwd) { $hook.cwd } else { (Get-Location).Path }
$candidate = if ([IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $root $path }
$full = [IO.Path]::GetFullPath($candidate)
if (-not (Test-Path -LiteralPath $full)) { exit 0 }

Push-Location $root
try {
    $output = & dotnet format whitespace --folder --include $full 2>&1
    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine("dotnet format whitespace failed on ${path}:`n$($output -join "`n")")
        exit 1
    }
}
finally { Pop-Location }

exit 0

# Stop: when any .cs file differs from HEAD, builds the solution. A failed build exits 2,
# which keeps Claude working with the first 30 error lines. A second stop never re-runs it.

$ErrorActionPreference = 'Stop'
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
$hook = $reader.ReadToEnd() | ConvertFrom-Json
if ($hook.stop_hook_active -eq $true) { exit 0 }

$root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } elseif ($hook.cwd) { $hook.cwd } else { (Get-Location).Path }

# Tracked changes against HEAD, plus new .cs files git doesn't know yet.
$changed = @(& git -C $root diff --name-only HEAD -- '*.cs') +
           @(& git -C $root ls-files --others --exclude-standard -- '*.cs') | Where-Object { $_ }
if ($changed.Count -eq 0) { exit 0 }

Push-Location $root
try {
    $output = & dotnet build Encore.sln -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        $errors = @($output | ForEach-Object { "$_" } | Where-Object { $_ -match ': error ' } | Select-Object -Unique -First 30)
        if ($errors.Count -eq 0) { $errors = @($output | Select-Object -Last 30) }
        [Console]::Error.WriteLine("dotnet build Encore.sln failed:`n$($errors -join "`n")")
        exit 2
    }
}
finally { Pop-Location }

exit 0

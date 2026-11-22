# PreToolUse (Edit|Write): denies any write under load/evidence/, the committed measurement evidence.

$ErrorActionPreference = 'Stop'
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
$hook = $reader.ReadToEnd() | ConvertFrom-Json
$path = [string]$hook.tool_input.file_path
if (-not $path) { exit 0 }

$root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } elseif ($hook.cwd) { $hook.cwd } else { (Get-Location).Path }
$candidate = if ([IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $root $path }
$full = [IO.Path]::GetFullPath($candidate).Replace('/', '\')
$evidence = [IO.Path]::GetFullPath((Join-Path $root 'load\evidence')).TrimEnd('\') + '\'

if ($full.StartsWith($evidence, [StringComparison]::OrdinalIgnoreCase)) {
    @{
        hookSpecificOutput = @{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = 'load/evidence/ is committed measurement evidence and stays unchanged. Write new load-run output to load/results/ instead.'
        }
    } | ConvertTo-Json -Depth 3 -Compress
}

exit 0

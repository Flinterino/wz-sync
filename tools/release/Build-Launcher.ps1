param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if (-not $IsWindows) { throw 'The WPF release build requires Windows.' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Use a new launcher output directory.' }

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$commit = (& git -C $repository rev-parse --verify HEAD).Trim()
if ($commit -notmatch '\A[0-9a-f]{40}\z') { throw 'Cannot identify the launcher commit.' }

$work = [IO.Directory]::CreateTempSubdirectory('wz-sync-build-')
try {
    $archive = Join-Path $work.FullName 'source.zip'
    & git -C $repository archive --format=zip "--output=$archive" $commit

    $published = foreach ($build in 'first', 'second') {
        $source = Join-Path $work.FullName "$build-source"
        $output = Join-Path $work.FullName "$build-output"
        Expand-Archive -LiteralPath $archive -DestinationPath $source
        Push-Location -LiteralPath $source
        try {
            $publishArgs = @(
                'publish', 'src/WzSync.App/WzSync.App.csproj', '--configuration', 'Release', '--runtime', 'win-x64',
                '--self-contained', 'true', '--output', $output, '-p:PublishSingleFile=true',
                '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishReadyToRun=false',
                '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true',
                "-p:SourceRevisionId=$commit", "-p:PathMap=$source=/_/"
            )
            & dotnet @publishArgs | Out-Host

            $files = @(Get-ChildItem -LiteralPath $output -Force)
            if ($files.Count -ne 1 -or $files[0].PSIsContainer -or $files[0].Name -cne 'WzSync.App.exe' -or
                ($files[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -or $files[0].Length -le 0) {
                throw 'Publish must produce a single WzSync.App.exe.'
            }
            $files[0]
        }
        finally { Pop-Location }
    }

    $first = (Get-FileHash -LiteralPath $published[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $second = (Get-FileHash -LiteralPath $published[1].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($first -cne $second -or $published[0].Length -ne $published[1].Length) {
        throw "The two clean publishes differ: $first vs $second."
    }

    if (Test-Path -LiteralPath $destination) { throw 'The launcher output directory already exists.' }
    [void][IO.Directory]::CreateDirectory($destination)
    [IO.File]::Copy($published[0].FullName, (Join-Path $destination 'WzSync.App.exe'), $false)
    $checksum = Join-Path $destination 'WzSync.App.exe.sha256'
    $stream = [IO.File]::Open($checksum, [IO.FileMode]::CreateNew)
    try { $stream.Write([Text.Encoding]::ASCII.GetBytes("$first  WzSync.App.exe" + [char]10)) }
    finally { $stream.Dispose() }

    Write-Host "Reproducible Windows x64 launcher at $commit ($($published[0].Length) bytes): $first"
    if ($env:GITHUB_OUTPUT) {
        [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "sha256=$first" + [char]10)
    }
}
finally {
    $resolved = (Resolve-Path -LiteralPath $work.FullName).Path
    if ($resolved -ceq $work.FullName -and (Split-Path -Parent $resolved) -ceq $work.Parent.FullName -and
        (Split-Path -Leaf $resolved).StartsWith('wz-sync-build-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolved -Recurse
    }
}

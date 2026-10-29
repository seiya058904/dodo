#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string] $Configuration = 'Release',
    [ValidateSet('SelfContained', 'NoRuntime', 'Both')][string] $RuntimeFlavor = 'SelfContained',
    [switch] $SingleFile,
    [switch] $RebuildLmdb,
    [string] $ExpectedTag,
    [string] $OutputDirectory,
    [ValidatePattern('^[a-zA-Z0-9-]*$')][string] $BuildLabel = '',
    [string] $ValidateArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path $PSScriptRoot -Parent

function Assert-DodoArchive([string] $Archive) {
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($name in $names) {
            if ($name.StartsWith('/') -or $name.Contains(':') -or $name.Split('/') -contains '..') {
                throw "Unsafe archive entry: $name"
            }
            if ($name -match '(^|/)(dodo-data|data(?:\.[^/]*)?\.json|note-assets\.lmdb(?:-lock)?|telemetry[^/]*|[^/]*\.log|\.env[^/]*|\.git|\.local|\.runtime|obj|bin)(/|$)' -or
                $name -match '^plugins/data/') {
                throw "User data, cache or development files must not be packaged: $name"
            }
        }
        $required = @('Dodo.exe', 'LICENSE.md', 'REQUIRED-NOTICE.md', 'THIRD-PARTY-NOTICES.md',
            'licenses/LMDB-LICENSE', 'licenses/LMDB-COPYRIGHT', 'licenses/wpf-notifyicon-LICENSE',
            'PORTABLE-README.txt', 'licenses/Markdig-LICENSE.txt', 'licenses/ModelContextProtocol-LICENSE.txt',
            'licenses/SharpGen-LICENSE.txt', 'licenses/Vortice-LICENSE.txt', 'licenses/Costura-LICENSE.txt',
            'licenses/DOTNET-LICENSE.txt', 'Dodo-package.json', 'plugins/sample.focus-timer.native/plugin.json',
            'plugins/sample.focus-timer.native/PaperTodo.Plugin.FocusTimer.dll',
            'plugins/sample.clock.native/plugin.json', 'plugins/sample.clock.native/PaperTodo.Plugin.SampleClock.dll')
        foreach ($name in $required) {
            if ($names -cnotcontains $name -or $zip.GetEntry($name).Length -le 0) { throw "Missing or empty package file: $name" }
        }
        $reader = [IO.StreamReader]::new($zip.GetEntry('Dodo-package.json').Open())
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($metadata.product -cne 'Dodo' -or $metadata.runtime -cne 'win-x64') { throw 'Invalid Dodo package metadata.' }
        if (-not $metadata.singleFile) {
            foreach ($name in @('Dodo.dll', 'Dodo.deps.json', 'Dodo.runtimeconfig.json', 'papertodo_lmdb.dll', 'PaperTodo.Plugin.Abstractions.dll')) {
                if ($names -cnotcontains $name) { throw "Incomplete multi-file application: $name" }
            }
            if ($metadata.selfContained -and $names -cnotcontains 'coreclr.dll') { throw 'Self-contained package has no .NET runtime.' }
        }
        if ($metadata.selfContained) {
            foreach ($runtimeId in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
                if (-not ($names -match "^licenses/nuget/$([regex]::Escape($runtimeId))/[^/]+/LICENSE(?:\.TXT)?$")) {
                    throw "Missing runtime license: $runtimeId"
                }
            }
        }
        $pluginIds = @($names | Where-Object { $_ -match '^plugins/[^/]+/plugin.json$' } | ForEach-Object { $_.Split('/')[1] })
        if ($pluginIds.Count -ne 2 -or 'sample.focus-timer.native' -notin $pluginIds -or 'sample.clock.native' -notin $pluginIds) {
            throw 'Only the two selected native plugins belong in the distribution.'
        }
        foreach ($id in $pluginIds) {
            if ($names -contains "plugins/$id/PaperTodo.Plugin.Abstractions.dll") { throw "Duplicated host plugin contract in $id" }
            $reader = [IO.StreamReader]::new($zip.GetEntry("plugins/$id/plugin.json").Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($manifest.kind -cne 'native' -or $manifest.id -cne $id -or $names -cnotcontains "plugins/$id/$($manifest.entry)") {
                throw "Invalid bundled native plugin: $id"
            }
        }
        Write-Host "Validated complete package: $Archive ($($zip.Entries.Count) files)"
    }
    finally { $zip.Dispose() }
}

function Copy-NuGetNotices([string] $PublishDirectory) {
    $assets = Get-Content -LiteralPath (Join-Path $repository 'obj/project.assets.json') -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
    $packagePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $assets.libraries.Values) {
        if ($library.type -eq 'package') { $null = $packagePaths.Add($library.path) }
    }
    foreach ($framework in $assets.project.frameworks.Values) {
        if (-not $framework.ContainsKey('downloadDependencies')) { continue }
        foreach ($dependency in $framework.downloadDependencies) {
            if ($dependency.version -notmatch '^\[([^,\]]+)') { throw "Unpinned runtime pack: $($dependency.name)" }
            $null = $packagePaths.Add($dependency.name.ToLowerInvariant() + '/' + $Matches[1])
        }
    }
    foreach ($packagePath in $packagePaths) {
        $source = Join-Path $assets.project.restore.packagesPath $packagePath
        $legalFiles = @(Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object {
            $_.Extension -eq '.nuspec' -or $_.Name -match '^(license|licence|copying|notice|third.party.notices)(\..*)?$'
        })
        foreach ($file in $legalFiles) {
            $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
            $target = Join-Path $PublishDirectory "licenses/nuget/$packagePath/$relative"
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}

if ($ValidateArchive) { Assert-DodoArchive $ValidateArchive; return }
if (-not $IsWindows) { throw 'Dodo packaging requires Windows and the .NET 10 SDK.' }
$null = Get-Command dotnet -ErrorAction Stop
$projectPath = Join-Path $repository 'PaperTodo.csproj'
$project = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)
$versions = $project.SelectNodes('/Project/PropertyGroup/Version')
if ($versions.Count -ne 1) { throw 'The host project must declare exactly one Version.' }
$version = $versions[0].InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid version: $version" }
if ($ExpectedTag -and $ExpectedTag -cne "v$version") { throw "Tag $ExpectedTag does not match project version v$version." }
$assemblyNames = $project.SelectNodes('/Project/PropertyGroup/AssemblyName')
if ($assemblyNames.Count -ne 1 -or $assemblyNames[0].InnerText -cne 'Dodo') { throw 'The distributable must be named Dodo.exe.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository ('输出/packages/' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Use a new output directory; existing packages are never overwritten: $output" }

$notices = @{
    'LICENSE.md' = 'LICENSE.md'
    'REQUIRED-NOTICE.md' = 'REQUIRED-NOTICE.md'
    'THIRD-PARTY-NOTICES.md' = 'THIRD-PARTY-NOTICES.md'
    'licenses/LMDB-LICENSE' = 'native/lmdb/upstream/LICENSE'
    'licenses/LMDB-COPYRIGHT' = 'native/lmdb/upstream/COPYRIGHT'
    'licenses/wpf-notifyicon-LICENSE' = 'vendor/wpf-notifyicon/LICENSE'
    'PORTABLE-README.txt' = 'tools/PORTABLE-README.txt'
}
foreach ($license in @('Markdig', 'ModelContextProtocol', 'SharpGen', 'Vortice', 'Costura', 'DOTNET')) {
    $notices["licenses/$license-LICENSE.txt"] = "licenses/$license-LICENSE.txt"
}
foreach ($source in $notices.Values) {
    if (-not (Test-Path -LiteralPath (Join-Path $repository $source) -PathType Leaf)) { throw "Required notice is missing: $source" }
}

# A fresh staging tree outside the repository prevents SDK compile globs from absorbing a publish.
# Retain it for inspection; the script never broad-deletes directories or copies a live app folder.
$staging = Join-Path ([IO.Path]::GetTempPath()) ('Dodo-package-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $staging, $output
$packages = [Collections.Generic.List[string]]::new()
Push-Location $repository
try {
    $sourceCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve source commit.' }
    $sourceModified = -not [string]::IsNullOrWhiteSpace((& git status --porcelain --untracked-files=no | Out-String))
    if ($RebuildLmdb) {
        & ./native/lmdb/build.ps1 -Configuration Release -ForceRebuild | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Native LMDB rebuild failed.' }
    }

    $plugins = @(
        @{ Project = 'PaperTodo.Plugin.FocusTimer'; Id = 'sample.focus-timer.native' },
        @{ Project = 'PaperTodo.Plugin.SampleClock'; Id = 'sample.clock.native' }
    )
    foreach ($plugin in $plugins) {
        $pluginOutput = Join-Path $staging ('compiled-' + $plugin.Id)
        & dotnet build "plugin-samples/$($plugin.Project)/$($plugin.Project).csproj" -c Release -o $pluginOutput "/p:PathMap=$repository=/_/Dodo" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Plugin build failed: $($plugin.Id)" }
        $plugin.Output = $pluginOutput
    }

    $flavors = if ($RuntimeFlavor -eq 'Both') { @('SelfContained', 'NoRuntime') } else { @($RuntimeFlavor) }
    foreach ($flavor in $flavors) {
        $selfContained = $flavor -eq 'SelfContained'
        $runtimeLabel = if ($selfContained) { 'portable' } else { 'no-runtime' }
        $suffix = if ($Configuration -eq 'Debug') { '-debug' } else { '' }
        if ($BuildLabel) { $suffix += "-$BuildLabel" }
        if ($SingleFile) { $suffix += '-single-file' }
        $stem = "Dodo-v$version$suffix-win-x64-$runtimeLabel"
        $publish = Join-Path $staging $stem
        $debugType = if ($Configuration -eq 'Debug') { 'embedded' } else { 'none' }
        $arguments = @('publish', $projectPath, '-c', $Configuration, '-r', 'win-x64',
            '--self-contained', $selfContained.ToString().ToLowerInvariant(), '-o', $publish,
            "/p:PublishSingleFile=$($SingleFile.IsPresent.ToString().ToLowerInvariant())", '/p:PublishTrimmed=false',
            '/p:PublishAot=false', '/p:PublishReadyToRun=false', '/p:EnableCompressionInSingleFile=false',
            '/p:IncludeNativeLibrariesForSelfExtract=true', '/p:EmbedAllSources=false', '/p:EmbedUntrackedSources=false',
            "/p:DebugType=$debugType", "/p:DebugSymbols=$($Configuration -eq 'Debug')", "/p:PathMap=$repository=/_/Dodo")
        & dotnet @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $flavor." }
        foreach ($notice in $notices.GetEnumerator()) {
            $target = Join-Path $publish $notice.Key
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)
            Copy-Item -LiteralPath (Join-Path $repository $notice.Value) -Destination $target
        }
        Copy-NuGetNotices $publish
        foreach ($plugin in $plugins) {
            $target = Join-Path $publish "plugins/$($plugin.Id)"
            $null = New-Item -ItemType Directory -Path $target
            foreach ($file in @("$($plugin.Project).dll", "$($plugin.Project).deps.json")) {
                Copy-Item -LiteralPath (Join-Path $plugin.Output $file) -Destination $target
            }
            Copy-Item -LiteralPath "plugin-samples/$($plugin.Project)/plugin.json" -Destination $target
            Copy-Item -LiteralPath "plugin-samples/$($plugin.Project)/README.md" -Destination $target
        }
        [ordered]@{
            product = 'Dodo'; version = $version; runtime = 'win-x64'; configuration = $Configuration
            selfContained = $selfContained; singleFile = $SingleFile.IsPresent; sourceCommit = $sourceCommit
            sourceModified = $sourceModified; lmdbRebuiltFromSource = $RebuildLmdb.IsPresent
            bundledPlugins = @($plugins.Id)
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'Dodo-package.json') -Encoding utf8NoBOM
        $archive = Join-Path $output "$stem.zip"
        [IO.Compression.ZipFile]::CreateFromDirectory($publish, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
        Assert-DodoArchive $archive
        $packages.Add($archive)
    }
    $sums = @($packages | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) })
    $sums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
    Write-Host "Packages: $output"
    Write-Host "Staging retained for inspection: $staging"
    [pscustomobject]@{ Version = $version; Directory = $output; Archives = $packages.ToArray(); Staging = $staging }
}
finally { Pop-Location }

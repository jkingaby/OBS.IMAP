#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64')][string] $RuntimeIdentifier,
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')][string] $Version = '0.1.0',
    [string] $OutputRoot = (Join-Path $PSScriptRoot '../artifacts/releases')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [IO.Path]::GetFullPath($OutputRoot)
$packageName = "obs-imap-$Version-$RuntimeIdentifier"
$packageRoot = Join-Path $outputPath $packageName
$project = Join-Path $repoRoot 'src/OBS.IMAP.Host/OBS.IMAP.Host.csproj'
$runtimeVersion = '10.0.12'
if (Test-Path -LiteralPath $packageRoot) { throw "Output already exists: $packageRoot. Choose a fresh OutputRoot; existing artifacts are never deleted." }
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

Push-Location $repoRoot
try {
    # Runtime-specific lock files must already be reviewed and committed. This command cannot update them.
    & dotnet publish $project --configuration Release --runtime $RuntimeIdentifier --self-contained true `
        -p:RestoreLockedMode=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
        "-p:RuntimeFrameworkVersion=$runtimeVersion" -p:TargetLatestRuntimePatch=false "-p:Version=$Version" --output $packageRoot
    if ($LASTEXITCODE -ne 0) { throw 'Locked standalone publishing failed.' }

    foreach ($name in @('LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'CHANGELOG.md', 'SECURITY.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $packageRoot
    }
    foreach ($name in @('docs', 'examples')) { Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $packageRoot -Recurse }

    $assets = Get-Content -LiteralPath (Join-Path $repoRoot 'src/OBS.IMAP.Host/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $packageFolders = @($assets.packageFolders.Keys)
    $inventory = [ordered]@{}
    foreach ($item in $assets.libraries.GetEnumerator() | Sort-Object Key) {
        if ($item.Value.type -eq 'package') { $inventory[$item.Key] = @{ Path = $item.Value.path; Kind = 'NuGet dependency (may include build-only dependencies)' } }
    }
    # Framework runtime packs are SDK downloads, not ordinary library nodes in project.assets.json.
    foreach ($id in @("Microsoft.NETCore.App.Runtime.$RuntimeIdentifier", "Microsoft.AspNetCore.App.Runtime.$RuntimeIdentifier")) {
        $inventory["$id/$runtimeVersion"] = @{ Path = "$($id.ToLowerInvariant())/$runtimeVersion"; Kind = 'Bundled .NET framework runtime pack' }
    }
    $licensesRoot = Join-Path $packageRoot 'third-party-licenses'
    New-Item -ItemType Directory -Path $licensesRoot | Out-Null
    $packages = [Collections.Generic.List[object]]::new()
    $relationships = [Collections.Generic.List[object]]::new()
    $licenseIndex = [Collections.Generic.List[string]]::new()
    $licenseIndex.Add('# Packaged dependency licenses')
    $licenseIndex.Add('')
    $licenseIndex.Add('Original license/notice files and embedded README license notices are copied from the restored NuGet cache. Where a package ships only a license expression, LICENSE-EXPRESSION.txt identifies that fact, the standard license text is supplied, and the package copyright is preserved from its nuspec. Original nuspec metadata is included for every dependency. This is a dependency inventory, including build-time packages; it is not a claim that every listed assembly is shipped.')
    $licenseIndex.Add('')
    $packages.Add([ordered]@{
        SPDXID = 'SPDXRef-Application'; name = 'OBS-IT IMAP Connector'; versionInfo = $Version
        supplier = 'Organization: OBS-IT'; downloadLocation = 'NOASSERTION'; filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'; licenseDeclared = 'Apache-2.0'; copyrightText = 'Copyright 2026 OBS-IT'
        primaryPackagePurpose = 'APPLICATION'; comment = "Self-contained $RuntimeIdentifier binary. OS libraries and container base images are outside this inventory."
    })
    $relationships.Add(@{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = 'SPDXRef-Application' })

    foreach ($item in $inventory.GetEnumerator()) {
        $packagePath = $null
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path $folder $item.Value.Path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packagePath = [IO.Path]::GetFullPath($candidate); break }
        }
        if (!$packagePath) { throw "Restored package missing: $($item.Key)" }
        $nuspec = @(Get-ChildItem -LiteralPath $packagePath -File -Filter '*.nuspec')
        if ($nuspec.Count -ne 1) { throw "Expected one nuspec: $($item.Key)" }
        [xml] $metadata = Get-Content -LiteralPath $nuspec[0].FullName -Raw
        $id = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="id"]').InnerText
        $packageVersion = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="version"]').InnerText
        $licenseNode = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="license"]')
        $copyrightNode = $metadata.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="copyright"]')
        $copyright = if ($copyrightNode) { $copyrightNode.InnerText } else { 'NOASSERTION' }
        $license = if ($licenseNode -and $licenseNode.GetAttribute('type') -eq 'expression') { $licenseNode.InnerText } else { 'NOASSERTION' }
        $destination = Join-Path $licensesRoot "$id-$packageVersion"
        New-Item -ItemType Directory -Path $destination | Out-Null
        Copy-Item -LiteralPath $nuspec[0].FullName -Destination (Join-Path $destination 'package.nuspec')
        Set-Content -LiteralPath (Join-Path $destination 'COPYRIGHT.txt') -Value $copyright -Encoding utf8NoBOM
        $hasLicense = $false
        foreach ($file in Get-ChildItem -LiteralPath $packagePath -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($packagePath, $file.FullName)
            $isLicense = $file.Name -match '^(license|copying)(\.|$)'
            $isNotice = $file.Name -match '^(notice|third.party.notices)(\.|$)'
            $isReadmeLicense = $file.Name -match '^readme(\.|$)' -and (Get-Content -LiteralPath $file.FullName -Raw) -match 'Permission is hereby granted, free of charge'
            if ($isLicense -or $isNotice -or $isReadmeLicense) {
                $target = Join-Path $destination $relative
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $target
                if ($isLicense -or $isReadmeLicense) { $hasLicense = $true }
            }
        }
        if (!$hasLicense) {
            Set-Content -LiteralPath (Join-Path $destination 'LICENSE-EXPRESSION.txt') -Encoding utf8NoBOM -Value "Package declares $license in its nuspec and ships no standalone license text. Standard expression license text is supplied separately; package copyright is preserved in COPYRIGHT.txt."
            if ($license -eq 'Apache-2.0') { Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $destination 'Apache-2.0.txt') }
            elseif ($license -eq 'MIT') {
                $runtimeLicense = $null
                foreach ($folder in $packageFolders) {
                    $candidate = Join-Path $folder "microsoft.netcore.app.runtime.$RuntimeIdentifier/$runtimeVersion/LICENSE.TXT"
                    if (Test-Path -LiteralPath $candidate) { $runtimeLicense = Get-Content -LiteralPath $candidate -Raw; break }
                }
                if (!$runtimeLicense) { throw 'Cannot locate the bundled runtime MIT license text.' }
                $start = $runtimeLicense.IndexOf('Permission is hereby granted', [StringComparison]::Ordinal)
                if ($start -lt 0) { throw 'Cannot identify standard MIT terms in runtime license.' }
                Set-Content -LiteralPath (Join-Path $destination 'MIT.txt') -Encoding utf8NoBOM -Value "MIT License`n`n$copyright`n`n$($runtimeLicense.Substring($start))"
            }
            else { throw "Manual license review required for $id ($license). No distributable license text was found." }
        }
        $licenseIndex.Add("- $id $packageVersion : $license; $($item.Value.Kind)")
        $spdxId = 'SPDXRef-Package-' + ("$id-$packageVersion" -replace '[^A-Za-z0-9.-]', '-')
        $nupkg = @(Get-ChildItem -LiteralPath $packagePath -File -Filter '*.nupkg')
        if ($nupkg.Count -ne 1) { throw "Expected original NuGet archive: $id" }
        $packages.Add([ordered]@{
            SPDXID = $spdxId; name = $id; versionInfo = $packageVersion
            downloadLocation = "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/$packageVersion/$($id.ToLowerInvariant()).$packageVersion.nupkg"
            filesAnalyzed = $false; licenseConcluded = 'NOASSERTION'; licenseDeclared = $license; copyrightText = $copyright
            checksums = @(@{ algorithm = 'SHA256'; checksumValue = (Get-FileHash -LiteralPath $nupkg[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
            externalRefs = @(@{ referenceCategory = 'PACKAGE-MANAGER'; referenceType = 'purl'; referenceLocator = "pkg:nuget/$id@$packageVersion" })
            primaryPackagePurpose = 'LIBRARY'; comment = $item.Value.Kind
        })
        $relationships.Add(@{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = $spdxId })
    }
    Set-Content -LiteralPath (Join-Path $licensesRoot 'INDEX.md') -Value $licenseIndex -Encoding utf8NoBOM
    $sbom = [ordered]@{
        spdxVersion = 'SPDX-2.3'; dataLicense = 'CC0-1.0'; SPDXID = 'SPDXRef-DOCUMENT'
        name = $packageName; documentNamespace = "https://obs-it.invalid/spdx/$packageName/$([Guid]::NewGuid())"
        creationInfo = @{ creators = @('Tool: OBS-IT Publish.ps1', 'Organization: OBS-IT'); created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') }
        comment = 'Application and restored NuGet dependency/runtime-pack inventory. Includes build-only NuGet dependencies. This is not a complete OS/container SBOM or source vulnerability/security attestation.'
        packages = @($packages.ToArray()); relationships = @($relationships.ToArray())
    }
    $sbom | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $packageRoot 'sbom.spdx.json') -Encoding utf8NoBOM
    # Verify the produced inventory is parseable before creating a package.
    $null = Get-Content -LiteralPath (Join-Path $packageRoot 'sbom.spdx.json') -Raw | ConvertFrom-Json
    $checksums = foreach ($file in Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\', '/')
        "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
    }
    Set-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS') -Value $checksums -Encoding utf8NoBOM
    if ($RuntimeIdentifier -eq 'win-x64') {
        $archive = Join-Path $outputPath "$packageName.zip"
        [IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    }
    else {
        # Managed tar creation preserves executable permissions even when cross-publishing on Windows.
        $archive = Join-Path $outputPath "$packageName.tar.gz"
        $fileStream = [IO.File]::Create($archive)
        $gzip = [IO.Compression.GZipStream]::new($fileStream, [IO.Compression.CompressionLevel]::Optimal)
        $tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName) {
                $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\', '/')
                $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, "$packageName/$relative")
                $entry.Mode = [IO.UnixFileMode] $(if ($relative -eq 'obs-imap') { 493 } else { 420 })
                $entry.DataStream = [IO.File]::OpenRead($file.FullName)
                try { $tar.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
            }
        }
        finally { $tar.Dispose(); $gzip.Dispose(); $fileStream.Dispose() }
    }
    Set-Content -LiteralPath "$archive.sha256" -Encoding utf8NoBOM -Value "$((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($archive))"
    Write-Output "Created $archive"
    Write-Output "SPDX inventory: $($packages.Count) packages; original and expression licenses: $licensesRoot"
}
finally { Pop-Location }

# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 HARMOVUE contributors

param(
 [Parameter(Mandatory=$true)][string]$PayloadDirectory,
 [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
 [string]$MakeNsis='E:\Codex\Programs\LocalMedia-NSIS\makensis.exe',
 [switch]$TestOnly,
 [string]$InstallerOutput
)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot
$payload=[IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
if([IO.Path]::GetPathRoot($payload) -ne 'E:\' -or [IO.Path]::GetPathRoot($evidence) -ne 'E:\'){throw 'Payload and build evidence must be on E:.'}
if(Test-Path -LiteralPath $evidence){throw 'Choose a new installer build evidence directory.'}
if(!(Test-Path -LiteralPath (Join-Path $payload 'LocalMedia.exe') -PathType Leaf)){throw 'Published application missing.'}
if(Test-Path -LiteralPath (Join-Path $payload 'Data')){throw 'Never package Data.'}
$entries=@(Get-ChildItem -LiteralPath $payload -Recurse -Force)
if(@($entries | Where-Object {($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}).Count){throw 'Payload contains reparse points.'}
$files=@($entries | Where-Object {!$_.PSIsContainer} | Sort-Object FullName)
New-Item -ItemType Directory -Path $evidence | Out-Null
$lines=[Collections.Generic.List[string]]::new()
$uninstall=[Collections.Generic.List[string]]::new()
$uninstallPreflight=[Collections.Generic.List[string]]::new()
$paths=[Collections.Generic.List[string]]::new()
foreach($file in $files){
 $relative=[IO.Path]::GetRelativePath($payload,$file.FullName)
 if($relative.StartsWith('..') -or $relative.Contains('"') -or $relative.Contains("`n") -or $relative.Contains("`r") -or $relative.Contains('*') -or $relative.Contains('?') -or $relative.Contains(':')){throw 'Unsafe payload name.'}
 $paths.Add($relative)
 $escaped=$relative.Replace('$','$$')
 $lines.Add((' Push "$INSTDIR\{0}"' -f $escaped))
 $lines.Add(' Call LocalMediaProbeTarget')
 $uninstall.Add((' Push "$INSTDIR\{0}"' -f $escaped))
 $uninstall.Add(' Call un.LocalMediaDeleteOwnedFile')
 $uninstallPreflight.Add((' Push "$INSTDIR\{0}"' -f $escaped))
 $uninstallPreflight.Add(' Call un.LocalMediaProbeOwnedFile')
}
$directories=@($entries | Where-Object {$_.PSIsContainer} | Sort-Object { $_.FullName.Length } -Descending)
foreach($directory in $directories){
 $relative=[IO.Path]::GetRelativePath($payload,$directory.FullName)
 if($relative.StartsWith('..') -or $relative.Contains('"') -or $relative.Contains("`n") -or $relative.Contains("`r") -or $relative.Contains('*') -or $relative.Contains('?') -or $relative.Contains(':')){throw 'Unsafe payload directory name.'}
 $relative=$relative.Replace('$','$$')
 $uninstall.Add((' Push "$INSTDIR\{0}"' -f $relative))
 $uninstall.Add(' Call un.LocalMediaRemoveEmptyOwnedDirectory')
}
$include=Join-Path $evidence 'payload-preflight.nsh'
[IO.File]::WriteAllLines($include,$lines,[Text.UTF8Encoding]::new($true))
$uninstallInclude=Join-Path $evidence 'payload-uninstall.nsh'
[IO.File]::WriteAllLines($uninstallInclude,$uninstall,[Text.UTF8Encoding]::new($true))
$uninstallPreflightInclude=Join-Path $evidence 'payload-uninstall-preflight.nsh'
[IO.File]::WriteAllLines($uninstallPreflightInclude,$uninstallPreflight,[Text.UTF8Encoding]::new($true))
$legacyInclude=Join-Path $evidence 'legacy-loose-cleanup.nsh'
$legacyCommands=[Collections.Generic.List[string]]::new()
$ownedPaths=[Collections.Generic.HashSet[string]]::new($paths,[StringComparer]::OrdinalIgnoreCase)
foreach($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'LegacyLoosePayload-1.0.2.sha256')){
 if(!$line -or $line.StartsWith('#')){continue}
 if($line -notmatch '^(?<hash>[0-9A-F]{64})  (?<name>.+)$'){throw 'Invalid legacy payload manifest.'}
 $relative=$Matches.name; $expected=$Matches.hash
 if([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|\\)(\.{1,2}|Data)(\\|$)|["$:*?<>|]'){
  throw 'Unsafe legacy payload path.'
 }
 if(!$ownedPaths.Contains($relative)){
  $legacyCommands.Add((' StrCpy $R0 "$INSTDIR\{0}"' -f $relative))
  $legacyCommands.Add((' StrCpy $R1 "{0}"' -f $expected))
  $legacyCommands.Add(' Call LocalMediaRetireMatchingFile')
 }
}
[IO.File]::WriteAllLines($legacyInclude,$legacyCommands,[Text.UTF8Encoding]::new($true))
$env:TEMP='E:\Codex\Temp';$env:TMP=$env:TEMP
$compilerArguments=@('/V3',"/DPAYLOAD_DIR=$payload","/DPREFLIGHT_INCLUDE=$include","/DUNINSTALL_INCLUDE=$uninstallInclude","/DUNINSTALL_PREFLIGHT_INCLUDE=$uninstallPreflightInclude","/DLEGACY_LOOSE_INCLUDE=$legacyInclude")
$mpvHash=(Get-FileHash -LiteralPath (Join-Path $payload 'libmpv-2.dll')).Hash
$sqliteHash=(Get-FileHash -LiteralPath (Join-Path $payload 'e_sqlite3.dll')).Hash
[xml]$sourceProject=Get-Content -LiteralPath (Join-Path $project 'src\LocalMedia.csproj') -Raw
$version=@($sourceProject.Project.PropertyGroup.Version|Where-Object {$_})[0]
if($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$'){throw 'Invalid installer version.'}
$compilerArguments+=@("/DMPV_SHA256=$mpvHash","/DSQLITE_SHA256=$sqliteHash","/DAPP_VERSION=$version")
if($TestOnly){$compilerArguments+=@('/DLOCALMEDIA_TEST_INSTALLER',('/DINSTALLER_OUTFILE='+(Join-Path $evidence 'HARMOVUE-test-Setup.exe')))}
if($InstallerOutput){
 if($TestOnly){throw 'Use the generated path for a test-only installer.'}
 $InstallerOutput=[IO.Path]::GetFullPath($InstallerOutput)
 if([IO.Path]::GetPathRoot($InstallerOutput) -ne 'E:\' -or [IO.Path]::GetExtension($InstallerOutput) -ne '.exe' -or (Test-Path -LiteralPath $InstallerOutput)){
  throw 'Choose a new installer .exe output on E:.'
 }
 $compilerArguments+=("/DINSTALLER_OUTFILE=$InstallerOutput")
}
$compilerArguments+=(Join-Path $PSScriptRoot 'installer.nsi')
& $MakeNsis @compilerArguments *> (Join-Path $evidence 'makensis.log')
$result=[ordered]@{Payload=$payload;PayloadFiles=$paths;PreflightInclude=$include;PreflightSHA256=(Get-FileHash -LiteralPath $include).Hash;UninstallInclude=$uninstallInclude;UninstallSHA256=(Get-FileHash -LiteralPath $uninstallInclude).Hash;UninstallPreflightSHA256=(Get-FileHash -LiteralPath $uninstallPreflightInclude).Hash;LegacyCleanupFileCount=($legacyCommands.Count / 3);TestOnly=[bool]$TestOnly;Compiler=$MakeNsis;ExitCode=$LASTEXITCODE}
[IO.File]::WriteAllText((Join-Path $evidence 'compile-result.json'),($result | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
if($LASTEXITCODE){throw 'Installer build failed; see makensis.log.'}
[pscustomobject]@{PayloadFiles=$files.Count;Evidence=$evidence;ExitCode=0}

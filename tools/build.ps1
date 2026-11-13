# SPDX-License-Identifier: GPL-3.0-or-later
param([string]$OutputDirectory='',[string]$NativeLibrary='',[string]$ExpectedNativeSHA256='',
      [string]$HelperCompiler='', [string]$DotNet='dotnet',[string]$CacheRoot='',[string]$PackageCache='',[switch]$SourceOnly)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo 'artifacts\app'}
if(!$CacheRoot){$CacheRoot=Join-Path $repo '.cache'}
if(!$PackageCache){$PackageCache=Join-Path $CacheRoot 'packages'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$CacheRoot=[IO.Path]::GetFullPath($CacheRoot)
if(@(Get-ChildItem -LiteralPath $OutputDirectory -Force -ErrorAction SilentlyContinue).Count){throw 'Choose an empty output directory. Data and existing files are never removed.'}
if(!$NativeLibrary -and !$SourceOnly){$NativeLibrary=Join-Path $repo 'native\libmpv-2.dll'}
$nativeHash=$null
if($NativeLibrary){
 $NativeLibrary=[IO.Path]::GetFullPath($NativeLibrary)
 if(!(Test-Path -LiteralPath $NativeLibrary -PathType Leaf)){throw 'Supply a Windows x64 libmpv-2.dll. See docs/BUILD.md.'}
 $nativeHash=(Get-FileHash -LiteralPath $NativeLibrary).Hash
 if($ExpectedNativeSHA256 -and $nativeHash -ine $ExpectedNativeSHA256){throw 'Native library SHA256 mismatch.'}
}
foreach($path in @($OutputDirectory,$CacheRoot,$PackageCache)){New-Item -ItemType Directory -Path $path -Force|Out-Null}
$env:DOTNET_CLI_HOME=Join-Path $CacheRoot 'cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:NUGET_PACKAGES=[IO.Path]::GetFullPath($PackageCache)
$env:NUGET_HTTP_CACHE_PATH=Join-Path $CacheRoot 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH=Join-Path $CacheRoot 'nuget-plugins'
$env:NUGET_SCRATCH=Join-Path $CacheRoot 'nuget-scratch'
$env:TEMP=Join-Path $CacheRoot 'temp';$env:TMP=$env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force|Out-Null
$helper=Join-Path $repo 'native\Harmovue.NativePrewarm.exe'
if($HelperCompiler){& (Join-Path $PSScriptRoot 'native-prewarm\Build.ps1') -Compiler $HelperCompiler -OutputDirectory (Join-Path $repo 'native')}
if(!$SourceOnly -and !(Test-Path -LiteralPath $helper)){Write-Warning 'Optional warmup helper is absent. Playback falls back to the normal loader; historical timings do not apply.'}
$publish=@('publish',(Join-Path $repo 'src\LocalMedia.csproj'),'-c','Release','-r','win-x64','--self-contained','true',
 '-p:RestoreLockedMode=true','-p:PublishSingleFile=true','-p:EnableCompressionInSingleFile=true',
 '-p:EnableSingleFileAnalyzer=false','-p:UseSharedCompilation=false','-nodeReuse:false','-o',$OutputDirectory)
if($SourceOnly){
 $publish+=('-p:MpvLibraryPath='+(Join-Path $CacheRoot 'source-only-no-mpv.dll'))
 $publish+=('-p:NativeWarmupPath='+(Join-Path $CacheRoot 'source-only-no-helper.exe'))
}else{$publish+=('-p:MpvLibraryPath='+$NativeLibrary)}
Push-Location $repo
try{
 $sdk=(& $DotNet --version|Out-String).Trim()
 if($LASTEXITCODE -or $sdk -ne '10.0.402'){throw 'Install .NET SDK 10.0.402 specified by global.json.'}
 & $DotNet @publish
 if($LASTEXITCODE){throw 'Publish failed.'}
}finally{Pop-Location}
[ordered]@{SourceOnly=[bool]$SourceOnly;NativeSHA256=$nativeHash;WarmupHelperIncluded=(!$SourceOnly -and (Test-Path -LiteralPath $helper));Sdk=$sdk;PublicBinaryReleaseApproved=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'BUILD_PROVENANCE.json') -Encoding utf8
Write-Output ('Build output: '+$OutputDirectory)

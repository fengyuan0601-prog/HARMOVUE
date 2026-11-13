# SPDX-License-Identifier: GPL-3.0-or-later
param([Parameter(Mandatory)][string]$Compiler,[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
if(!$OutputDirectory){$OutputDirectory=Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'native'}
if(!(Test-Path -LiteralPath $Compiler -PathType Leaf)){throw 'Pass an existing x64 MinGW-w64 gcc executable. This script installs nothing.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$target=Join-Path $OutputDirectory 'Harmovue.NativePrewarm.exe'
$source=Join-Path $PSScriptRoot 'native-prewarm.c'
$priorPath=$env:PATH
try{
 $env:PATH=(Split-Path $Compiler)+';'+$priorPath
 & $Compiler '-Os' '-s' '-nostdlib' '-fno-stack-protector' '-fno-builtin' '-fno-tree-loop-distribute-patterns' '-fno-ident' '-Wl,--entry,entry,--subsystem,windows,--no-insert-timestamp,--nxcompat,--dynamicbase' '-o' $target $source '-lkernel32'
 if($LASTEXITCODE){throw 'Warmup helper compilation failed.'}
}finally{$env:PATH=$priorPath}
Get-FileHash -LiteralPath $target

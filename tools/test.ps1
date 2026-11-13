# SPDX-License-Identifier: GPL-3.0-or-later
param([Parameter(Mandatory)][string]$Application,[string]$WorkDirectory='')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
if(!$WorkDirectory){$WorkDirectory=Join-Path $repo ('artifacts\tests-'+[guid]::NewGuid().ToString('N'))}
if(Test-Path -LiteralPath $WorkDirectory){throw 'Choose a fresh test directory. Existing data is never overwritten.'}
$Application=[IO.Path]::GetFullPath($Application)
New-Item -ItemType Directory -Path $WorkDirectory|Out-Null
$cases=@(@{Name='search';Argument='--test-search';Log='search-test.txt'},@{Name='library-data';Argument='--test-library-data';Log='library-data-test.txt'})
$results=@()
foreach($case in $cases){
 $profile=Join-Path $WorkDirectory $case.Name
 $start=[Diagnostics.ProcessStartInfo]::new($Application)
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=Split-Path $Application
 $start.Environment['LOCALMEDIA_DATA']=$profile
 $start.Environment['TEMP']=Join-Path $WorkDirectory 'temp';$start.Environment['TMP']=$start.Environment['TEMP']
 $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=Join-Path $WorkDirectory 'extract'
 New-Item -ItemType Directory -Path $start.Environment['TEMP'] -Force|Out-Null
 $start.ArgumentList.Add($case.Argument)
 $process=[Diagnostics.Process]::Start($start);$null=$process.Handle
 if(!$process.WaitForExit(120000)){$process.Kill();$process.WaitForExit();throw ('Owned test timed out: '+$case.Name)}
 if($process.ExitCode -ne 0){throw ('Test failed: '+$case.Name+' exit '+$process.ExitCode)}
 $lines=@(Get-Content -LiteralPath (Join-Path $profile $case.Log))
 if(@($lines|Where-Object {$_ -like 'FAIL *'}).Count){throw ('Failed assertion: '+$case.Name)}
 $count=@($lines|Where-Object {$_ -like 'PASS *'}).Count
 if(!$count){throw 'No actual assertions were reported.'}
 $results+=[pscustomobject]@{Name=$case.Name;Passed=$true;Assertions=$count;ExitCode=$process.ExitCode}
 $process.Dispose()
}
$results|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $WorkDirectory 'RESULTS.json') -Encoding utf8
$results|Format-Table
Write-Output 'Search/data tests only; no decoding, audible output, visible video or cold-start acceptance claim.'

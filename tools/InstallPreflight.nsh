; SPDX-License-Identifier: GPL-3.0-or-later
; Copyright (C) 2026 HARMOVUE contributors

!ifndef LOCALMEDIA_INSTALL_PREFLIGHT
!define LOCALMEDIA_INSTALL_PREFLIGHT
!include "FileFunc.nsh"
Var InstallGuardMutex
Var InstallBlockedFile
Var InstallBlockedError
Var InstallCheckOnly
!include "InstallTargetSafety.nsh"

Function .onInit
 ; Keep an explicit/registered target. Prefer E: on the development machine;
 ; a new user without E: gets a writable standard per-user location.
 ReadRegStr $0 HKCU "Software\LocalMedia" "InstallDir"
 StrCmp $0 "" 0 lm_default_ready
 StrCmp $INSTDIR "E:\Codex\Programs\LocalMedia" 0 lm_default_ready
 ; A DVD mounted as E: is not a usable program/data volume.
 System::Call 'kernel32::GetDriveTypeW(w "E:\") i.r0'
 StrCmp $0 2 lm_default_check_drive
 StrCmp $0 3 lm_default_check_drive lm_default_no_drive
 lm_default_check_drive:
 System::Call 'kernel32::GetFileAttributesW(w "E:\") i.r0'
 IntCmp $0 -1 lm_default_no_drive lm_default_ready lm_default_ready
 lm_default_no_drive:
 System::Call 'kernel32::GetCommandLineW() w.r0'
 ClearErrors
 ${GetOptions} $0 "/D=" $1
 IfErrors 0 lm_default_ready
 StrCpy $INSTDIR "$LOCALAPPDATA\Programs\HARMOVUE"
 lm_default_ready:
 StrCpy $InstallCheckOnly 0
 ${GetParameters} $0
 ClearErrors
 ${GetOptions} $0 "/PRECHECKONLY" $1
 IfErrors +2
 StrCpy $InstallCheckOnly 1
 Call LocalMediaValidateRequestedTarget
 System::Call 'kernel32::CreateMutexW(p 0, i 1, w "Local\HARMOVUE.Setup") p.r0 ?e'
 Pop $1
 StrCpy $InstallGuardMutex $0
 StrCmp $0 0 lm_mutex_failed
 StrCmp $1 183 lm_mutex_busy
 Return
 lm_mutex_busy:
 IfSilent +2
 MessageBox MB_OK|MB_ICONEXCLAMATION "另一个 HARMOVUE 安装程序正在运行，请先关闭它。"
 SetErrorLevel 1618
 Quit
 lm_mutex_failed:
 IfSilent +2
 MessageBox MB_OK|MB_ICONEXCLAMATION "无法建立安装保护，安装尚未开始。请稍后重试。"
 SetErrorLevel 5
 Quit
FunctionEnd

; Only OPEN_EXISTING is used. No content is written, created or truncated.
; The generated list contains program payload files, never the user's Data.
!macro LocalMediaDefineTargetProbe PREFIX
Function ${PREFIX}LocalMediaProbeTarget
 Exch $0
 Push $1
 Push $2
 StrCmp $InstallBlockedFile "" 0 lm_probe_done
 StrCpy $LocalMediaCheckedPath $0
 Call ${PREFIX}LocalMediaCheckPath
 StrCmp $LocalMediaPathSafe 1 lm_probe_path_ready
 StrCpy $InstallBlockedFile $0
 StrCpy $InstallBlockedError 87
 Goto lm_probe_done
 lm_probe_path_ready:
 IfFileExists "$0" 0 lm_probe_done
 System::Call 'kernel32::CreateFileW(w r0, i 0x40000000, i 0, p 0, i 3, i 0x80, p 0) p.r1 ?e'
 Pop $2
 IntCmp $1 -1 lm_probe_blocked
 System::Call 'kernel32::CloseHandle(p r1)'
 Goto lm_probe_done
 lm_probe_blocked:
 StrCpy $InstallBlockedFile $0
 StrCpy $InstallBlockedError $2
 StrCmp $InstallBlockedError 0 0 lm_probe_done
 StrCpy $InstallBlockedError 32
 lm_probe_done:
 Pop $2
 Pop $1
 Pop $0
FunctionEnd
!macroend
!insertmacro LocalMediaDefineTargetProbe ""
!insertmacro LocalMediaDefineTargetProbe "un."

Function LocalMediaCheckTargetReady
 lm_preflight_retry:
 StrCpy $InstallBlockedFile ""
 StrCpy $InstallBlockedError 0
 !include "${PREFLIGHT_INCLUDE}"
 Push "$INSTDIR\Uninstall.exe"
 Call LocalMediaProbeTarget
 StrCmp $InstallBlockedFile "" lm_preflight_ready
 DetailPrint "Installation stopped before copying files: $InstallBlockedFile (Windows $InstallBlockedError)"
 IfSilent lm_preflight_cancel
 MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "安装目录中的文件正在被占用，或暂时无法写入：$\r$\n$InstallBlockedFile$\r$\n$\r$\n请先退出 HARMOVUE（包括托盘），再点“重试”。若已退出，请检查目录是否可写。$\r$\n$\r$\n安装尚未开始，播放器数据不会被覆盖。" /SD IDCANCEL IDRETRY lm_preflight_retry
 lm_preflight_cancel:
 SetErrorLevel $InstallBlockedError
 Quit
 lm_preflight_ready:
 DetailPrint "Program files are ready for installation."
FunctionEnd
!endif

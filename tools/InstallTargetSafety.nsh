; SPDX-License-Identifier: GPL-3.0-or-later
; Copyright (C) 2026 HARMOVUE contributors

!ifndef LOCALMEDIA_INSTALL_TARGET_SAFETY
!define LOCALMEDIA_INSTALL_TARGET_SAFETY
!include "FileFunc.nsh"
Var LocalMediaCheckedPath
Var LocalMediaPathSafe

; Never follow a junction or symbolic link in the selected target or any
; generated payload path. Removal below is exact and never recursive.
!macro LocalMediaDefinePathCheck PREFIX
Function ${PREFIX}LocalMediaCheckPath
 Push $0
 Push $1
 Push $2
 Push $3
 StrCpy $LocalMediaPathSafe 0
 StrCmp $LocalMediaCheckedPath "" lm_path_done
 ; NSIS GetFullPathName can return an empty string for a not-yet-created
 ; directory. The Win32 lexical API also handles fresh install targets.
 System::Call 'kernel32::GetFullPathNameW(w "$LocalMediaCheckedPath", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r3'
 IntCmp $3 0 lm_path_done
 IntCmp $3 ${NSIS_MAX_STRLEN} lm_path_done 0 lm_path_done
 StrCmp $0 "" lm_path_done
 StrCpy $2 0
 lm_path_character:
 StrCpy $3 $0 1 $2
 StrCmp $3 "" lm_path_characters_ok
 StrCmp $3 '*' lm_path_done
 StrCmp $3 '?' lm_path_done
 StrCmp $3 '"' lm_path_done
 StrCmp $3 '<' lm_path_done
 StrCmp $3 '>' lm_path_done
 StrCmp $3 '|' lm_path_done
 StrCmp $3 '$\r' lm_path_done
 StrCmp $3 '$\n' lm_path_done
 StrCmp $3 ':' 0 lm_path_next_character
 IntCmp $2 1 lm_path_next_character lm_path_done lm_path_done
 lm_path_next_character:
 IntOp $2 $2 + 1
 Goto lm_path_character
 lm_path_characters_ok:
 ${GetRoot} "$0" $1
 StrCmp $0 $1 lm_path_done
 StrCmp $0 "$1\" lm_path_done
 lm_path_parent:
 System::Call 'kernel32::GetFileAttributesW(w r0) i.r2'
 IntCmp $2 -1 lm_path_next
 IntOp $3 $2 & 0x400
 IntCmp $3 0 0 lm_path_done lm_path_done
 lm_path_next:
 ${GetParent} "$0" $1
 StrCmp $0 $1 lm_path_ok
 StrCmp $1 "" lm_path_ok
 StrCpy $0 $1
 Goto lm_path_parent
 lm_path_ok:
 StrCpy $LocalMediaPathSafe 1
 lm_path_done:
 Pop $3
 Pop $2
 Pop $1
 Pop $0
FunctionEnd
!macroend
!insertmacro LocalMediaDefinePathCheck ""
!insertmacro LocalMediaDefinePathCheck "un."

Function LocalMediaValidateRequestedTarget
 ; NSIS removes /D= from $CMDLINE and silently falls back to InstallDir when
 ; a drive root is supplied. Check the original process command line too.
 System::Call 'kernel32::GetCommandLineW() w.r0'
 ClearErrors
 ${GetOptions} $0 "/D=" $1
 IfErrors lm_requested_ok
 StrCpy $LocalMediaCheckedPath $1
 Call LocalMediaCheckPath
 StrCmp $LocalMediaPathSafe 1 0 lm_requested_reject
 System::Call 'kernel32::GetFullPathNameW(w r1, i ${NSIS_MAX_STRLEN}, w .r2, p 0) i.r3'
 System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r3'
 ; Ignore an ordinary trailing separator, but never silently install in the
 ; default folder if NSIS discarded a malformed /D= value.
 lm_requested_trim:
 StrCpy $3 $2 1 -1
 StrCmp $3 '\' 0 lm_actual_trim
 StrCpy $2 $2 -1
 Goto lm_requested_trim
 lm_actual_trim:
 StrCpy $3 $0 1 -1
 StrCmp $3 '\' 0 lm_requested_compare
 StrCpy $0 $0 -1
 Goto lm_actual_trim
 lm_requested_compare:
 StrCmp $2 $0 lm_requested_ok
 lm_requested_reject:
 IfSilent +2
 MessageBox MB_OK|MB_ICONEXCLAMATION "指定的安装路径无效。请选择专用的文件夹，不能使用磁盘根目录或目录链接。"
 SetErrorLevel 87
 Quit
 lm_requested_ok:
FunctionEnd

Function LocalMediaValidateInstallDirectory
 System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r1'
 IntCmp $1 0 lm_target_reject
 IntCmp $1 ${NSIS_MAX_STRLEN} lm_target_reject 0 lm_target_reject
 StrCpy $INSTDIR $0
 StrCpy $LocalMediaCheckedPath $INSTDIR
 Call LocalMediaCheckPath
 StrCmp $LocalMediaPathSafe 1 0 lm_target_reject
 System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR") i.r0'
 IntCmp $0 -1 lm_target_enumerate
 IntOp $0 $0 & 0x10
 StrCmp $0 0 lm_target_reject
 ; An uninstall intentionally keeps Data and this product marker. Recognize
 ; that dedicated former installation without admitting arbitrary folders.
 IfFileExists "$INSTDIR\HARMOVUE.install" 0 lm_target_existing_program
 StrCpy $LocalMediaCheckedPath "$INSTDIR\HARMOVUE.install"
 Call LocalMediaCheckPath
 StrCmp $LocalMediaPathSafe 1 0 lm_target_reject
 ClearErrors
 FileOpen $0 "$INSTDIR\HARMOVUE.install" r
 IfErrors lm_target_reject
 FileSeek $0 0 END $2
 FileSeek $0 0 SET
 StrCmp $2 34 0 lm_marker_close
 FileRead $0 $1
 FileClose $0
 StrCmp $1 "HARMOVUE.LocalMedia.InstallRoot.v1" lm_target_ok lm_target_existing_program
 lm_marker_close:
 FileClose $0
 lm_target_existing_program:
 ; Existing legitimate installations may contain Data, backups and other
 ; user extras. A different nonempty directory is never reused implicitly.
 IfFileExists "$INSTDIR\LocalMedia.exe" 0 lm_target_enumerate
 IfFileExists "$INSTDIR\LocalMedia.dll" lm_target_ok
 IfFileExists "$INSTDIR\Uninstall.exe" 0 lm_target_enumerate
 ReadRegStr $0 HKCU "Software\LocalMedia" "InstallDir"
 StrCmp $0 "$INSTDIR" lm_target_ok
 lm_target_enumerate:
 Push $0
 Push $1
 FindFirst $0 $1 "$INSTDIR\*.*"
 IfErrors lm_target_empty
 lm_target_entry:
 StrCmp $1 "." lm_target_continue
 StrCmp $1 ".." lm_target_continue
 StrCmp $1 "" lm_target_empty
 FindClose $0
 Pop $1
 Pop $0
 Goto lm_target_reject
 lm_target_continue:
 FindNext $0 $1
 IfErrors lm_target_empty
 Goto lm_target_entry
 lm_target_empty:
 FindClose $0
 Pop $1
 Pop $0
 lm_target_ok:
 Return
 lm_target_reject:
 IfSilent +2
 MessageBox MB_OK|MB_ICONEXCLAMATION "请选择专用的空文件夹，或现有的 HARMOVUE 安装目录。不能使用磁盘根目录、含其他文件的目录或目录链接。$\r$\n$\r$\n尚未写入任何文件。"
 SetErrorLevel 87
 Quit
FunctionEnd

Function un.LocalMediaDeleteOwnedFile
 Pop $LocalMediaCheckedPath
 Call un.LocalMediaCheckPath
 StrCmp $LocalMediaPathSafe 1 0 lm_delete_skip
 Delete "$LocalMediaCheckedPath"
 Return
 lm_delete_skip:
 DetailPrint "Preserved an unexpected linked path: $LocalMediaCheckedPath"
FunctionEnd

Function un.LocalMediaRemoveEmptyOwnedDirectory
 Pop $LocalMediaCheckedPath
 Call un.LocalMediaCheckPath
 StrCmp $LocalMediaPathSafe 1 0 lm_remove_done
 RMDir "$LocalMediaCheckedPath"
 lm_remove_done:
FunctionEnd

Function un.LocalMediaProbeOwnedFile
 Pop $LocalMediaCheckedPath
 Call un.LocalMediaCheckPath
 StrCmp $LocalMediaPathSafe 1 0 lm_probe_owned_done
 Push "$LocalMediaCheckedPath"
 Call un.LocalMediaProbeTarget
 lm_probe_owned_done:
FunctionEnd

Function un.onInit
 System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r1'
 IntCmp $1 0 lm_uninstall_reject
 IntCmp $1 ${NSIS_MAX_STRLEN} lm_uninstall_reject 0 lm_uninstall_reject
 StrCpy $INSTDIR $0
 StrCpy $LocalMediaCheckedPath $INSTDIR
 Call un.LocalMediaCheckPath
 StrCmp $LocalMediaPathSafe 1 lm_uninstall_ok
 lm_uninstall_reject:
 IfSilent +2
 MessageBox MB_OK|MB_ICONEXCLAMATION "安装目录包含目录链接或不安全的目标。没有删除文件。"
 SetErrorLevel 87
 Quit
 lm_uninstall_ok:
 StrCpy $InstallBlockedFile ""
 StrCpy $InstallBlockedError 0
 !include "${UNINSTALL_PREFLIGHT_INCLUDE}"
 StrCmp $InstallBlockedFile "" lm_uninstall_ready
 IfSilent +2
 MessageBox MB_OK|MB_ICONEXCLAMATION "程序文件正在使用中或无法删除。请先退出 HARMOVUE（包括托盘），再重新卸载。$\r$\n$\r$\n尚未删除任何程序文件。"
 SetErrorLevel $InstallBlockedError
 Quit
 lm_uninstall_ready:
FunctionEnd
!endif

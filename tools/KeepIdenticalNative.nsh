; SPDX-License-Identifier: GPL-3.0-or-later
; Copyright (C) 2026 HARMOVUE contributors

!ifndef LOCALMEDIA_KEEP_IDENTICAL_NATIVE
!define LOCALMEDIA_KEEP_IDENTICAL_NATIVE

; Read-only SHA-256 using the Windows CNG provider and the bundled NSIS System
; plugin. No PowerShell child, key containers, new dependency or security change.
; Stack: full path -> uppercase digest, or empty on any open/read/hash failure.
; All working registers are preserved. Caller treats empty as "must replace".
Function LocalMediaNativeSHA256
 Exch $0
 Push $1
 Push $2
 Push $3
 Push $4
 Push $5
 Push $6
 Push $7
 Push $8
 Push $9
 StrCpy $1 -1
 StrCpy $2 0
 StrCpy $3 0
 StrCpy $4 0
 StrCpy $9 ""
 ClearErrors
 FileOpen $1 "$0" r
 IfErrors lm_hash_cleanup
 System::Call 'bcrypt::BCryptOpenAlgorithmProvider(*p.r2, w "SHA256", p 0, i 0) i.r6'
 StrCmp $6 0 0 lm_hash_cleanup
 System::Call 'bcrypt::BCryptCreateHash(p r2, *p.r3, p 0, i 0, p 0, i 0, i 0) i.r6'
 StrCmp $6 0 0 lm_hash_cleanup
 System::Alloc 65536
 Pop $4
 StrCmp $4 0 lm_hash_cleanup
 lm_hash_read:
 System::Call 'kernel32::ReadFile(p r1, p r4, i 65536, *i.r5, p 0) i.r6'
 StrCmp $6 0 lm_hash_cleanup
 StrCmp $5 0 lm_hash_finish
 System::Call 'bcrypt::BCryptHashData(p r3, p r4, i r5, i 0) i.r6'
 StrCmp $6 0 lm_hash_read lm_hash_cleanup
 lm_hash_finish:
 System::Call 'bcrypt::BCryptFinishHash(p r3, p r4, i 32, i 0) i.r6'
 StrCmp $6 0 0 lm_hash_cleanup
 StrCpy $5 0
 lm_hash_hex:
 IntOp $7 $4 + $5
 System::Call '*$7(&i1 .r8)'
 IntOp $8 $8 & 0xff
 IntFmt $8 "%02X" $8
 StrCpy $9 "$9$8"
 IntOp $5 $5 + 1
 IntCmp $5 32 lm_hash_cleanup lm_hash_hex lm_hash_cleanup
 lm_hash_cleanup:
 StrCmp $4 0 +2
 System::Free $4
 StrCmp $3 0 +2
 System::Call 'bcrypt::BCryptDestroyHash(p r3)'
 StrCmp $2 0 +2
 System::Call 'bcrypt::BCryptCloseAlgorithmProvider(p r2, i 0)'
 StrCmp $1 -1 +2
 FileClose $1
 StrCpy $0 $9
 Pop $9
 Pop $8
 Pop $7
 Pop $6
 Pop $5
 Pop $4
 Pop $3
 Pop $2
 Pop $1
 Exch $0
FunctionEnd

; Use once for each unique plain filename. SetOutPath must already be $INSTDIR.
; SOURCE is the build-time payload file; EXPECTED_SHA256 must be its SHA-256.
; The caller excludes these files from its normal File /r instruction.
!macro KeepIdenticalNative SOURCE NAME EXPECTED_SHA256
 !if "${EXPECTED_SHA256}" == ""
  !error "KeepIdenticalNative requires the payload SHA-256; an empty value must never match a read failure."
 !endif
 Push $R0
 Push "$INSTDIR\${NAME}"
 Call LocalMediaNativeSHA256
 Pop $R0
 StrCmp $R0 "${EXPECTED_SHA256}" lm_keep_${NAME}
 SetOutPath "$INSTDIR"
 SetOverwrite on
 File /oname=${NAME} "${SOURCE}"
 Push "$INSTDIR\${NAME}"
 Call LocalMediaNativeSHA256
 Pop $R0
 StrCmp $R0 "${EXPECTED_SHA256}" lm_native_done_${NAME}
 SetErrorLevel 2
 Abort "Cannot verify installed ${NAME}"
 lm_keep_${NAME}:
 DetailPrint "Preserved identical ${NAME}"
 lm_native_done_${NAME}:
 Pop $R0
!macroend
!endif

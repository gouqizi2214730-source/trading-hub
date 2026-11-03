; Instalador de KVP (edición de Trading Hub) para Windows: por usuario (no pide permisos de administrador), con accesos en el menú
; Inicio y el escritorio, desinstalador en «Aplicaciones instaladas» y la opción de abrirla al terminar. Habla el idioma
; de Windows si es español o chino; si no, inglés (la app hace lo mismo la primera vez que se abre).
;
; Lo arman build-windows.ps1 (en Windows) y build-windows.sh (en Mac o Linux, con «brew install makensis»):
;   makensis -DVERSION=1.1.0 -DARCH=x64 -DSRC=<carpeta con TradingHub.exe publicado> -DOUT=<instalador.exe> installer/TradingHub.nsi
;
; Los datos de la app (%APPDATA%\Trading Hub y Documentos\Trading Hub) no se tocan al actualizar ni al desinstalar.

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "StrFunc.nsh"
${StrStr}
${UnStrStr}

!ifndef VERSION
  !define VERSION "1.1.0"
!endif
; VIProductVersion necesita 4 números: 1.2.0 → 1.2.0.0; los empaquetados de esta copia ya traen 4 (1.2.0.1)
!ifndef VIVERSION
  !define VIVERSION "${VERSION}.0"
!endif
!ifndef ARCH
  !define ARCH "x64"
!endif
!ifndef SRC
  !error "Falta -DSRC=<carpeta con TradingHub.exe publicado>"
!endif
!ifndef OUT
  !define OUT "KVP-Setup-${ARCH}.exe"
!endif

!ifndef FILELIST
  !error "Falta -DFILELIST=<lista de archivos del programa> (la arma scripts/build-release.sh)"
!endif
!include "${FILELIST}"

; Borra solo lo que es del programa: los archivos de esta versión, las DLL/EXE/PDB sueltas de versiones anteriores y sus
; subcarpetas de idiomas. Nunca la carpeta entera: si el usuario guardó datos de mercado, indicadores o ajustes adentro
; (por ejemplo en D:\Trading Hub), quedan intactos.
!macro RemoveProgram DIR
  !insertmacro RemoveAppFiles "${DIR}"
  Delete "${DIR}\*.dll"
  Delete "${DIR}\*.exe"
  Delete "${DIR}\*.pdb"
  Delete "${DIR}\TradingHub.deps.json"
  Delete "${DIR}\TradingHub.runtimeconfig.json"
  Delete "${DIR}\Desinstalar Trading Hub.exe"
  Delete "${DIR}\Desinstalar KVP.exe"
  RMDir "${DIR}"
!macroend

!define NAME "KVP"
; el ejecutable conserva el nombre del ensamblado original (los recursos de la interfaz lo llevan adentro: avares://TradingHub/)
!define EXE "TradingHub.exe"
; la edición anterior se llamaba así: también se espera a que cierre y se borra
!define OLDEXE "TradingHub.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\TradingHub"

Name "${NAME}"
OutFile "${OUT}"
; en su propia carpeta; actualizar y desinstalar borran solo los archivos del programa (ver RemoveProgram)
InstallDir "$LOCALAPPDATA\Programs\KVP"
; una actualización se instala donde ya estaba (la carpeta que se eligió la primera vez)
InstallDirRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\TradingHub" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ManifestDPIAware true
BrandingText "KVP ${VERSION}"
VIProductVersion "${VIVERSION}"
VIAddVersionKey "ProductName" "${NAME}"
VIAddVersionKey "FileDescription" "${NAME} Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "MIT License with Commons Clause"

!define MUI_ICON "..\src\TradingHub.App\Assets\tradinghub.ico"
!define MUI_UNICON "..\src\TradingHub.App\Assets\tradinghub.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "$(OpenApp)"

!insertmacro MUI_PAGE_WELCOME
; elegir la carpeta (al buscar con «Examinar» se agrega «\Trading Hub» solo, así siempre queda en una carpeta propia)
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE CheckInstallDir
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
; el primero es el de un Windows en otro idioma
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Spanish"
!insertmacro MUI_LANGUAGE "SimpChinese"

LangString WelcomeText ${LANG_ENGLISH} "This installs KVP ${VERSION} on your PC.$\r$\n$\r$\nIf you already have it, it gets updated: your connections, charts, strategies and indicators stay as they are.$\r$\n$\r$\nClick Install to continue."
LangString WelcomeText ${LANG_SPANISH} "Esto instala KVP ${VERSION} en tu PC.$\r$\n$\r$\nSi ya la tenés, se actualiza: tus conexiones, gráficos, estrategias e indicadores quedan como están.$\r$\n$\r$\nTocá Instalar para seguir."
LangString WelcomeText ${LANG_SIMPCHINESE} "这将在你的电脑上安装 KVP ${VERSION}。$\r$\n$\r$\n如果已经安装，会直接更新：你的连接、图表、策略和指标都保持不变。$\r$\n$\r$\n点击“安装”继续。"
LangString NotWritable ${LANG_ENGLISH} "Can't write to this folder (it may need administrator permissions, like Program Files). Choose another folder, for example on D:\ or inside your user folder."
LangString NotWritable ${LANG_SPANISH} "No se puede escribir en esta carpeta (puede pedir permisos de administrador, como Archivos de programa). Elegí otra, por ejemplo en D:\ o dentro de tu carpeta de usuario."
LangString NotWritable ${LANG_SIMPCHINESE} "无法写入这个文件夹（可能需要管理员权限，比如 Program Files）。请换一个文件夹，例如 D:\ 盘或你的用户文件夹里。"
LangString OpenApp ${LANG_ENGLISH} "Open KVP"
LangString OpenApp ${LANG_SPANISH} "Abrir KVP"
LangString OpenApp ${LANG_SIMPCHINESE} "打开 KVP"
LangString StillOpen ${LANG_ENGLISH} "KVP is open. Close it (if you have trades or strategies running, leave them as you want first) and click Retry."
LangString StillOpen ${LANG_SPANISH} "KVP está abierta. Cerrala (si tenés operaciones o estrategias corriendo, primero dejalas como quieras) y tocá Reintentar."
LangString StillOpen ${LANG_SIMPCHINESE} "KVP 正在运行。请先关闭它（如果有交易或策略在运行，请先按你的需要处理好），然后点击“重试”。"
LangString StillOpenUn ${LANG_ENGLISH} "KVP is open. Close it and click Retry."
LangString StillOpenUn ${LANG_SPANISH} "KVP está abierta. Cerrala y tocá Reintentar."
LangString StillOpenUn ${LANG_SIMPCHINESE} "KVP 正在运行。请关闭它，然后点击“重试”。"

; Si está abierta, Windows no deja reemplazar sus archivos: pide cerrarla (sin cerrarla a la fuerza, por si está operando)
Function WaitClosed
  ${Do}
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${EXE}" /NH'
    Pop $0
    Pop $1
    ${StrStr} $2 $1 "${EXE}"
    ${If} $2 == ""
      ; la edición anterior (TradingHub.exe), si es una actualización desde ella
      nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${OLDEXE}" /NH'
      Pop $0
      Pop $1
      ${StrStr} $2 $1 "${OLDEXE}"
      ${If} $2 == ""
        ${Break}
      ${EndIf}
    ${EndIf}
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(StillOpen)" /SD IDCANCEL IDRETRY +2
    Abort
  ${Loop}
FunctionEnd

Function un.WaitClosed
  ${Do}
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${EXE}" /NH'
    Pop $0
    Pop $1
    ${UnStrStr} $2 $1 "${EXE}"
    ${If} $2 == ""
      ${Break}
    ${EndIf}
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(StillOpenUn)" /SD IDCANCEL IDRETRY +2
    Abort
  ${Loop}
FunctionEnd

; La carpeta elegida siempre termina en «Trading Hub» (si se elige D:\Apps queda D:\Apps\Trading Hub): actualizar y
; desinstalar reemplazan esa carpeta entera, así nunca tocan otros archivos. Y tiene que poder escribirse sin permisos de
; administrador (la instalación es por usuario).
Function CheckInstallDir
  ${GetFileName} "$INSTDIR" $1
  ${If} $1 != "KVP"
  ${AndIf} $1 != "Trading Hub"
    StrCpy $INSTDIR "$INSTDIR\KVP"
  ${EndIf}
  ClearErrors
  CreateDirectory "$INSTDIR"
  FileOpen $0 "$INSTDIR\.tradinghub-prueba" w
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONEXCLAMATION "$(NotWritable)"
    Abort
  ${EndIf}
  FileClose $0
  Delete "$INSTDIR\.tradinghub-prueba"
FunctionEnd

; Si antes estaba instalada en otra carpeta, esa copia vieja se borra (con las mismas reglas: solo si es suya)
Function RemoveOldInstall
  ReadRegStr $R0 HKCU "${UNINSTKEY}" "InstallLocation"
  ${If} $R0 == ""
  ${OrIf} $R0 == "$INSTDIR"
    Return
  ${EndIf}
  ${GetFileName} "$R0" $0
  ${If} ${FileExists} "$R0\${EXE}"
    !insertmacro RemoveProgram "$R0"
  ${EndIf}
FunctionEnd

; Quita el programa de su carpeta (solo sus archivos: ver RemoveProgram)
Function CleanInstallDir
  ${If} ${FileExists} "$INSTDIR\${EXE}"
    SetOutPath "$TEMP"
    !insertmacro RemoveProgram "$INSTDIR"
  ${EndIf}
FunctionEnd

Function un.CleanInstallDir
  SetOutPath "$TEMP"
  !insertmacro RemoveProgram "$INSTDIR"
FunctionEnd

Section "KVP" SecMain
  Call WaitClosed
  ; una actualización reemplaza los archivos del programa; lo demás que haya en la carpeta queda
  Call RemoveOldInstall
  Call CleanInstallDir
  SetOutPath "$INSTDIR"
  File /r "${SRC}\*.*"
  WriteUninstaller "$INSTDIR\Desinstalar KVP.exe"

  ; accesos de la edición anterior
  Delete "$SMPROGRAMS\Trading Hub.lnk"
  Delete "$DESKTOP\Trading Hub.lnk"
  CreateShortcut "$SMPROGRAMS\KVP.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  CreateShortcut "$DESKTOP\KVP.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0

  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${NAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "KVP"
  WriteRegStr HKCU "${UNINSTKEY}" "URLInfoAbout" "https://github.com/gouqizi2214730-source/trading-hub"
  WriteRegStr HKCU "${UNINSTKEY}" "URLUpdateInfo" "https://github.com/gouqizi2214730-source/trading-hub/releases"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Desinstalar KVP.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Desinstalar KVP.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Call un.WaitClosed
  Delete "$SMPROGRAMS\Trading Hub.lnk"
  Delete "$DESKTOP\Trading Hub.lnk"
  Delete "$SMPROGRAMS\KVP.lnk"
  Delete "$DESKTOP\KVP.lnk"
  Call un.CleanInstallDir
  DeleteRegKey HKCU "${UNINSTKEY}"
SectionEnd

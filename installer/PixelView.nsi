Unicode true
!include "MUI2.nsh"
!ifndef APP_VERSION
  !define APP_VERSION "1.2.2"
!endif
!ifndef FILE_VERSION
  !define FILE_VERSION "1.2.2.0"
!endif
!ifndef PACKAGE_VERSION
  !define PACKAGE_VERSION "1.2.2"
!endif
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\artifacts\publish"
!endif
Name "PixelView"
OutFile "..\artifacts\PixelView-${PACKAGE_VERSION}-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\PixelView"
RequestExecutionLevel user
SetCompressor /SOLID lzma
CRCCheck on
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${FILE_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "PixelView 安装程序"
VIAddVersionKey /LANG=2052 "FileDescription" "PixelView 图片查看器安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "PixelView"
!define MUI_ICON "..\src\PixelView\PixelView.ico"
!define MUI_UNICON "..\src\PixelView\PixelView.ico"
!define MUI_WELCOMEPAGE_TITLE "安装或升级 PixelView"
!define MUI_WELCOMEPAGE_TEXT "PixelView 将安装到当前账户的固定位置。$\r$\n$\r$\n以后运行新版安装程序即可升级，原有设置和已确认的默认打开方式会继续使用。$\r$\n$\r$\n如果 PixelView 正在运行，请先关闭窗口。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\PixelView.exe"
!define MUI_FINISHPAGE_RUN_TEXT "打开 PixelView"
!define MUI_FINISHPAGE_TEXT "安装已完成。$\r$\n$\r$\n首次设置默认看图器，请打开 PixelView 的“设置 → 默认打开方式”。$\r$\n$\r$\n以后升级会直接更新同一位置的程序。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  SetShellVarContext current
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\PixelView"
FunctionEnd

Section "PixelView"
  SetOutPath "$INSTDIR"
  SetOverwrite on
  ; Stage the complete executable before replacing the previous version.
  ClearErrors
  File /oname=PixelView.exe.new "${PUBLISH_DIR}\PixelView.exe"
  IfErrors install_failed
  cleanup_previous:
    ClearErrors
    Delete "$INSTDIR\PixelView.exe.previous"
    IfErrors previous_busy
    Goto switch_executable
  previous_busy:
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "请关闭所有 PixelView 窗口，再点“重试”完成升级。" IDRETRY cleanup_previous
    Goto install_failed
  switch_executable:
    IfFileExists "$INSTDIR\PixelView.exe" 0 activate_new
    ClearErrors
    Rename "$INSTDIR\PixelView.exe" "$INSTDIR\PixelView.exe.previous"
    IfErrors current_busy
    Goto activate_new
  current_busy:
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "请先关闭 PixelView，再点“重试”。" IDRETRY switch_executable
    Goto install_failed
  activate_new:
    ClearErrors
    Rename "$INSTDIR\PixelView.exe.new" "$INSTDIR\PixelView.exe"
    IfErrors restore_previous
    Delete "$INSTDIR\PixelView.exe.previous"
    Goto install_files
  restore_previous:
    Rename "$INSTDIR\PixelView.exe.previous" "$INSTDIR\PixelView.exe"
  install_failed:
    Delete "$INSTDIR\PixelView.exe.new"
    MessageBox MB_OK|MB_ICONSTOP "安装未完成。请关闭 PixelView，检查磁盘空间后重试。"
    Abort
  install_files:
    File /oname=使用说明.txt "..\docs\使用说明-安装版.txt"
    File /oname=LICENSE "..\LICENSE"
    SetOutPath "$INSTDIR\Licenses"
    File "..\licenses\*.txt"
    SetOutPath "$INSTDIR"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateDirectory "$SMPROGRAMS\PixelView"
    CreateShortcut "$SMPROGRAMS\PixelView\PixelView.lnk" "$INSTDIR\PixelView.exe"
    CreateShortcut "$SMPROGRAMS\PixelView\卸载 PixelView.lnk" "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView" "DisplayName" "PixelView"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView" "DisplayIcon" "$INSTDIR\PixelView.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView" "NoModify" 1
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView" "NoRepair" 1
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView" "EstimatedSize" 75500
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  ; Delete only package files, keeping the separate preferences directory.
  delete_program:
    ClearErrors
    Delete "$INSTDIR\PixelView.exe"
    IfErrors program_busy
    Goto remove_registration
  program_busy:
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "请关闭 PixelView 后重试卸载。" IDRETRY delete_program
    Abort
  remove_registration:
    StrCpy $0 0
    registry_loop:
      EnumRegValue $1 HKCU "Software\PixelView\Capabilities\FileAssociations" $0
      StrCmp $1 "" registry_done
      DeleteRegValue HKCU "Software\Classes\$1\OpenWithProgids" "PixelView.Image"
      IntOp $0 $0 + 1
      Goto registry_loop
    registry_done:
    DeleteRegValue HKCU "Software\RegisteredApplications" "PixelView"
    DeleteRegKey HKCU "Software\Classes\PixelView.Image"
    DeleteRegKey HKCU "Software\PixelView\Capabilities"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PixelView"
    Delete "$INSTDIR\PixelView.exe.previous"
    Delete "$INSTDIR\PixelView.exe.new"
    Delete "$INSTDIR\使用说明.txt"
    Delete "$INSTDIR\LICENSE"
    Delete "$INSTDIR\Licenses\Apache-2.0.txt"
    Delete "$INSTDIR\Licenses\dotnet-License.txt"
    Delete "$INSTDIR\Licenses\dotnet-Third-Party-Notices.txt"
    Delete "$INSTDIR\Licenses\Magick.NET-Notice.txt"
    Delete "$INSTDIR\Licenses\WindowsDesktop-License.txt"
    RMDir "$INSTDIR\Licenses"
    Delete "$SMPROGRAMS\PixelView\PixelView.lnk"
    Delete "$SMPROGRAMS\PixelView\卸载 PixelView.lnk"
    RMDir "$SMPROGRAMS\PixelView"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
SectionEnd

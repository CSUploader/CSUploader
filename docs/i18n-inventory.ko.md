# i18n Inventory — User-Facing UI Strings

This file lists every English string presented to the user in the CSUploader WPF app, in
`Key = "English text"` form, grouped by feature area. Each entry will be moved into a ResX
once a markup-extension migration is in place.

Conventions:

- `_Format` suffix marks strings that contain `{0}` / `{1}` placeholders. A trailing
  `# {0} = …` comment names each placeholder.
- Brand names (CSUploader, Rapidgator, JDownloader 2, Velopack, .NET, EF Core, SQLite,
  MIT, GitHub) are kept inline literally — they should NOT be translated when resourced.
- Log messages, exception messages, DB-stable identifier strings (SettingKey, ConfirmationKeys constants),
  format/culture patterns (`yyyy-MM-dd`, `F0`, `N0`), URLs, and resource keys are intentionally excluded.
- A `# review` comment marks entries whose user-facing-ness is borderline.

---

## Common

```
Common_OK                         = 확인
Common_Cancel                     = 취소
Common_Yes                        = 예
Common_No                         = 아니요
Common_Save                       = 저장
Common_Add                        = 추가
Common_Remove                     = 제거
Common_Close                      = 닫기
Common_Edit                       = 편집
Common_Delete                     = 삭제
Common_Browse                     = 찾아보기
Common_Back                       = 뒤로
Common_Next                       = 다음
Common_Apply                      = 적용
Common_Refresh                    = 새로 고침
Common_Details                    = 자세히
Common_Copy                       = 복사
Common_Import                     = 가져오기
Common_Export                     = 내보내기
Common_All                        = 모두
Common_None                       = 없음
Common_Test                       = 테스트
Common_Confirm                    = 확인
Common_Error                      = 오류
Common_Warning                    = 경고
Common_SelectFolder               = 폴더 선택
Common_SelectFiles                 = 파일 선택
Common_PleaseWait                 = 잠시만 기다려 주십시오…
Common_Loading                    = 불러오는 중…
Common_Cancelling                 = 취소하는 중…
Common_Preparing                  = 준비 중…
Common_Unknown                    = 알 수 없음
Common_Context_Copy               = Copy
Common_Context_OpenUrl            = Open URL
```

`Common_Save` is reused by the Settings → Connection page Save button, the Edit Account
dialog Save button, and the Confirmation/Edit dialogs. If translators need different
wordings per context, add `Settings_Save`, `Account_Save`, etc., from the per-section
keys below.

---

## MainWindow / Menus

```
Main_Title                        = CSUploader
Main_Title_UpdateAvailable_Format = CSUploader — 업데이트 사용 가능 (v{0}) — 도움말 → 업데이트 설치를 클릭하세요     # {0} = available semver
Main_Menu_File                    = 파일(_F)
Main_Menu_File_Exit               = 종료(_E)
Main_Menu_File_Exit_Gesture       = Alt+F4
Main_Menu_View                    = 보기(_V)
Main_Menu_View_UploadOverview     = 업로드 개요(_O)
Main_Menu_View_DarkMode           = 다크 모드(_D)
Main_Menu_View_LightMode          = 라이트 모드(_L)
Main_Menu_Help                    = 도움말(_H)
Main_Menu_Help_CheckForUpdates    = 업데이트 확인(_C)…
Main_Menu_Help_InstallUpdate      = 업데이트 설치(_I)
Main_Menu_Help_About              = CSUploader 정보(_A)

Main_Tab_Uploads                  = 업로드
Main_Tab_Uploaded                 = 기록
Main_Tab_Settings                 = 설정
Main_Tab_Logs                     = 로그

Main_CheckForUpdates_DialogTitle  = 업데이트 확인
Main_CheckForUpdates_AlreadyLatest = 최신 버전을 사용 중입니다.
Main_CheckForUpdates_Available_Format = 업데이트 사용 가능: v{0}.\n\n도움말 → 업데이트 설치를 통해 다운로드하고 설치하세요.   # {0} = available semver
Main_CheckForUpdates_Failed_Format = 업데이트를 확인할 수 없습니다: {0}
Update_CheckFailed_ToastTitle = 업데이트 확인 실패
Update_CheckFailed_ToastBody = CSUploader가 업데이트를 확인하지 못했습니다. 나중에 다시 시도합니다.
```

---

## Uploads tab

Toolbar, context menu, columns, overview panel, filter, and footer link.

```
Uploads_Toolbar_AddTip            = 새 업로드 추가
Uploads_Toolbar_StartTip          = 모든 업로드 시작
Uploads_Toolbar_PauseTip          = 일시 중지 / 재개
Uploads_Toolbar_StopTip           = 모든 업로드 중지
Uploads_Toolbar_RemoveTip         = 선택 항목 제거

Uploads_Context_Start             = 시작
Uploads_Context_ForceStart        = 강제 시작
Uploads_Context_StartNow          = 지금 시작
Uploads_Context_Stop              = 중지
Uploads_Context_SkipUpload        = 업로드 건너뛰기
Uploads_Context_Reset             = 재설정
Uploads_Context_OpenSourceDir     = 원본 디렉터리 열기
Uploads_Context_SetSpeedLimit     = 속도 제한 설정…
Uploads_Context_Move              = 이동
Uploads_Context_Remove            = 제거
Uploads_Move_Up10                 = 위로 10
Uploads_Move_Up1                  = 위로 1
Uploads_Move_Down1                = 아래로 1
Uploads_Move_Down10               = 아래로 10
Uploads_Tooltip_MoveUp            = 위로 이동(더 빨리 업로드)
Uploads_Tooltip_MoveDown          = 아래로 이동(더 늦게 업로드)

Uploads_Col_Name                  = 이름
Uploads_Col_Size                  = 크기
Uploads_Col_Hoster                = 호스터
Uploads_Col_Account               = 계정
Uploads_Col_Status                = 상태
Uploads_Col_Speed                 = 속도
Uploads_Col_ETA                   = 남은 시간
Uploads_Col_BytesLoaded           = 처리된 바이트
Uploads_Col_BytesRemaining        = 남은 바이트
Uploads_Col_Progress              = 진행률
Uploads_Col_Path                  = 경로
Uploads_Col_Added                 = 추가됨
Uploads_Col_Finished              = 완료됨
Uploads_Col_ScheduledAt           = 예약 시각
Uploads_Col_Duration              = 경과 시간
Uploads_Col_Order                 = 순서
Uploads_Col_SpeedLimit            = 속도 제한
Uploads_Col_URL                   = URL
Uploads_Col_Hash                  = 해시
Uploads_Col_Error                 = 오류

Uploads_ColumnHeader_LockTip      = 열 너비 잠금
Uploads_ColumnMenu_Reset          = 열 재설정
Uploads_ColumnMenu_DefaultLabel   = 열                                              # fallback when a column has no header text

Uploads_Overview_Title            = 업로드 개요
Uploads_Overview_CloseTip         = 개요 닫기
Uploads_Overview_ToggleTip        = 개요 통계 표시/숨김

Uploads_Overview_Packages         = 패키지
Uploads_Overview_Links            = 링크
Uploads_Overview_TotalBytes       = 총 바이트
Uploads_Overview_Uploadspeed      = 업로드 속도
Uploads_Overview_BytesLoaded      = 처리된 바이트
Uploads_Overview_RemainingBytes   = 남은 바이트
Uploads_Overview_Eta              = 남은 시간
Uploads_Overview_RunningUploads   = 실행 중인 업로드
Uploads_Overview_OpenConnections  = 열린 연결
Uploads_Overview_FinishedLinks    = 완료된 링크
Uploads_Overview_SkippedLinks     = 건너뛴 링크
Uploads_Overview_FailedLinks      = 실패한 링크

# Same labels appear inline with a trailing colon. Splitting them so a translator can
# control whether the colon is part of the term (typographic spacing differs in CJK).
Uploads_Overview_PackagesLabel        = 패키지:
Uploads_Overview_LinksLabel           = 링크:
Uploads_Overview_TotalBytesLabel      = 총 바이트:
Uploads_Overview_UploadspeedLabel     = 업로드 속도:
Uploads_Overview_BytesLoadedLabel     = 처리된 바이트:
Uploads_Overview_RemainingBytesLabel  = 남은 바이트:
Uploads_Overview_EtaLabel             = 남은 시간:
Uploads_Overview_RunningUploadsLabel  = 실행 중인 업로드:
Uploads_Overview_OpenConnectionsLabel = 열린 연결:
Uploads_Overview_FinishedLinksLabel   = 완료된 링크:
Uploads_Overview_SkippedLinksLabel    = 건너뛴 링크:
Uploads_Overview_FailedLinksLabel     = 실패한 링크:

Uploads_FilterLabel               = 파일 이름
Uploads_FilterTip                 = 파일 또는 패키지 이름으로 필터링
Uploads_FooterPremiumLink         = 프리미엄 계정 추가…

# Item-state names rendered in the Status column (FileStateDisplayConverter).
Uploads_State_Idle                = 대기
Uploads_State_HashQueued          = 해시 대기 중
Uploads_State_Hashing             = 해시 계산 중
Uploads_State_UploadQueued        = 업로드 대기 중
Uploads_State_Uploading           = 업로드 중
Uploads_State_Completed           = 완료됨
Uploads_State_CompletedWithErrors = 완료(오류 있음)
Uploads_State_Failed              = 실패
Uploads_State_Paused              = 일시 중지됨
Uploads_State_Cancelled           = 취소됨

# ETA fallback (UploadsViewModel)
Uploads_Eta_NotApplicable         = ~

# Reset-columns confirmation (different wording per tab)
Uploads_ResetColumns_Title        = 열 재설정
Uploads_ResetColumns_Message      = 업로드 탭의 열을 기본값으로 재설정하시겠습니까? 사용자 지정한 표시/숨김 및 순서가 모두 지워집니다.

# Remove confirmation prompts (UploadsViewModel)
Uploads_Remove_Title              = 제거
Uploads_Remove_Package_Format     = 패키지 '{0}' 및 파일 {1}개를 제거하시겠습니까?                            # {0} = package name, {1} = file count
Uploads_Remove_File_Format        = 업로드 목록에서 '{0}'을(를) 제거하시겠습니까?                                   # {0} = file name
Uploads_Remove_Generic            = 이 항목을 제거하시겠습니까?
Uploads_Remove_PackagesOnly_Format     = 패키지 {0}개(파일 {1}개)를 제거하시겠습니까?                            # {0} = package count, {1} = total file count
Uploads_Remove_FilesOnly_Format        = 업로드 목록에서 파일 {0}개를 제거하시겠습니까?                       # {0} = file count
Uploads_Remove_PackagesAndFiles_Format = 패키지 {0}개와 파일 {1}개(총 {2}개 항목)를 제거하시겠습니까?      # {0} = packages, {1} = loose files, {2} = total

# Reset confirmation prompts
Uploads_Reset_Title                  = 재설정
Uploads_Reset_Package_Format         = 패키지 '{0}'을(를) 재설정하시겠습니까? 이 패키지의 완료된 파일 {1}개를 다시 해시하고 업로드합니다.  # {0} = package name, {1} = completed file count
Uploads_Reset_File_Format            = '{0}'을(를) 재설정하시겠습니까? 이 파일은 이미 업로드 완료되었으며 — 재설정 시 다시 해시하고 업로드합니다.  # {0} = file name
Uploads_ForceStart_Reupload_Title    = 다시 업로드
Uploads_ForceStart_Reupload_Format   = 이미 완료된 파일 {0}개를 다시 업로드할까요? 정상적으로 업로드되었지만 강제 시작으로 다시 업로드합니다.  # {0} = completed file count
```

---

## Uploaded tab

```
Uploaded_Toolbar_ExportJson       = JSON으로 내보내기…
Uploaded_Toolbar_ExportJsonTip    = 완료된 모든 업로드를 모든 필드와 함께 JSON 파일로 내보냅니다

Uploaded_Col_Name                 = 이름
Uploaded_Col_Path                 = 경로
Uploaded_Col_Size                 = 크기
Uploaded_Col_Hoster               = 호스터
Uploaded_Col_Account              = 계정
Uploaded_Col_Finished             = 완료됨
Uploaded_Col_URL                  = URL
Uploaded_Col_Hash                 = 해시

Uploaded_Context_Copy             = 복사
Uploaded_Context_Copy_Gesture     = Ctrl+C
Uploaded_Context_CopyURL          = URL 복사
Uploaded_Context_Remove           = 제거
Uploaded_Context_Remove_Gesture   = Del
Uploaded_Context_ExportJson       = JSON으로 내보내기…

# Reset-columns confirmation
Uploaded_ResetColumns_Title       = 열 재설정
Uploaded_ResetColumns_Message     = 기록 탭의 열을 기본값으로 재설정하시겠습니까? 사용자 지정한 표시/숨김 및 순서가 모두 지워집니다.

# Remove confirmation
Uploaded_Remove_Title             = 제거
Uploaded_Remove_Single_Format     = 기록에서 '{0}'을(를) 제거하시겠습니까?                                          # {0} = file name
Uploaded_Remove_Many_Format       = 기록에서 {0}개의 항목을 제거하시겠습니까?                                    # {0} = entry count
```

---

## Settings — General

```
Settings_Sidebar_General          = 일반
Settings_Sidebar_Upload           = 업로드
Settings_Sidebar_Connection       = 연결
Settings_Sidebar_Accounts         = 계정

Settings_General_Language_Title        = 언어
Settings_General_Language_Desc         = UI 언어입니다. 변경 사항은 즉시 적용됩니다.
Settings_General_Language_Label        = 언어

Settings_General_Developer_Title       = 개발자
Settings_General_Developer_Desc        = 로컬 개발 및 테스트용 옵션입니다.
Settings_General_UseMockServer         = 모의 서버 사용 (모든 파일 호스터 요청을 localhost:8080/<hoster>로 리디렉션)   # review — hidden behind a developer flag, may stay English

Settings_General_GridAppearance_Title  = 표 모양
Settings_General_GridAppearance_Desc   = 업로드 및 기록 탭에 사용되는 글꼴입니다. 변경 사항은 즉시 적용됩니다.
Settings_General_GridFont              = 표 글꼴
Settings_General_GridFontSize          = 표 글꼴 크기

Settings_General_WindowBehaviour_Title = 창 동작
Settings_General_WindowBehaviour_Desc  = 메인 창을 최소화하거나 닫을 때의 동작을 선택합니다.
Settings_General_MinimizeToTray        = 메인 창을 작업 표시줄 대신 시스템 트레이로 최소화
Settings_General_CloseAction           = 닫기 버튼 동작

Settings_General_CloseAction_Ask         = 매번 묻기
Settings_General_CloseAction_MinToTray   = 트레이로 최소화
Settings_General_CloseAction_Exit        = 응용 프로그램 종료

Settings_General_Notifications_Title  = 알림
Settings_General_Notifications_Desc   = 업로드가 완료되면 오른쪽 하단에 팝업을 표시합니다.
Settings_General_ShowCompletionToasts = 업로드가 완료되면 팝업 알림 표시

Settings_General_ConfirmationPrompts_Title = 확인 메시지
Settings_General_ConfirmationPrompts_Desc  = 동작 전에 다시 묻도록 하려면 확인란을 선택하세요. 해당 동작에 대한 확인 메시지를 표시하지 않으려면 선택을 해제하세요.

Settings_General_Database_Title            = 데이터베이스
Settings_General_Database_Desc             = CSUploader는 업로드 기록(패키지, 파일 및 URL)을 로컬 SQLite 데이터베이스에 저장합니다. 업로드 또는 기록 탭에서 항목을 제거해도 숨겨질 뿐이며 — 데이터 행은 데이터베이스에 그대로 남아 있습니다. "지우기"를 클릭하면 두 탭 모두에서 숨겨진 행이 영구적으로 삭제됩니다.
Settings_General_Database_BtnClear         = 지우기
Settings_General_Database_ConfirmTitle     = 데이터베이스 지우기
Settings_General_Database_ConfirmMessage   = 두 탭 모두에서 숨겨진 업로드 기록을 영구적으로 삭제하시겠습니까?\n\n활성 및 표시 중인 업로드는 영향을 받지 않습니다. 이 작업은 되돌릴 수 없습니다.
Settings_General_Database_Status_Cleared_Format    = 데이터베이스에서 파일 {0}개와 패키지 {1}개를 지웠습니다.
Settings_General_Database_Status_NothingToClear    = 지울 숨겨진 행이 없습니다.
Settings_General_Database_BtnClearLogs              = Clear logs
Settings_General_Database_ConfirmClearLogsTitle     = Clear log history
Settings_General_Database_ConfirmClearLogsMessage   = Permanently delete all log entries from the database?\n\nThe Logs tab will also be emptied. This cannot be undone.
Settings_General_Database_LogsCleared_Format        = Cleared {0} log entr(ies) from the database.
Settings_General_Database_LogsNothingToClear        = No log entries to clear.
```

---

## Toast notifications

```
Toast_FileCompleted_Title    = 업로드 완료
Toast_FileCompleted_Body     = {0}
Toast_PackageCompleted_Title = 패키지 업로드 완료
Toast_PackageCompleted_Body  = {1}개 중 {0}개 파일 업로드됨 — {2}
```

---

## Settings — Upload

```
Settings_Upload_Mgmt_Title         = 업로드 관리
Settings_Upload_Mgmt_Desc          = 연결 제한, 우선순위 등 업로드 컨트롤러의 세부 항목을 설정합니다.
Settings_Upload_MaxConcurrent      = 최대 동시 업로드 수
Settings_Upload_MaxPerHoster       = 파일 호스터당 최대 동시 업로드 수
Settings_Upload_RemoveFinished     = 완료된 업로드 제거
Settings_Upload_IfFileExists       = 파일이 이미 존재하는 경우
Settings_Upload_MaxCpuJobs         = 최대 동시 CPU 작업 수
Settings_Upload_SpeedLimit         = 속도 제한 (KB/s)

Settings_Upload_RemoveFinished_Never              = 안 함
Settings_Upload_RemoveFinished_Immediately        = 즉시
Settings_Upload_RemoveFinished_AtStartup          = 시작 시
Settings_Upload_RemoveFinished_WhenPackageReady   = 패키지 준비 완료 시

Settings_Upload_IfExists_Ask                      = 각 파일마다 묻기
Settings_Upload_IfExists_Skip                     = 건너뛰기
Settings_Upload_IfExists_Overwrite                = 덮어쓰기
Settings_Upload_IfExists_Rename                   = 이름 변경

Settings_Upload_Autostart_Title                   = 업로드 자동 시작
Settings_Upload_Autostart_Desc                    = CSUploader가 사용자 조작 없이 대기 중인 업로드를 시작할지, 그리고 언제 시작할지를 선택합니다.
Settings_Upload_Autostart                         = 응용 프로그램 시작 시 업로드 자동 시작

Settings_Upload_Autostart_Always                  = 항상
Settings_Upload_Autostart_OnlyIfRunning           = 마지막 세션 종료 시 업로드가 실행 중이었던 경우에만
Settings_Upload_Autostart_Never                   = 안 함
```

---

## Settings — Connection (proxy manager)

```
Settings_Conn_Title                = 연결 관리자
Settings_Conn_Desc                 = 인터넷 접속에 프록시가 필요한 경우 여기서 구성하세요. 여러 프록시는 새 업로드에 대해 순환 사용됩니다. 활성화된 프록시가 없으면 기본 동작은 직접 연결입니다.
Settings_Conn_UseProxies           = 프록시 사용
Settings_Conn_UseProxiesTip        = 순환 사용의 마스터 스위치입니다. 꺼져 있으면 표에 프록시가 있어도 모든 트래픽(업로드 및 계정 확인)이 직접 연결됩니다 — 사용을 확정하지 않고 프록시를 추가하고 테스트할 때 유용합니다.
Settings_Conn_AutoDisable          = 실패한 프록시 자동으로 선택 해제
Settings_Conn_AutoDisableTip       = 켜져 있으면 수동 테스트 또는 업로드에 실패한 프록시는 선택 해제되어 순환에서 제외됩니다. 어떤 경우에도 상태 아이콘은 갱신됩니다.
Settings_Conn_AllowInvalidCert    = 잘못된 서버 인증서 허용(권장하지 않음)
Settings_Conn_AllowInvalidCertTip = 모든 아웃바운드 요청에서 TLS 인증서 유효성 검사를 건너뜁니다. 일부 호스터의 스토리지 CDN 노드(예: FileBoom의 cmb-*.filestore.app 에지)가 표준 검증에 실패하는 인증서를 사용하는 경우 필요합니다. MITM 공격에 대한 보호가 비활성화됩니다 — 업로드가 SSL 오류로 실패하는 경우에만 활성화하십시오.

Settings_Conn_Col_On               = 사용
Settings_Conn_Col_Priority         = 우선순위
Settings_Conn_Col_Type             = 유형
Settings_Conn_Col_Host             = 호스트 / IP
Settings_Conn_Col_Port             = 포트
Settings_Conn_Col_User             = 사용자
Settings_Conn_Col_Password         = 비밀번호
Settings_Conn_Col_Test             = 테스트
Settings_Conn_Col_Status           = 상태

Settings_Conn_PriorityUpTip        = 위로 이동 (우선순위 높임)
Settings_Conn_PriorityDownTip      = 아래로 이동 (우선순위 낮춤)

Settings_Conn_Context_Test         = 테스트
Settings_Conn_Context_Remove       = 제거
Settings_Conn_Context_Remove_Gesture = Del

Settings_Conn_Btn_Import           = 가져오기
Settings_Conn_Btn_Import_FromText  = 텍스트에서 가져오기…
Settings_Conn_Btn_Import_FromFile  = 파일에서 가져오기…
Settings_Conn_Btn_Export           = 내보내기
Settings_Conn_Btn_Export_AllToText = 모든 프록시를 텍스트로 내보내기…
Settings_Conn_Btn_Export_AllToFile = 모든 프록시를 파일로 내보내기…
Settings_Conn_Btn_Export_OkToText  = 테스트 성공한 프록시를 텍스트로 내보내기…
Settings_Conn_Btn_Export_OkToFile  = 테스트 성공한 프록시를 파일로 내보내기…
Settings_Conn_Btn_Export_SelectedToText = 선택한 프록시를 텍스트로 내보내기…
Settings_Conn_Btn_Export_SelectedToFile = 선택한 프록시를 파일로 내보내기…
Settings_Conn_Btn_Save             = 저장
Settings_Conn_Btn_Add              = 추가
Settings_Conn_Btn_Remove           = 제거
Settings_Conn_Btn_RemoveSelected   = 선택 항목 제거
Settings_Conn_Btn_RemoveFailed     = 실패한 항목 제거
Settings_Conn_Btn_TestAll          = 모두 테스트
Settings_Conn_Btn_TestAllTip       = 목록의 모든 프록시에 대해 연결을 테스트합니다
Settings_Conn_Btn_Details          = 자세히

# Proxy import/export dialogs (ProxyTextDialog ctor args, ConnectionManagerViewModel)
Settings_Conn_ImportProxies_FileDialogTitle = 프록시 가져오기
Settings_Conn_ImportProxies_FileFilter      = 프록시 목록 (*.txt)|*.txt|모든 파일 (*.*)|*.*
Settings_Conn_ImportProxies_DialogTitle     = 프록시 가져오기
Settings_Conn_ImportProxies_DialogDesc      = 프록시 줄을 붙여 넣으세요(한 줄에 하나씩). 형식: scheme://[user:pass@]host[:port] — 포트는 스킴에 따라 80/443/1080이 기본값입니다.
Settings_Conn_ExportAll_DialogTitle         = 모든 프록시 내보내기
Settings_Conn_ExportOk_DialogTitle          = 테스트 성공한 프록시 내보내기
Settings_Conn_ExportAll_Desc_Format         = 프록시 {0}개:                                  # {0} = count of all proxies
Settings_Conn_ExportOk_Desc_Format          = 마지막 테스트가 성공한 프록시 {0}개:      # {0} = count of OK proxies
Settings_Conn_ExportSelected_DialogTitle    = 선택한 프록시 내보내기
Settings_Conn_ExportSelected_Desc_Format    = 선택한 프록시 {0}개:                         # {0} = count of selected proxies

# Proxy remove confirmations (ConnectionManagerViewModel)
Settings_Conn_RemoveProxy_Title             = 프록시 제거
Settings_Conn_RemoveProxy_One_Format        = 프록시 '{0}:{1}'을(를) 제거하시겠습니까?                        # {0} = host, {1} = port
Settings_Conn_RemoveProxy_Many_Format       = 프록시 {0}개를 제거하시겠습니까?                            # {0} = count
Settings_Conn_RemoveFailedProxy_Title       = 실패한 프록시 제거
Settings_Conn_RemoveFailedProxy_One_Format  = 실패한 프록시 '{0}:{1}'을(를) 제거하시겠습니까?             # {0} = host, {1} = port
Settings_Conn_RemoveFailedProxy_Many_Format = 실패한 프록시 {0}개를 제거하시겠습니까?                     # {0} = count

# Proxy test/save status strings (ConnectionManagerViewModel / ProxySettingItem)
Settings_Conn_Status_Queued                 = 대기 중…
Settings_Conn_Status_Testing                = 테스트 중…
Settings_Conn_Status_OkLive                 = 정상 (실시간)
Settings_Conn_Status_OkLatencyIp_Format     = 정상 {0}ms ({1})                                 # {0} = ms, {1} = detected IP
Settings_Conn_Status_OkLatencyUnknown_Format = 정상 {0}ms (예상치 못한 응답)                # {0} = ms
Settings_Conn_Status_Failed_Format          = 실패: {0}                                    # {0} = error first line / message
Settings_Conn_Status_Saved                  = 저장됨
Settings_Conn_Status_SaveFailed_Format      = 저장 실패: {0}                               # {0} = error message
Settings_Conn_Status_Imported_Format        = 프록시 {0}개를 가져왔습니다                    # {0} = proxy count
Settings_Conn_Status_ExportedToFile_Format  = 프록시 {0}개를 {1}(으)로 내보냈습니다                   # {0} = count, {1} = file name
```

---

## Settings — Accounts

```
Settings_Accounts_Title            = 계정 관리자
Settings_Accounts_Desc             = 모든 프리미엄/골드/플래티넘 계정을 입력하고 관리합니다.

Settings_Accounts_Col_Enabled      = ✓                                                       # check-mark glyph header — leave as glyph or localize as e.g. "On"
Settings_Accounts_Col_Hoster       = 호스터
Settings_Accounts_Col_Status       = 상태
Settings_Accounts_Col_Username     = 사용자 이름
Settings_Accounts_Col_Password     = 비밀번호
Settings_Accounts_Col_Type         = 유형
Settings_Accounts_Col_Used        = 사용 중
Settings_Accounts_Col_Available   = 남은 용량
Settings_Accounts_Col_AddedAt     = 추가한 시각
Settings_Accounts_Col_RefreshedAt = 새로 고침 시각
Settings_Accounts_Storage_Unlimited= 무제한

Settings_Accounts_Context_Edit     = 계정 편집…
Settings_Accounts_Context_Refresh  = 확인 / 새로 고침
Settings_Accounts_Context_Enable   = 사용
Settings_Accounts_Context_Disable  = 사용 안 함
Settings_Accounts_Context_Delete   = 삭제

Settings_Accounts_Btn_Add          = 추가
Settings_Accounts_Btn_Remove       = 제거
Settings_Accounts_Btn_Refresh      = 새로 고침

# Account remove / validation
Settings_Accounts_Remove_Title             = 계정 제거
Settings_Accounts_Remove_Message_Format    = {1}의 계정 '{0}'을(를) 제거하시겠습니까?                  # {0} = username, {1} = file hoster name
Settings_Accounts_Remove_MessageBulk_Format= 선택한 {0}개의 계정을 제거하시겠습니까?

Settings_Accounts_Validation_FillHosterUser = 파일 호스터, 사용자 이름, 비밀번호를 입력해 주십시오.
Settings_Accounts_Check_DialogTitle         = 계정 확인
Settings_Accounts_Check_FailedAddAnyway_Format = 계정 확인에 실패했습니다: {0}\n\n그래도 추가하시겠습니까?    # {0} = error message
Settings_Accounts_Check_CouldNotVerifyAddAnyway_Format = 계정을 확인할 수 없습니다: {0}\n\n그래도 추가하시겠습니까?   # {0} = error message

# CheckAccountStatus inline status messages
Settings_Accounts_Status_Verifying          = 자격 증명을 확인하는 중…
Settings_Accounts_Status_Checking           = 계정을 확인하는 중…
Settings_Accounts_Status_NoAccountsToRefresh = 새로 고칠 계정이 없습니다.
Settings_Accounts_Status_Verified_Format    = 확인됨: {0}                                  # {0} = result.Message
Settings_Accounts_Status_Warning_Format     = 경고: {0}                                   # {0} = result.Message
Settings_Accounts_Status_Valid_Format       = 유효함: {0}                                     # {0} = result.Message
Settings_Accounts_Status_ValidExclaim_Format = 유효함! {0}                                    # {0} = result.Message  (separate from above — different exclamation)
Settings_Accounts_Status_Failed_Format      = 실패: {0}                                    # {0} = result.Message
Settings_Accounts_Status_CheckError_Format  = 확인 오류: {0}                               # {0} = exception.Message
Settings_Accounts_Status_Error_Format       = 오류: {0}                                     # {0} = exception.Message
Settings_Accounts_Status_AccountAdded_Format = {0} 계정이 추가되었습니다!                        # {0} = file hoster name
Settings_Accounts_Status_NoImpl_Format      = {0}에 대한 구현이 없습니다. 확인할 수 없습니다.       # {0} = file hoster name
Settings_Accounts_Status_NoImplWillSave_Format = {0}에 대한 구현이 없습니다. 확인 없이 계정이 저장됩니다.   # {0} = file hoster name
Settings_Accounts_Status_CheckingProgress_Format = {0}@{1} 확인 중… ({2}/{3})             # {0} = username, {1} = hoster, {2} = current, {3} = total
Settings_Accounts_Status_CheckingShort      = 확인 중…
Settings_Accounts_Status_NoImpl             = 구현 없음
Settings_Accounts_Status_RefreshSummary_Format = 계정 {0}개를 새로 고쳤습니다. {1}개가 갱신되었습니다.        # {0} = checked count, {1} = updated count
Settings_Accounts_Status_AccountDisabled_Format = 계정 '{0}'이(가) 비활성화되었습니다.                    # {0} = username
Settings_Accounts_Status_AccountEnabled_Format  = 계정 '{0}'이(가) 활성화되었습니다.                     # {0} = username
Settings_Accounts_Status_AccountsBulkDisabled_Format= {0}개의 계정이 비활성화되었습니다.
Settings_Accounts_Status_AccountsBulkEnabled_Format= {0}개의 계정이 활성화되었습니다.

# AccountCheckResult fallback strings (SettingsViewModel)
Settings_Accounts_DefaultStatus_OK        = 정상
Settings_Accounts_DefaultStatus_Failed    = 실패

# Password column placeholder
Settings_Accounts_PasswordMask            = ******
```

---

## Logs tab

```
Logs_AutoScroll                    = 자동 스크롤
Logs_BtnClear                      = Clear
Logs_Tab_Status                    = 상태
Logs_Tab_Http                      = HTTP
Logs_Tab_Errors                    = 오류
Logs_Tab_UI                        = UI

Logs_Col_DateTime                  = 날짜/시간
Logs_Col_Status                    = 상태
Logs_Col_Filename                  = 파일 이름
Logs_Col_Function                  = 함수
Logs_Col_Line                      = 줄
Logs_Col_Message                   = 메시지
Logs_Col_Thread                    = 스레드

# Status messages logged to the Status tab (UploadedViewModel) — these surface to the
# user via the Logs tab so they're worth localising.
Logs_Status_NoUrlsClipboardCleared = 선택 항목에 URL이 없습니다. 클립보드를 비웠습니다
Logs_Status_CopiedUrls_Format      = URL {0}개를 클립보드에 복사했습니다                          # {0} = url count
Logs_Status_HiddenFiles_Format     = 기록 탭에서 파일 {0}개를 숨겼습니다                   # {0} = file count
Logs_Status_ExportedPackages_Format = 패키지 {0}개를 {1}(으)로 내보냈습니다                         # {0} = pkg count, {1} = file path
```

---

## Upload Wizard

```
Wizard_Title                       = 업로드 마법사

Wizard_Step_DirectorySource        = 1. 디렉터리
Wizard_Step_FileHosters            = 2. 파일 호스터
Wizard_Step_Summary               = 3. 요약
Wizard_Step_Start                  = 4. 시작
Wizard_Summary_Title              = 업로드 요약
Wizard_Summary_Desc               = 각 호스터에 업로드될 내용을 확인하세요. 적합한 파일이 없는 호스터는 제외됩니다.
Wizard_Summary_FileCount_Suffix   = 개 파일
Wizard_Summary_OrphanWarning_Suffix= 개 파일은 어떤 호스터에도 업로드되지 않습니다:
Wizard_Summary_MaxFileSize_Format = 파일당 최대 {0}
Wizard_Step_FilesSource            = 1. 파일

Wizard_Step0_Mode_Directory        = 디렉터리 업로드
Wizard_Step0_Mode_Files            = 파일 업로드

Wizard_Step0_Title                 = 업로드 디렉터리 선택
Wizard_Step0_Desc                  = 업로드할 파일이 들어 있는 디렉터리를 선택하세요.
Wizard_Step0_Browse                = 찾아보기
Wizard_Step0_BrowseDialogTitle     = 업로드 디렉터리 선택                                 # used when calling BrowseFolder

Wizard_Step1_Title                 = 파일 선택
Wizard_Step1_PackageTitleLabel     = 패키지 제목:
Wizard_Step1_FilterLabel           = 필터:
Wizard_Step1_BtnSelectAll          = 모두 선택
Wizard_Step1_BtnDeselectAll        = 모두 선택 해제
Wizard_Step1_BtnRemove            = 제거
Wizard_Step1_Col_File              = 파일
Wizard_Step1_Col_Size              = 크기
Wizard_Step1_SelectedLabel         = 선택됨:
Wizard_Step1_FilesUnit             = 개 파일
Wizard_Step1_TotalSizeLabel        = 총 크기:

Wizard_Step2_Title                 = 파일 호스터 선택
Wizard_Step2_Desc                  = 업로드할 파일 호스터와 계정을 선택하세요.
Wizard_Step2_Col_Use               = 사용
Wizard_Step2_Col_FileHoster        = 파일 호스터
Wizard_Step2_Col_Account           = 계정
Wizard_Step2_Col_MaxFileSize       = 최대 파일 크기
Wizard_Step2_Col_MaxConcurrent     = 최대 동시 전송
Wizard_Step2_NoLimit               = 제한 없음
Wizard_Step2_AccountAnonymous      = (익명)
Wizard_Step2_AccountSelect         = (계정 선택)
Wizard_Step2_AddAccountLink        = 계정 추가…
Wizard_Step2_AccountRequiredTooltip = 이 호스터는 계정이 필요합니다. "계정 추가…"를 클릭하여 추가하세요.
Wizard_Hoster_LimitsHeader         = 이 호스터의 제한이 초과됩니다:
Wizard_Hoster_FileTooLarge_Format  = {0}: 다음 파일은 파일당 {1} 제한을 초과하여 업로드되지 않습니다:\n{2}
Wizard_Hoster_FileNameRejected_Format = {0}: 다음 파일 이름에는 이 호스터가 허용하지 않는 문자가 포함되어 있어 업로드되지 않습니다:\n{1}
Wizard_Hoster_FileTypeRejected_Format = {0}: 다음 파일은 이 호스터가 허용하지 않는 확장자여서 업로드되지 않습니다:\n{1}
Wizard_Hoster_AccountDisabled_Format = {0}: 계정 "{1}"이(가) 꺼져 있어 이 호스터에는 아무것도 업로드되지 않습니다.
Wizard_Hoster_AccountCheckFailed_Format = {0}: 계정 "{1}"의 마지막 확인에 실패하여 이 호스터에는 아무것도 업로드되지 않습니다. 설정 → 계정에서 다시 확인하세요.
{1}
Wizard_Hoster_TooManyFiles_Format  = {0}: {1}개 파일이 선택되었지만 패키지당 제한은 {2}개입니다.

Wizard_Step3_Title                 = 시작 시점
Wizard_Step3_Desc                  = 업로드를 시작할 시점을 선택하세요.
Wizard_Step3_Mode_Immediately      = 마법사를 닫은 후 즉시 시작
Wizard_Step3_Mode_Later            = 대기열에 추가하고 나중에 시작 (수동 시작)
Wizard_Step3_Mode_Scheduled        = 특정 날짜와 시간으로 예약
Wizard_Step3_TimeFormatHint        = (HH:mm)

Wizard_Btn_Back                    = 뒤로
Wizard_Btn_Cancel                  = 취소
Wizard_Btn_Next                    = 다음
Wizard_Btn_Add                     = 추가

# Validation errors (UploadWizardViewModel.ShowError)
Wizard_Validation_PickValidDir     = 유효한 디렉터리를 선택해 주십시오.
Wizard_Validation_PickFile         = 파일을 하나 이상 선택해 주십시오.
Wizard_Validation_PickHoster       = 파일 호스터를 하나 이상 선택해 주십시오.
Wizard_Error_Format                = 오류: {0}                                              # {0} = exception.Message

Wizard_Step0_Files_Title           = 파일 선택
Wizard_Step0_Files_Desc            = 업로드할 파일을 선택하세요. 나중에 더 추가할 수 있습니다.
Wizard_Step0_Files_Pick            = 파일 추가…
Wizard_Step0_Files_BrowseDialogTitle = 업로드할 파일 선택                                  # used when calling BrowseFiles

Wizard_Step1_DuplicateFilenameSuffixFormat = {0} ({1} 내)                                    # {0} = filename, {1} = parent folder name

Wizard_Validation_PickAtLeastOneFile = 계속하기 전에 파일을 하나 이상 선택해 주십시오.
Wizard_Validation_TitleRequired    = 패키지 제목을 입력해 주십시오.
```

---

## Confirmation Prompts (Settings → General list labels)

These are the user-visible labels for `ConfirmationKeys.All` — the strings shown in the
"Confirmation Prompts" section of Settings. Stable IDs (`remove-upload-package-or-file`
etc.) stay English.

```
Confirm_RemoveUploadPackageOrFile  = 업로드 탭에서 패키지 또는 파일 제거
Confirm_RemoveUploadedEntry        = 기록 탭에서 항목 제거
Confirm_RemoveFileHosterAccount    = 파일 호스터 계정 제거
Confirm_RemoveProxy                = 연결 관리자에서 프록시 제거
Confirm_ResetCompletedUpload       = 완료된 업로드 재설정(다시 해시 및 업로드)
Confirm_ResetColumns               = 업로드 / 기록 탭의 열을 기본값으로 재설정
```

---

## Dialog windows

### About

```
About_WindowTitle                  = CSUploader 정보
About_AppName                      = CSUploader
About_Version_Format               = 버전 {0}                                             # {0} = assembly version, e.g. "1.2.3"
About_Description                  = 여러 호스팅 서비스를 위한 강력한 파일 업로드 관리자입니다. 해싱, 대기열 관리, 실시간 진행률 추적 등의 기능을 제공합니다.
About_Field_Framework              = 프레임워크:
About_Field_Framework_Value        = .NET 10.0 (Avalonia)
About_Field_Database               = 데이터베이스:
About_Field_Database_Value         = SQLite via EF Core 10
About_Field_License                = 라이선스:
About_Field_License_Value          = MIT
About_Field_Source                 = 소스:
About_OK                           = 확인
```

### CloseAction dialog

```
CloseAction_WindowTitle            = CSUploader 닫기
CloseAction_Heading                = 닫기 버튼이 어떤 동작을 하길 원하십니까?
CloseAction_Subheading             = 하나를 선택하세요 — 나중에 설정 → 일반에서 변경할 수 있습니다.
CloseAction_Remember               = 내 선택 기억하기
CloseAction_BtnMinimize            = 트레이로 최소화
CloseAction_BtnExit                = 종료
CloseAction_BtnCancel              = 취소
```

### Confirmation dialog

```
Confirmation_WindowTitle           = 확인
Confirmation_DontAskAgain          = 이 동작에 대해 다시 묻지 않기
Confirmation_BtnYes                = 예
Confirmation_BtnNo                 = 아니요
```

### EditAccount dialog

```
EditAccount_WindowTitle            = 계정 편집
EditAccount_AddTitle               = 계정 추가                                             # used by SettingsViewModel.AddAccountDialog
EditAccount_FileHosterLabel        = 파일 호스터:
EditAccount_UsernameLabel          = 사용자 이름:
EditAccount_PasswordLabel          = 비밀번호:
EditAccount_AccountEnabled         = 계정 사용
EditAccount_BtnSave                = 저장
EditAccount_BtnCancel              = 취소
EditAccount_Validation_RequireUsernameAndPassword = 사용자 이름과 비밀번호를 모두 입력해 주십시오.

EditProxy_AddTitle                 = 프록시 추가
EditProxy_EditTitle                = 프록시 편집
EditProxy_EnabledLabel             = 프록시 사용
EditProxy_BtnSave                  = 저장
EditProxy_BtnCancel                = 취소
EditProxy_BtnTest                  = 테스트
EditProxy_Validation_HostRequired  = 호스트 또는 IP 주소를 입력하세요.
EditProxy_Validation_PortInvalid   = 1에서 65535 사이의 유효한 포트를 입력하세요.
EditProxy_Status_Testing           = 테스트 중…
EditProxy_Status_OkLatency_Format  = 정상 {0}ms (예기치 않은 응답)
EditProxy_Status_OkLatencyIp_Format = 정상 {0}ms ({1})
EditProxy_Status_Failed_Format     = 실패: {0}
```

### HttpDetails window

```
HttpDetails_WindowTitle            = HTTP 트랜잭션 세부 정보
HttpDetails_Tab_Request            = 요청
HttpDetails_Tab_Response           = 응답
HttpDetails_Tab_FullDump           = 전체 덤프
HttpDetails_SubTab_Headers         = 헤더
HttpDetails_SubTab_BodyRaw         = 본문 (원본)
HttpDetails_SubTab_BodyJson        = 본문 (JSON)
HttpDetails_SubTab_Hex             = 16진수

# Header strip
HttpDetails_Timing_Format          = 시작: {0}  |  소요: {1}ms  |  크기: {2}바이트    # {0} = HH:mm:ss.fff, {1} = ms, {2} = byte count
HttpDetails_Proxy_Format           = 프록시: {0}                                              # {0} = proxy display string
HttpDetails_NoData                 = (데이터 없음)
HttpDetails_NoBody                 = (본문 없음)
# Section dividers used in the Full Dump (these are framed in box-drawing chars; the
# label words are the only translatable parts).
HttpDetails_FullDump_Request       = 요청
HttpDetails_FullDump_Response      = 응답
```

### LogDetails window

```
LogDetails_WindowTitle             = 로그 세부 정보
LogDetails_Field_DateTime          = 날짜/시간:
LogDetails_Field_ThreadId          = 스레드 ID:
LogDetails_Field_Filename          = 파일 이름:
LogDetails_Field_Function          = 함수:
LogDetails_Field_Line              = 줄:
LogDetails_Tab_Text                = 텍스트
LogDetails_Tab_Html                = HTML
LogDetails_Btn_Close               = 닫기
```

### Progress / UpdateProgress windows

```
Progress_WindowTitle               = 잠시만 기다려 주십시오…
Progress_DefaultLabel              = 불러오는 중…
Progress_LabelSuffix               = 잠시만 기다려 주십시오…                                          # appended to the caller-supplied label on a new line
Progress_BtnCancel                 = 취소
Progress_BtnCancelling             = 취소하는 중…

UpdateProgress_WindowTitle         = CSUploader 업데이트 중
UpdateProgress_StatusInitial       = 준비 중…
UpdateProgress_StatusDownloading_Format = 업데이트 v{0} 다운로드 중…                           # {0} = available semver
UpdateProgress_StatusRestarting    = 다시 시작하는 중…
UpdateProgress_StatusFailed_Format = 업데이트 실패: {0}                                      # {0} = exception.Message
UpdateProgress_PercentInitial      = 0%
```

### ProxyText dialog

```
ProxyText_WindowTitle              = 프록시                                                 # XAML default — overridden via ctor with Import/Export titles
ProxyText_BtnImport                = 가져오기
ProxyText_BtnCopy                  = 복사
ProxyText_BtnCancel                = 취소
ProxyText_BtnClose                 = 닫기                                                   # replaces Cancel in read-only export mode
```

### SpeedLimit dialog

```
SpeedLimit_WindowTitle             = 속도 제한
SpeedLimit_Heading                 = 속도 제한 설정
SpeedLimit_Subheading              = 패키지별 재정의입니다. 전역 설정을 사용하려면 비워 두세요.
SpeedLimit_Unit                    = KB/s
SpeedLimit_BtnClear                = 지우기
SpeedLimit_BtnCancel               = 취소
SpeedLimit_BtnOk                   = 확인
SpeedLimit_Validation_Title        = 잘못된 값
SpeedLimit_Validation_Message      = 양의 정수(KB/s)를 입력하거나, 지우려면 비워 두세요.
```

---

## Status / inline messages

These are short status strings shown in non-dialog inline UI. Several have already been
listed in their owning section above; this section catches the rest, plus dialog-service
default titles.

```
Dialog_DefaultErrorTitle           = 오류
Dialog_DefaultConfirmTitle         = 확인
Dialog_DefaultBrowseFolderTitle    = 폴더 선택
Dialog_GenericErrorTitle           = 오류                                                   # ProgressWindow exception fallback

# File-picker filters (Microsoft.Win32 OpenFileDialog / SaveFileDialog)
Picker_Filter_Json                 = JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*
Picker_Filter_ProxyLists           = 프록시 목록 (*.txt)|*.txt|모든 파일 (*.*)|*.*
```

Speed-limit display strings produced by `SpeedLimitConverter` in the Uploads grid Speed
Limit column:

```
SpeedLimit_Display_Mbps_Format     = {0} MB/s                                                # {0} = numeric value (formatted "0.##")
SpeedLimit_Display_Kbps_Format     = {0} KB/s                                                # {0} = integer KB/s
```

Upload-overview UploadSpeed default when zero (`UploadsViewModel.UploadSpeed`):

```
Uploads_Overview_UploadSpeed_Zero  = 0 B/s
```

---

## Tray icon menu

Strings from `TrayIconManager`. The tooltip text "CSUploader" is a brand name and stays
literal even when localising.

```
Tray_Tooltip                       = CSUploader                                              # brand — do not translate
Tray_Menu_Show                     = CSUploader 표시                                         # "Show " + brand; localise the verb only
Tray_Menu_Exit                     = 종료
Tray_Balloon_Title                 = CSUploader                                              # brand — do not translate
Tray_Balloon_Body                  = 트레이에서 계속 실행 중입니다. 아이콘을 클릭하면 창이 복원되고, 마우스 오른쪽 버튼을 클릭하면 종료할 수 있습니다.
```

---

## Notes / ambiguous cases

Items flagged for human review:

- **`Settings_General_UseMockServer`** — checkbox tied to a developer/QA flag for
  redirecting hoster requests to `localhost:8080`. Strictly speaking, the "Developer"
  group is exposed in the General tab, so end users *could* see it. Marked `# review`;
  the team may decide to either localise it normally, hide it behind a build flag, or
  leave it English-only.

- **`Settings_Accounts_Col_Enabled`** — column header is the literal Unicode check-mark
  glyph `✓`. Some locales prefer a short word like "On" / "啟用" instead of a glyph; left
  as-is in the inventory but worth a UX call before translating.

- **`Settings_Accounts_DefaultStatus_OK`** / **`_Failed`** — these strings come from
  `AccountCheckResult.Message ?? "OK"` / `?? "Failed"` fallbacks. The actual result
  message normally comes from the hoster client (e.g. RapidgatorClient) and is a separate
  set of strings (`"Failed to login"`, `"Failed to create folder"`, …). Those are
  currently passed straight to the user via `UploadFinishedCallback` and the Status
  column. They sit on the boundary between log/diagnostic and user-facing — flagged for
  the team to decide whether to resource them or treat them as developer-only.

- **`Wizard_Error_Format` ("Error: {0}")** vs. the dialog generic
  **`Dialog_DefaultErrorTitle` ("Error")** — kept as separate keys so a translator can
  give the inline "Error: …" prefix a natural rendering even if the dialog title stays
  one word.

- Several short labels with a colon suffix (`Package(s):`, `ETA:`) are duplicated as
  with-colon and without-colon forms because some translations want the colon attached
  to the term itself (CJK typography rules differ). De-duplicate later if it turns out
  every locale wants the same convention.

- `Common_Save` is reused by Settings → Connection Save, the Edit-Account Save, and the
  Confirmation/Edit dialog Save buttons. All three currently render the same English
  text and there is no functional reason to split, but if a translator pushes back the
  per-section keys (`Settings_Conn_Btn_Save`, `EditAccount_BtnSave`) are already listed.

- `Tray_Tooltip` / `Tray_Balloon_Title` are brand-only and should not be translated.

- `About_AppName`, `About_Field_Framework_Value`, `About_Field_Database_Value`, and
  `About_Field_License_Value` are mostly proper nouns / brand strings; included for
  completeness so the markup-extension pass doesn't miss them, but the translator should
  leave them as-is.

- The Uploaded-tab `Header="✓"` glyph and the JD2-style up/down chevrons (`▲`, `▼`,
  `+`, `−`, `✕`, `▶`, `▼`) on toolbar buttons are pure glyphs — not localised.

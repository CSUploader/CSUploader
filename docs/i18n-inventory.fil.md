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
Common_OK                         = OK
Common_Cancel                     = Kanselahin
Common_Yes                        = Oo
Common_No                         = Hindi
Common_Save                       = I-save
Common_Add                        = Idagdag
Common_Remove                     = Tanggalin
Common_Close                      = Isara
Common_Edit                       = I-edit
Common_Delete                     = Burahin
Common_Browse                     = Mag-browse
Common_Back                       = Bumalik
Common_Next                       = Susunod
Common_Apply                      = I-apply
Common_Refresh                    = I-refresh
Common_Details                    = Details
Common_Copy                       = Kopyahin
Common_Import                     = Mag-import
Common_Export                     = Mag-export
Common_All                        = Lahat
Common_None                       = Wala
Common_Test                       = I-test
Common_Confirm                    = Kumpirmahin
Common_Error                      = Error
Common_Warning                    = Babala
Common_SelectFolder               = Pumili ng Folder
Common_SelectFiles                 = Pumili ng mga File
Common_PleaseWait                 = Mangyaring maghintay...
Common_Loading                    = Naglo-load...
Common_Cancelling                 = Kinakansela...
Common_Preparing                  = Naghahanda…
Common_Unknown                    = hindi alam
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
Main_Title_UpdateAvailable_Format = CSUploader — May available na update (v{0}) — i-click ang Help → Install Update     # {0} = available semver
Main_Menu_File                    = File(_F)
Main_Menu_File_Exit               = Lumabas(_E)
Main_Menu_File_Exit_Gesture       = Alt+F4
Main_Menu_View                    = View(_V)
Main_Menu_View_UploadOverview     = Upload Overview(_O)
Main_Menu_View_DarkMode           = Dark Mode(_D)
Main_Menu_View_LightMode          = Light Mode(_L)
Main_Menu_Help                    = Help(_H)
Main_Menu_Help_CheckForUpdates    = Mag-check ng Updates(_C)…
Main_Menu_Help_InstallUpdate      = I-install ang Update(_I)
Main_Menu_Help_About              = Tungkol sa CSUploader(_A)

Main_Tab_Uploads                  = Uploads
Main_Tab_Uploaded                 = History
Main_Tab_Settings                 = Settings
Main_Tab_Logs                     = Logs

Main_CheckForUpdates_DialogTitle  = Mag-check ng Updates
Main_CheckForUpdates_AlreadyLatest = Nasa pinakabagong version ka na.
Main_CheckForUpdates_Available_Format = May available na update: v{0}.\n\nGamitin ang Help → Install Update para i-download at i-install.   # {0} = available semver
```

---

## Uploads tab

Toolbar, context menu, columns, overview panel, filter, and footer link.

```
Uploads_Toolbar_AddTip            = Magdagdag ng bagong upload
Uploads_Toolbar_StartTip          = Simulan lahat ng uploads
Uploads_Toolbar_PauseTip          = I-pause / Ipagpatuloy
Uploads_Toolbar_StopTip           = Itigil lahat ng uploads
Uploads_Toolbar_RemoveTip         = Tanggalin ang napili

Uploads_Context_Start             = Simulan
Uploads_Context_StartNow          = Simulan ngayon
Uploads_Context_Stop              = Itigil
Uploads_Context_SkipUpload        = Laktawan ang Upload
Uploads_Context_Reset             = I-reset
Uploads_Context_OpenSourceDir     = Buksan ang Source Directory
Uploads_Context_SetSpeedLimit     = Itakda ang Speed Limit...
Uploads_Context_Priority          = Priyoridad
Uploads_Context_Remove            = Tanggalin
Uploads_Priority_Highest          = Pinakamataas
Uploads_Priority_High             = Mataas
Uploads_Priority_Normal           = Normal
Uploads_Priority_Low              = Mababa
Uploads_Priority_Lowest           = Pinakamababa
Uploads_Tooltip_IncreasePriority  = Itaas ang priyoridad
Uploads_Tooltip_DecreasePriority  = Ibaba ang priyoridad

Uploads_Col_Name                  = Pangalan
Uploads_Col_Size                  = Laki
Uploads_Col_Hoster                = Hoster
Uploads_Col_Status                = Status
Uploads_Col_Speed                 = Bilis
Uploads_Col_ETA                   = ETA
Uploads_Col_BytesLoaded           = Bytes Na-load
Uploads_Col_BytesRemaining        = Natitirang Bytes
Uploads_Col_Progress              = Progreso
Uploads_Col_SaveTo                = I-save sa
Uploads_Col_Added                 = Idinagdag
Uploads_Col_Finished              = Tapos
Uploads_Col_ScheduledAt           = Naka-iskedyul sa
Uploads_Col_Duration              = Tagal
Uploads_Col_Priority              = Priority
Uploads_Col_SpeedLimit            = Speed Limit
Uploads_Col_URL                   = URL
Uploads_Col_Hash                  = Hash
Uploads_Col_Error                 = Error

Uploads_ColumnHeader_LockTip      = I-lock ang lapad ng column
Uploads_ColumnMenu_Reset          = I-reset ang mga column
Uploads_ColumnMenu_DefaultLabel   = Column                                              # fallback when a column has no header text

Uploads_Overview_Title            = Upload Overview
Uploads_Overview_CloseTip         = Isara ang overview
Uploads_Overview_ToggleTip        = Ipakita / itago ang overview stats

Uploads_Overview_Packages         = Package(s)
Uploads_Overview_Links            = Link(s)
Uploads_Overview_TotalBytes       = Total na Bytes
Uploads_Overview_Uploadspeed      = Bilis ng Upload
Uploads_Overview_BytesLoaded      = Bytes na-load
Uploads_Overview_RemainingBytes   = Natitirang Bytes
Uploads_Overview_Eta              = ETA
Uploads_Overview_RunningUploads   = Tumatakbong Uploads
Uploads_Overview_OpenConnections  = Bukas na Connections
Uploads_Overview_FinishedLinks    = Tapos na Link(s)
Uploads_Overview_SkippedLinks     = Nilaktawang Link(s)
Uploads_Overview_FailedLinks      = Failed na Link(s)

# Same labels appear inline with a trailing colon. Splitting them so a translator can
# control whether the colon is part of the term (typographic spacing differs in CJK).
Uploads_Overview_PackagesLabel        = Package(s):
Uploads_Overview_LinksLabel           = Link(s):
Uploads_Overview_TotalBytesLabel      = Total na Bytes:
Uploads_Overview_UploadspeedLabel     = Bilis ng Upload:
Uploads_Overview_BytesLoadedLabel     = Bytes na-load:
Uploads_Overview_RemainingBytesLabel  = Natitirang Bytes:
Uploads_Overview_EtaLabel             = ETA:
Uploads_Overview_RunningUploadsLabel  = Tumatakbong Uploads:
Uploads_Overview_OpenConnectionsLabel = Bukas na Connections:
Uploads_Overview_FinishedLinksLabel   = Tapos na Link(s):
Uploads_Overview_SkippedLinksLabel    = Nilaktawang Link(s):
Uploads_Overview_FailedLinksLabel     = Failed na Link(s):

Uploads_FilterLabel               = Pangalan ng File
Uploads_FilterTip                 = I-filter ayon sa pangalan ng file o package
Uploads_FooterPremiumLink         = Magdagdag ng Premium Account…

# Item-state names rendered in the Status column (FileStateDisplayConverter).
Uploads_State_Idle                = Idle
Uploads_State_HashQueued          = Naka-queue ang Hash
Uploads_State_Hashing             = Naghahash
Uploads_State_UploadQueued        = Naka-queue ang Upload
Uploads_State_Uploading           = Nag-uupload
Uploads_State_Completed           = Tapos na
Uploads_State_CompletedWithErrors = Tapos na may error
Uploads_State_Failed              = Failed
Uploads_State_Paused              = Naka-pause
Uploads_State_Cancelled           = Kinansela

# ETA fallback (UploadsViewModel)
Uploads_Eta_NotApplicable         = ~

# Reset-columns confirmation (different wording per tab)
Uploads_ResetColumns_Title        = I-reset ang mga column
Uploads_ResetColumns_Message      = I-reset ang mga column ng Uploads tab sa default? Ito ay magbubura ng anumang custom show/hide at ordering na itinakda mo.

# Remove confirmation prompts (UploadsViewModel)
Uploads_Remove_Title              = Tanggalin
Uploads_Remove_Package_Format     = Tanggalin ang package na '{0}' at ang {1} file(s) nito?                            # {0} = package name, {1} = file count
Uploads_Remove_File_Format        = Tanggalin ang '{0}' sa upload list?                                   # {0} = file name
Uploads_Remove_Generic            = Tanggalin ang item na ito?
Uploads_Remove_PackagesOnly_Format     = Tanggalin ang {0} package(s) ({1} file(s))?                            # {0} = package count, {1} = total file count
Uploads_Remove_FilesOnly_Format        = Tanggalin ang {0} file(s) sa upload list?                       # {0} = file count
Uploads_Remove_PackagesAndFiles_Format = Tanggalin ang {0} package(s) at {1} file(s) ({2} item(s) lahat)?      # {0} = packages, {1} = loose files, {2} = total

# Reset confirmation prompts
Uploads_Reset_Title                  = I-reset
Uploads_Reset_Package_Format         = I-reset ang package na '{0}'? Mag-re-rehash at muling i-aupload ang {1} natapos na file(s) sa package na ito.  # {0} = package name, {1} = completed file count
Uploads_Reset_File_Format            = I-reset ang '{0}'? Tapos na itong na-upload — ang pag-reset ay magre-rehash at muling mag-uupload nito.  # {0} = file name
```

---

## Uploaded tab

```
Uploaded_Toolbar_ExportJson       = I-export sa JSON…
Uploaded_Toolbar_ExportJsonTip    = I-export ang lahat ng natapos na uploads (kasama lahat ng field) sa JSON file

Uploaded_Col_Name                 = Pangalan
Uploaded_Col_Path                 = Path
Uploaded_Col_Size                 = Laki
Uploaded_Col_Hoster               = Hoster
Uploaded_Col_Finished             = Tapos
Uploaded_Col_URL                  = URL
Uploaded_Col_Hash                 = Hash

Uploaded_Context_Copy             = Kopyahin
Uploaded_Context_Copy_Gesture     = Ctrl+C
Uploaded_Context_CopyURL          = Kopyahin ang URL
Uploaded_Context_Remove           = Tanggalin
Uploaded_Context_Remove_Gesture   = Del
Uploaded_Context_ExportJson       = I-export sa JSON…

# Reset-columns confirmation
Uploaded_ResetColumns_Title       = I-reset ang mga column
Uploaded_ResetColumns_Message     = I-reset ang mga column ng History tab sa default? Ito ay magbubura ng anumang custom show/hide at ordering na itinakda mo.

# Remove confirmation
Uploaded_Remove_Title             = Tanggalin
Uploaded_Remove_Single_Format     = Tanggalin ang '{0}' sa history?                                          # {0} = file name
Uploaded_Remove_Many_Format       = Tanggalin ang {0} entries sa history?                                    # {0} = entry count
```

---

## Settings — General

```
Settings_Sidebar_General          = General
Settings_Sidebar_Upload           = Upload
Settings_Sidebar_Connection       = Connection
Settings_Sidebar_Accounts         = Accounts

Settings_General_Language_Title        = Wika
Settings_General_Language_Desc         = Wika ng UI. Ang pagbabago ay agad na ia-apply.
Settings_General_Language_Label        = Wika

Settings_General_Developer_Title       = Developer
Settings_General_Developer_Desc        = Mga option para sa local development at testing.
Settings_General_UseMockServer         = Gamitin ang mock server (i-redirect lahat ng file hoster requests sa localhost:8080/<hoster>)   # review — hidden behind a developer flag, may stay English

Settings_General_GridAppearance_Title  = Hitsura ng Grid
Settings_General_GridAppearance_Desc   = Font na gagamitin para sa Uploads at History tabs. Ang mga pagbabago ay agad na ia-apply.
Settings_General_GridFont              = Grid font
Settings_General_GridFontSize          = Sukat ng grid font

Settings_General_WindowBehaviour_Title = Galaw ng Window
Settings_General_WindowBehaviour_Desc  = Pumili kung ano ang mangyayari kapag ang main window ay na-minimize o na-close.
Settings_General_MinimizeToTray        = I-minimize ang main window sa system tray sa halip na sa taskbar
Settings_General_CloseAction           = Aksyon ng close button

Settings_General_CloseAction_Ask         = Magtanong sa bawat pagkakataon
Settings_General_CloseAction_MinToTray   = I-minimize sa tray
Settings_General_CloseAction_Exit        = Lumabas sa application

Settings_General_Notifications_Title  = Mga Abiso
Settings_General_Notifications_Desc   = Lalabas na popup sa kanang-ibaba kapag tapos na ang pag-upload.
Settings_General_ShowCompletionToasts = Magpakita ng popup na abiso kapag tapos na ang pag-upload

Settings_General_ConfirmationPrompts_Title = Confirmation Prompts
Settings_General_ConfirmationPrompts_Desc  = I-tick ang isang prompt para magtanong ulit bago ang aksyon. I-untick para hindi na magpakita ng confirmation para sa aksyong iyon.

Settings_General_Database_Title            = Database
Settings_General_Database_Desc             = Iniimbak ng CSUploader ang upload history mo (mga package, file, at URL) sa isang lokal na SQLite database. Ang pag-alis ng entries sa Uploads o History tab ay nagtatago lamang ng mga ito — nananatili ang rows sa database. I-click ang Clear para permanenteng tanggalin ang mga rows na nakatago sa parehong tab.
Settings_General_Database_BtnClear         = Clear
Settings_General_Database_ConfirmTitle     = I-clear ang database
Settings_General_Database_ConfirmMessage   = Permanenteng tanggalin ang upload records na nakatago sa parehong tab?\n\nHindi maaapektuhan ang aktibo at nakikitang uploads. Hindi na ito maibabalik.
Settings_General_Database_Status_Cleared_Format    = Na-clear ang {0} file row at {1} package row mula sa database.
Settings_General_Database_Status_NothingToClear    = Walang nakatagong rows na ic-clear.
Settings_General_Database_BtnClearLogs              = Clear logs
Settings_General_Database_ConfirmClearLogsTitle     = Clear log history
Settings_General_Database_ConfirmClearLogsMessage   = Permanently delete all log entries from the database?\n\nThe Logs tab will also be emptied. This cannot be undone.
Settings_General_Database_LogsCleared_Format        = Cleared {0} log entr(ies) from the database.
Settings_General_Database_LogsNothingToClear        = No log entries to clear.
```

---

## Toast notifications

```
Toast_FileCompleted_Title    = Tapos na ang pag-upload
Toast_FileCompleted_Body     = {0}
Toast_PackageCompleted_Title = Tapos na ang package
Toast_PackageCompleted_Body  = {0} sa {1} na file ang na-upload — {2}
```

---

## Settings — Upload

```
Settings_Upload_Mgmt_Title         = Upload Management
Settings_Upload_Mgmt_Desc          = Connection limits, priorities, ...i-set up ang mga detalye ng Upload controller.
Settings_Upload_MaxConcurrent      = Max. na sabay-sabay na Uploads
Settings_Upload_MaxPerHoster       = Max. na sabay-sabay na uploads per file hoster
Settings_Upload_RemoveFinished     = Tanggalin ang tapos na uploads
Settings_Upload_IfFileExists       = Kung umiiral na ang file
Settings_Upload_MaxCpuJobs         = Max. na sabay-sabay na CPU Jobs
Settings_Upload_SpeedLimit         = Speed Limit (KB/s)

Settings_Upload_RemoveFinished_Never              = Hinding-hindi
Settings_Upload_RemoveFinished_Immediately        = Agad-agad
Settings_Upload_RemoveFinished_AtStartup          = Sa startup
Settings_Upload_RemoveFinished_WhenPackageReady   = Kapag handa na ang package

Settings_Upload_IfExists_Ask                      = Magtanong para sa bawat file
Settings_Upload_IfExists_Skip                     = Laktawan
Settings_Upload_IfExists_Overwrite                = I-overwrite
Settings_Upload_IfExists_Rename                   = Palitan ang pangalan

Settings_Upload_Autostart_Title                   = Autostart na Uploads
Settings_Upload_Autostart_Desc                    = Pumili kung ang CSUploader ay magsisimula ng pending na uploads nang walang user interaction, at kung kailan.
Settings_Upload_Autostart                         = Autostart na uploads sa pagsisimula ng application

Settings_Upload_Autostart_Always                  = Palagi
Settings_Upload_Autostart_OnlyIfRunning           = Kung tumatakbo ang uploads sa pagtatapos ng huling session lamang
Settings_Upload_Autostart_Never                   = Hinding-hindi
```

---

## Settings — Connection (proxy manager)

```
Settings_Conn_Title                = Connection Manager
Settings_Conn_Desc                 = Kung kailangan ng proxy para makapunta sa internet, i-configure mo sila dito. Ang maraming proxies ay iniikot para sa bagong uploads. Ang default na behavior na walang naka-enable na proxies ay direct connection.
Settings_Conn_UseProxies           = Gumamit ng proxies
Settings_Conn_UseProxiesTip        = Master switch para sa rotation. Kapag naka-off, ang lahat ng trapiko (uploads at account checks) ay direkta na kumokonekta kahit may proxies sa grid — kapaki-pakinabang para sa pagdaragdag at pag-test ng proxies bago committed na gamitin sila.
Settings_Conn_AutoDisable          = Awtomatikong i-uncheck ang failing na proxies
Settings_Conn_AutoDisableTip       = Kapag naka-on, ang isang proxy na nag-fail sa manual test o sa upload ay ina-uncheck para laktawan ng rotation. Ang status icon ay nag-a-update kahit anong mangyari.

Settings_Conn_Col_On               = On
Settings_Conn_Col_Priority         = Priority
Settings_Conn_Col_Type             = Type
Settings_Conn_Col_Host             = Host / IP
Settings_Conn_Col_Port             = Port
Settings_Conn_Col_User             = User
Settings_Conn_Col_Password         = Password
Settings_Conn_Col_Test             = Test
Settings_Conn_Col_Status           = Status

Settings_Conn_PriorityUpTip        = Ilipat pataas (mas mataas na priority)
Settings_Conn_PriorityDownTip      = Ilipat pababa (mas mababang priority)

Settings_Conn_Context_Test         = I-test
Settings_Conn_Context_Remove       = Tanggalin
Settings_Conn_Context_Remove_Gesture = Del

Settings_Conn_Btn_Import           = Mag-import
Settings_Conn_Btn_Import_FromText  = Mag-import mula sa text…
Settings_Conn_Btn_Import_FromFile  = Mag-import mula sa file…
Settings_Conn_Btn_Export           = Mag-export
Settings_Conn_Btn_Export_AllToText = I-export lahat ng proxies sa text…
Settings_Conn_Btn_Export_AllToFile = I-export lahat ng proxies sa file…
Settings_Conn_Btn_Export_OkToText  = I-export ang tested-OK na proxies sa text…
Settings_Conn_Btn_Export_OkToFile  = I-export ang tested-OK na proxies sa file…
Settings_Conn_Btn_Export_SelectedToText = I-export ang napiling proxies sa text…
Settings_Conn_Btn_Export_SelectedToFile = I-export ang napiling proxies sa file…
Settings_Conn_Btn_Save             = I-save
Settings_Conn_Btn_Add              = Idagdag
Settings_Conn_Btn_Remove           = Tanggalin
Settings_Conn_Btn_RemoveSelected   = Tanggalin ang napili
Settings_Conn_Btn_RemoveFailed     = Tanggalin ang failed
Settings_Conn_Btn_TestAll          = I-test Lahat
Settings_Conn_Btn_TestAllTip       = I-test ang connectivity para sa bawat proxy sa list
Settings_Conn_Btn_Details          = Details

# Proxy import/export dialogs (ProxyTextDialog ctor args, ConnectionManagerViewModel)
Settings_Conn_ImportProxies_FileDialogTitle = Mag-import ng proxies
Settings_Conn_ImportProxies_FileFilter      = Proxy lists (*.txt)|*.txt|All files (*.*)|*.*
Settings_Conn_ImportProxies_DialogTitle     = Mag-import ng Proxies
Settings_Conn_ImportProxies_DialogDesc      = I-paste ang proxy lines (isa kada linya). Format: scheme://[user:pass@]host[:port] — ang port ay default sa 80/443/1080 ayon sa scheme.
Settings_Conn_ExportAll_DialogTitle         = I-export Lahat ng Proxies
Settings_Conn_ExportOk_DialogTitle          = I-export ang Tested-OK na Proxies
Settings_Conn_ExportAll_Desc_Format         = {0} proxy(s):                                  # {0} = count of all proxies
Settings_Conn_ExportOk_Desc_Format          = {0} proxy(s) na may successful na huling test:      # {0} = count of OK proxies
Settings_Conn_ExportSelected_DialogTitle    = I-export ang Napiling Proxies
Settings_Conn_ExportSelected_Desc_Format    = {0} napiling proxy(s):                         # {0} = count of selected proxies

# Proxy remove confirmations (ConnectionManagerViewModel)
Settings_Conn_RemoveProxy_Title             = Tanggalin ang proxy
Settings_Conn_RemoveProxy_One_Format        = Tanggalin ang proxy na '{0}:{1}'?                        # {0} = host, {1} = port
Settings_Conn_RemoveProxy_Many_Format       = Tanggalin ang {0} proxies?                            # {0} = count
Settings_Conn_RemoveFailedProxy_Title       = Tanggalin ang failed na proxies
Settings_Conn_RemoveFailedProxy_One_Format  = Tanggalin ang failed na proxy na '{0}:{1}'?             # {0} = host, {1} = port
Settings_Conn_RemoveFailedProxy_Many_Format = Tanggalin ang {0} failed na proxies?                     # {0} = count

# Proxy test/save status strings (ConnectionManagerViewModel / ProxySettingItem)
Settings_Conn_Status_Queued                 = Naka-queue…
Settings_Conn_Status_Testing                = Tine-test…
Settings_Conn_Status_OkLive                 = OK (live)
Settings_Conn_Status_OkLatencyIp_Format     = OK {0}ms ({1})                                 # {0} = ms, {1} = detected IP
Settings_Conn_Status_OkLatencyUnknown_Format = OK {0}ms (hindi inaasahang response)                # {0} = ms
Settings_Conn_Status_Failed_Format          = Failed: {0}                                    # {0} = error first line / message
Settings_Conn_Status_Saved                  = Na-save
Settings_Conn_Status_SaveFailed_Format      = Hindi na-save: {0}                               # {0} = error message
Settings_Conn_Status_Imported_Format        = Na-import ang {0} proxy(s)                     # {0} = proxy count
Settings_Conn_Status_ExportedToFile_Format  = Na-export ang {0} proxy(s) sa {1}                   # {0} = count, {1} = file name
```

---

## Settings — Accounts

```
Settings_Accounts_Title            = Account Manager
Settings_Accounts_Desc             = Ilagay at pamahalaan lahat ng iyong Premium/Gold/Platinum accounts.

Settings_Accounts_Col_Enabled      = ✓                                                       # check-mark glyph header — leave as glyph or localize as e.g. "On"
Settings_Accounts_Col_Hoster       = Hoster
Settings_Accounts_Col_Status       = Status
Settings_Accounts_Col_Username     = Username
Settings_Accounts_Col_Password     = Password
Settings_Accounts_Col_Type         = Type

Settings_Accounts_Context_Edit     = I-edit ang Account...
Settings_Accounts_Context_Refresh  = I-check / I-refresh
Settings_Accounts_Context_Enable   = I-enable
Settings_Accounts_Context_Disable  = I-disable
Settings_Accounts_Context_Delete   = Burahin

Settings_Accounts_Btn_Add          = Idagdag
Settings_Accounts_Btn_Remove       = Tanggalin
Settings_Accounts_Btn_Refresh      = I-refresh

# Account remove / validation
Settings_Accounts_Remove_Title             = Tanggalin ang Account
Settings_Accounts_Remove_Message_Format    = Tanggalin ang account na '{0}' para sa {1}?                  # {0} = username, {1} = file hoster name

Settings_Accounts_Validation_FillHosterUser = Mangyaring punan ang file hoster, username, at password.
Settings_Accounts_Check_DialogTitle         = Account Check
Settings_Accounts_Check_FailedAddAnyway_Format = Nag-fail ang account check: {0}\n\nIdagdag pa rin?    # {0} = error message
Settings_Accounts_Check_CouldNotVerifyAddAnyway_Format = Hindi ma-verify ang account: {0}\n\nIdagdag pa rin?   # {0} = error message

# CheckAccountStatus inline status messages
Settings_Accounts_Status_Verifying          = Vinerify ang credentials...
Settings_Accounts_Status_Checking           = Chinecheck ang account...
Settings_Accounts_Status_NoAccountsToRefresh = Walang accounts na ire-refresh.
Settings_Accounts_Status_Verified_Format    = Na-verify: {0}                                  # {0} = result.Message
Settings_Accounts_Status_Warning_Format     = Babala: {0}                                   # {0} = result.Message
Settings_Accounts_Status_Valid_Format       = Valid: {0}                                     # {0} = result.Message
Settings_Accounts_Status_ValidExclaim_Format = Valid! {0}                                    # {0} = result.Message  (separate from above — different exclamation)
Settings_Accounts_Status_Failed_Format      = Failed: {0}                                    # {0} = result.Message
Settings_Accounts_Status_CheckError_Format  = Check error: {0}                               # {0} = exception.Message
Settings_Accounts_Status_Error_Format       = Error: {0}                                     # {0} = exception.Message
Settings_Accounts_Status_AccountAdded_Format = Naidagdag ang account para sa {0}!                        # {0} = file hoster name
Settings_Accounts_Status_NoImpl_Format      = Walang implementation para sa {0}. Hindi ma-check.       # {0} = file hoster name
Settings_Accounts_Status_NoImplWillSave_Format = Walang implementation para sa {0}. Ang account ay isa-save nang walang verification.   # {0} = file hoster name
Settings_Accounts_Status_CheckingProgress_Format = Chinecheck ang {0}@{1}... ({2}/{3})             # {0} = username, {1} = hoster, {2} = current, {3} = total
Settings_Accounts_Status_CheckingShort      = Chinecheck...
Settings_Accounts_Status_NoImpl             = Walang implementation
Settings_Accounts_Status_RefreshSummary_Format = Na-refresh ang {0} accounts. {1} ang na-update.        # {0} = checked count, {1} = updated count
Settings_Accounts_Status_AccountDisabled_Format = Naka-disable ang account na '{0}'.                    # {0} = username
Settings_Accounts_Status_AccountEnabled_Format  = Naka-enable ang account na '{0}'.                     # {0} = username

# AccountCheckResult fallback strings (SettingsViewModel)
Settings_Accounts_DefaultStatus_OK        = OK
Settings_Accounts_DefaultStatus_Failed    = Failed

# Password column placeholder
Settings_Accounts_PasswordMask            = ******
```

---

## Logs tab

```
Logs_AutoScroll                    = Auto-scroll
Logs_BtnClear                      = Clear
Logs_Tab_Status                    = Status
Logs_Tab_Http                      = HTTP
Logs_Tab_Errors                    = Errors
Logs_Tab_UI                        = UI

Logs_Col_DateTime                  = DateTime
Logs_Col_Status                    = Status
Logs_Col_Filename                  = Filename
Logs_Col_Function                  = Function
Logs_Col_Line                      = Line
Logs_Col_Message                   = Message
Logs_Col_Thread                    = Thread

# Status messages logged to the Status tab (UploadedViewModel) — these surface to the
# user via the Logs tab so they're worth localising.
Logs_Status_NoUrlsClipboardCleared = Walang URLs sa selection; nilinis ang clipboard
Logs_Status_CopiedUrls_Format      = Nakopya ang {0} URL(s) sa clipboard                          # {0} = url count
Logs_Status_HiddenFiles_Format     = Itinago ang {0} file(s) sa History tab                   # {0} = file count
Logs_Status_ExportedPackages_Format = Na-export ang {0} package(s) sa {1}                         # {0} = pkg count, {1} = file path
```

---

## Upload Wizard

```
Wizard_Title                       = Upload Wizard

Wizard_Step_DirectorySource        = 1. Directory
Wizard_Step_FileHosters            = 2. File Hosters
Wizard_Step_Start                  = 3. Simulan
Wizard_Step_FilesSource            = 1. Mga File

Wizard_Step0_Mode_Directory        = Mag-upload ng directory
Wizard_Step0_Mode_Files            = Mag-upload ng mga file

Wizard_Step0_Title                 = Pumili ng Upload Directory
Wizard_Step0_Desc                  = Pumili ng directory na naglalaman ng mga files na gusto mong i-upload.
Wizard_Step0_Browse                = Mag-browse
Wizard_Step0_BrowseDialogTitle     = Pumili ng Upload Directory                                 # used when calling BrowseFolder

Wizard_Step1_Title                 = Pumili ng Files
Wizard_Step1_PackageTitleLabel     = Titulo ng package:
Wizard_Step1_FilterLabel           = Filter:
Wizard_Step1_BtnSelectAll          = Piliin lahat
Wizard_Step1_BtnDeselectAll        = Alisin sa pagpili lahat
Wizard_Step1_Col_File              = File
Wizard_Step1_Col_Size              = Laki
Wizard_Step1_SelectedLabel         = Napili:
Wizard_Step1_FilesUnit             = file(s)

Wizard_Step2_Title                 = Pumili ng File Hosters
Wizard_Step2_Desc                  = Pumili kung saang file hosters mag-uupload at pumili ng accounts.
Wizard_Step2_Col_Use               = Gamitin
Wizard_Step2_Col_FileHoster        = File Hoster
Wizard_Step2_Col_Account           = Account
Wizard_Step2_AccountAnonymous      = (anonymous)
Wizard_Step2_AccountSelect         = (pumili ng account)
Wizard_Step2_AddAccountLink        = Magdagdag ng account…
Wizard_Step2_AccountRequiredTooltip = Kailangan ng account para sa hoster na ito. I-click ang "Magdagdag ng account…" para magdagdag.
Wizard_Hoster_LimitsHeader         = Lalampasan ang mga limitasyon ng hoster na ito:
Wizard_Hoster_FileTooLarge_Format  = {0}: Lalampas sa limitasyon na {1} kada file ang mga sumusunod na file at hindi mai-upload:\n{2}
Wizard_Hoster_TooManyFiles_Format  = {0}: {1} file ang napili, ngunit ang limitasyon kada package ay {2}.

Wizard_Step3_Title                 = Kailan Magsisimula
Wizard_Step3_Desc                  = Pumili kung kailan magsisimula ang upload.
Wizard_Step3_Mode_Immediately      = Magsimula agad pagkatapos isara ang wizard
Wizard_Step3_Mode_Later            = Idagdag sa queue pero magsimula mamaya (manual start)
Wizard_Step3_Mode_Scheduled        = Mag-iskedyul para sa partikular na petsa at oras
Wizard_Step3_TimeFormatHint        = (HH:mm)

Wizard_Btn_Back                    = Bumalik
Wizard_Btn_Cancel                  = Kanselahin
Wizard_Btn_Next                    = Susunod
Wizard_Btn_Add                     = Idagdag

# Validation errors (UploadWizardViewModel.ShowError)
Wizard_Validation_PickValidDir     = Mangyaring pumili ng valid na directory.
Wizard_Validation_PickFile         = Mangyaring pumili ng kahit isang file.
Wizard_Validation_PickHoster       = Mangyaring pumili ng kahit isang file hoster.
Wizard_Error_Format                = Error: {0}                                              # {0} = exception.Message

Wizard_Step0_Files_Title           = Pumili ng mga File
Wizard_Step0_Files_Desc            = Pumili ng mga file na gusto mong i-upload. Maaari kang magdagdag ng higit pa mamaya.
Wizard_Step0_Files_Pick            = Magdagdag ng mga file…
Wizard_Step0_Files_BrowseDialogTitle = Pumili ng mga file para i-upload                                  # used when calling BrowseFiles

Wizard_Step1_DuplicateFilenameSuffixFormat = {0} (nasa {1})                                    # {0} = filename, {1} = parent folder name

Wizard_Validation_PickAtLeastOneFile = Pumili ng kahit isang file bago magpatuloy.
Wizard_Validation_TitleRequired    = Maglagay ng titulo ng package.
```

---

## Confirmation Prompts (Settings → General list labels)

These are the user-visible labels for `ConfirmationKeys.All` — the strings shown in the
"Confirmation Prompts" section of Settings. Stable IDs (`remove-upload-package-or-file`
etc.) stay English.

```
Confirm_RemoveUploadPackageOrFile  = Tanggalin ang package o file sa Uploads tab
Confirm_RemoveUploadedEntry        = Tanggalin ang entries sa History tab
Confirm_RemoveFileHosterAccount    = Tanggalin ang isang file hoster account
Confirm_RemoveProxy                = Tanggalin ang isang proxy sa Connection Manager
Confirm_ResetCompletedUpload       = I-reset ang tapos na upload (i-rehash at muling i-upload)
Confirm_ResetColumns               = I-reset ang mga column sa default sa Uploads / History tab
```

---

## Dialog windows

### About

```
About_WindowTitle                  = Tungkol sa CSUploader
About_AppName                      = CSUploader
About_Version_Format               = Version {0}                                             # {0} = assembly version, e.g. "1.2.3"
About_Description                  = Isang malakas na file upload manager para sa maraming hosting services. Kasama ang hashing, queue management, at real-time progress tracking.
About_Field_Framework              = Framework:
About_Field_Framework_Value        = .NET 10.0 (WPF)
About_Field_Database               = Database:
About_Field_Database_Value         = SQLite via EF Core 10
About_Field_License                = License:
About_Field_License_Value          = MIT
About_Field_Source                 = Source:
About_OK                           = OK
```

### CloseAction dialog

```
CloseAction_WindowTitle            = Isara ang CSUploader
CloseAction_Heading                = Ano ang gusto mong gawin ng close button?
CloseAction_Subheading             = Pumili ng isa — pwede mo itong baguhin mamaya sa Settings → General.
CloseAction_Remember               = Tandaan ang aking pinili
CloseAction_BtnMinimize            = I-minimize sa tray
CloseAction_BtnExit                = Lumabas
CloseAction_BtnCancel              = Kanselahin
```

### Confirmation dialog

```
Confirmation_WindowTitle           = Kumpirmahin
Confirmation_DontAskAgain          = Huwag nang itanong sa akin para sa aksyong ito
Confirmation_BtnYes                = Oo
Confirmation_BtnNo                 = Hindi
```

### EditAccount dialog

```
EditAccount_WindowTitle            = I-edit ang Account
EditAccount_AddTitle               = Magdagdag ng Account                                             # used by SettingsViewModel.AddAccountDialog
EditAccount_FileHosterLabel        = File Hoster:
EditAccount_UsernameLabel          = Username:
EditAccount_PasswordLabel          = Password:
EditAccount_AccountEnabled         = Naka-enable ang account
EditAccount_BtnSave                = I-save
EditAccount_BtnCancel              = Kanselahin
EditAccount_Validation_RequireUsernameAndPassword = Mangyaring ipasok ang username at password.

EditProxy_AddTitle                 = Magdagdag ng Proxy
EditProxy_EditTitle                = I-edit ang Proxy
EditProxy_EnabledLabel             = Pinagana ang proxy
EditProxy_BtnSave                  = I-save
EditProxy_BtnCancel                = Kanselahin
EditProxy_BtnTest                  = Subukan
EditProxy_Validation_HostRequired  = Mangyaring ipasok ang host o IP address.
EditProxy_Validation_PortInvalid   = Mangyaring ipasok ang wastong port sa pagitan ng 1 at 65535.
EditProxy_Status_Testing           = Sinusubukan…
EditProxy_Status_OkLatency_Format  = OK {0}ms (hindi inaasahang tugon)
EditProxy_Status_OkLatencyIp_Format = OK {0}ms ({1})
EditProxy_Status_Failed_Format     = Nabigo: {0}
```

### HttpDetails window

```
HttpDetails_WindowTitle            = HTTP Transaction Details
HttpDetails_Tab_Request            = Request
HttpDetails_Tab_Response           = Response
HttpDetails_Tab_FullDump           = Full Dump
HttpDetails_SubTab_Headers         = Headers
HttpDetails_SubTab_BodyRaw         = Body (Raw)
HttpDetails_SubTab_BodyJson        = Body (JSON)
HttpDetails_SubTab_Hex             = Hex

# Header strip
HttpDetails_Timing_Format          = Sinimulan: {0}  |  Tagal: {1}ms  |  Laki: {2} bytes    # {0} = HH:mm:ss.fff, {1} = ms, {2} = byte count
HttpDetails_Proxy_Format           = Proxy: {0}                                              # {0} = proxy display string
HttpDetails_NoData                 = (walang data)
HttpDetails_NoBody                 = (walang body)
# Section dividers used in the Full Dump (these are framed in box-drawing chars; the
# label words are the only translatable parts).
HttpDetails_FullDump_Request       = REQUEST
HttpDetails_FullDump_Response      = RESPONSE
```

### LogDetails window

```
LogDetails_WindowTitle             = Log Details
LogDetails_Field_DateTime          = DateTime:
LogDetails_Field_ThreadId          = Thread ID:
LogDetails_Field_Filename          = Filename:
LogDetails_Field_Function          = Function:
LogDetails_Field_Line              = Line:
LogDetails_Tab_Text                = Text
LogDetails_Tab_Html                = HTML
LogDetails_Btn_Close               = Isara
```

### Progress / UpdateProgress windows

```
Progress_WindowTitle               = Mangyaring maghintay...
Progress_DefaultLabel              = Naglo-load...
Progress_LabelSuffix               = Mangyaring maghintay...                                          # appended to the caller-supplied label on a new line
Progress_BtnCancel                 = Kanselahin
Progress_BtnCancelling             = Kinakansela...

UpdateProgress_WindowTitle         = Ina-update ang CSUploader
UpdateProgress_StatusInitial       = Naghahanda…
UpdateProgress_StatusDownloading_Format = Dina-download ang update v{0}…                           # {0} = available semver
UpdateProgress_StatusRestarting    = Nire-restart…
UpdateProgress_StatusFailed_Format = Nag-fail ang update: {0}                                      # {0} = exception.Message
UpdateProgress_PercentInitial      = 0%
```

### ProxyText dialog

```
ProxyText_WindowTitle              = Proxies                                                 # XAML default — overridden via ctor with Import/Export titles
ProxyText_BtnImport                = Mag-import
ProxyText_BtnCopy                  = Kopyahin
ProxyText_BtnCancel                = Kanselahin
ProxyText_BtnClose                 = Isara                                                   # replaces Cancel in read-only export mode
```

### SpeedLimit dialog

```
SpeedLimit_WindowTitle             = Speed Limit
SpeedLimit_Heading                 = Itakda ang Speed Limit
SpeedLimit_Subheading              = Per-package override. Iwanang blangko para gamitin ang global setting.
SpeedLimit_Unit                    = KB/s
SpeedLimit_BtnClear                = Linisin
SpeedLimit_BtnCancel               = Kanselahin
SpeedLimit_BtnOk                   = OK
SpeedLimit_Validation_Title        = Invalid na value
SpeedLimit_Validation_Message      = Mangyaring maglagay ng positibong integer (KB/s), o iwanang blangko para linisin.
```

---

## Status / inline messages

These are short status strings shown in non-dialog inline UI. Several have already been
listed in their owning section above; this section catches the rest, plus dialog-service
default titles.

```
Dialog_DefaultErrorTitle           = Error
Dialog_DefaultConfirmTitle         = Kumpirmahin
Dialog_DefaultBrowseFolderTitle    = Pumili ng Folder
Dialog_GenericErrorTitle           = Error                                                   # ProgressWindow exception fallback

# File-picker filters (Microsoft.Win32 OpenFileDialog / SaveFileDialog)
Picker_Filter_Json                 = JSON files (*.json)|*.json|All files (*.*)|*.*
Picker_Filter_ProxyLists           = Proxy lists (*.txt)|*.txt|All files (*.*)|*.*
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
Tray_Menu_Show                     = Ipakita ang CSUploader                                         # "Show " + brand; localise the verb only
Tray_Menu_Exit                     = Lumabas
Tray_Balloon_Title                 = CSUploader                                              # brand — do not translate
Tray_Balloon_Body                  = Tumatakbo pa rin sa tray. I-click ang icon para ibalik ang window, o i-right-click para sa Exit.
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

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
Common_Cancel                     = Cancel
Common_Yes                        = Yes
Common_No                         = No
Common_Save                       = Save
Common_Add                        = Add
Common_Remove                     = Remove
Common_Close                      = Close
Common_Edit                       = Edit
Common_Delete                     = Delete
Common_Browse                     = Browse
Common_Back                       = Back
Common_Next                       = Next
Common_Apply                      = Apply
Common_Refresh                    = Refresh
Common_Details                    = Details
Common_Copy                       = Copy
Common_Import                     = Import
Common_Export                     = Export
Common_All                        = All
Common_None                       = None
Common_Test                       = Test
Common_Confirm                    = Confirm
Common_Error                      = Error
Common_Warning                    = Warning
Common_SelectFolder               = Select Folder
Common_SelectFiles                 = Select files
Common_PleaseWait                 = Please wait...
Common_Loading                    = Loading...
Common_Cancelling                 = Cancelling...
Common_Preparing                  = Preparing…
Common_Unknown                    = unknown
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
Main_Title_UpdateAvailable_Format = CSUploader — Update available (v{0}) — click Help → Install Update     # {0} = available semver
Main_Menu_File                    = _File
Main_Menu_File_Exit               = _Exit
Main_Menu_File_Exit_Gesture       = Alt+F4
Main_Menu_View                    = _View
Main_Menu_View_UploadOverview     = Upload _Overview
Main_Menu_View_DarkMode           = _Dark Mode
Main_Menu_View_LightMode          = _Light Mode
Main_Menu_Help                    = _Help
Main_Menu_Help_CheckForUpdates    = _Check for Updates…
Main_Menu_Help_InstallUpdate      = _Install Update
Main_Menu_Help_About              = _About CSUploader

Main_Tab_Uploads                  = Uploads
Main_Tab_Uploaded                 = History
Main_Tab_Settings                 = Settings
Main_Tab_Logs                     = Logs

Main_CheckForUpdates_DialogTitle  = Check for Updates
Main_CheckForUpdates_AlreadyLatest = You're on the latest version.
Main_CheckForUpdates_Available_Format = Update available: v{0}.\n\nUse Help → Install Update to download and install.   # {0} = available semver
```

---

## Uploads tab

Toolbar, context menu, columns, overview panel, filter, and footer link.

```
Uploads_Toolbar_AddTip            = Add new upload
Uploads_Toolbar_StartTip          = Start all uploads
Uploads_Toolbar_PauseTip          = Pause / Resume
Uploads_Toolbar_StopTip           = Stop all uploads
Uploads_Toolbar_RemoveTip         = Remove selected

Uploads_Context_Start             = Start
Uploads_Context_ForceStart        = Force start
Uploads_Context_StartNow          = Start now
Uploads_Context_Stop              = Stop
Uploads_Context_SkipUpload        = Skip Upload
Uploads_Context_Reset             = Reset
Uploads_Context_OpenSourceDir     = Open Source Directory
Uploads_Context_SetSpeedLimit     = Set Speed Limit...
Uploads_Context_Move              = Move
Uploads_Context_Remove            = Remove
Uploads_Move_Up10                 = Up 10
Uploads_Move_Up1                  = Up 1
Uploads_Move_Down1                = Down 1
Uploads_Move_Down10               = Down 10
Uploads_Tooltip_MoveUp            = Move up (uploads sooner)
Uploads_Tooltip_MoveDown          = Move down (uploads later)

Uploads_Col_Name                  = Name
Uploads_Col_Size                  = Size
Uploads_Col_Hoster                = Hoster
Uploads_Col_Account               = Account
Uploads_Col_Status                = Status
Uploads_Col_Speed                 = Speed
Uploads_Col_ETA                   = ETA
Uploads_Col_BytesLoaded           = Bytes Loaded
Uploads_Col_BytesRemaining        = Bytes Remaining
Uploads_Col_Progress              = Progress
Uploads_Col_SaveTo                = Path
Uploads_Col_Added                 = Added
Uploads_Col_Finished              = Finished
Uploads_Col_Started               = Started
Uploads_Col_ScheduledAt           = Scheduled at
Uploads_Col_Duration              = Elapsed
Uploads_Col_Order                 = Order
Uploads_Col_SpeedLimit            = Speed Limit
Uploads_Col_URL                   = URL
Uploads_Col_Hash                  = Hash
Uploads_Col_Error                 = Error

Uploads_ColumnHeader_LockTip      = Lock column width
Uploads_ColumnMenu_Reset          = Reset columns
Uploads_ColumnMenu_DefaultLabel   = Column                                              # fallback when a column has no header text

Uploads_Overview_Title            = Upload Overview
Uploads_Overview_CloseTip         = Close overview
Uploads_Overview_ToggleTip        = Show / hide overview stats

Uploads_Overview_Packages         = Package(s)
Uploads_Overview_Links            = Link(s)
Uploads_Overview_TotalBytes       = Total Bytes
Uploads_Overview_Uploadspeed      = Upload speed
Uploads_Overview_BytesLoaded      = Bytes loaded
Uploads_Overview_RemainingBytes   = Remaining Bytes
Uploads_Overview_Eta              = ETA
Uploads_Overview_RunningUploads   = Running Uploads
Uploads_Overview_OpenConnections  = Open Connections
Uploads_Overview_FinishedLinks    = Finished Link(s)
Uploads_Overview_SkippedLinks     = Skipped Link(s)
Uploads_Overview_FailedLinks      = Failed Link(s)

# Same labels appear inline with a trailing colon. Splitting them so a translator can
# control whether the colon is part of the term (typographic spacing differs in CJK).
Uploads_Overview_PackagesLabel        = Package(s):
Uploads_Overview_LinksLabel           = Link(s):
Uploads_Overview_TotalBytesLabel      = Total Bytes:
Uploads_Overview_UploadspeedLabel     = Upload speed:
Uploads_Overview_BytesLoadedLabel     = Bytes loaded:
Uploads_Overview_RemainingBytesLabel  = Remaining Bytes:
Uploads_Overview_EtaLabel             = ETA:
Uploads_Overview_RunningUploadsLabel  = Running Uploads:
Uploads_Overview_OpenConnectionsLabel = Open Connections:
Uploads_Overview_FinishedLinksLabel   = Finished Link(s):
Uploads_Overview_SkippedLinksLabel    = Skipped Link(s):
Uploads_Overview_FailedLinksLabel     = Failed Link(s):

Uploads_FilterLabel               = File Name
Uploads_FilterTip                 = Filter by file or package name
Uploads_FooterPremiumLink         = Add a Premium Account…

# Item-state names rendered in the Status column (FileStateDisplayConverter).
Uploads_State_Idle                = Idle
Uploads_State_HashQueued          = Hash Queued
Uploads_State_Hashing             = Hashing
Uploads_State_UploadQueued        = Upload Queued
Uploads_State_Uploading           = Uploading
Uploads_State_Completed           = Completed
Uploads_State_CompletedWithErrors = Done with errors
Uploads_State_Failed              = Failed
Uploads_State_Paused              = Paused
Uploads_State_Cancelled           = Cancelled

# ETA fallback (UploadsViewModel)
Uploads_Eta_NotApplicable         = ~

# Reset-columns confirmation (different wording per tab)
Uploads_ResetColumns_Title        = Reset columns
Uploads_ResetColumns_Message      = Reset the Uploads tab columns to their defaults? This clears any custom show/hide and ordering you've set.

# Remove confirmation prompts (UploadsViewModel)
Uploads_Remove_Title              = Remove
Uploads_Remove_Package_Format     = Remove package '{0}' and its {1} file(s)?                            # {0} = package name, {1} = file count
Uploads_Remove_File_Format        = Remove '{0}' from the upload list?                                   # {0} = file name
Uploads_Remove_Generic            = Remove this item?
Uploads_Remove_PackagesOnly_Format     = Remove {0} package(s) ({1} file(s))?                            # {0} = package count, {1} = total file count
Uploads_Remove_FilesOnly_Format        = Remove {0} file(s) from the upload list?                       # {0} = file count
Uploads_Remove_PackagesAndFiles_Format = Remove {0} package(s) and {1} file(s) ({2} item(s) total)?      # {0} = packages, {1} = loose files, {2} = total

# Reset confirmation prompts (UploadsViewModel) — only shown when a completed file would
# be re-hashed and re-uploaded; resetting purely Failed/Cancelled files skips the prompt.
Uploads_Reset_Title                  = Reset
Uploads_Reset_Package_Format         = Reset package '{0}'? This will re-hash and re-upload {1} completed file(s) in this package.  # {0} = package name, {1} = completed file count
Uploads_Reset_File_Format            = Reset '{0}'? It already uploaded successfully — resetting will re-hash and re-upload it.  # {0} = file name
Uploads_ForceStart_Reupload_Title    = Re-upload
Uploads_ForceStart_Reupload_Format   = Re-upload {0} already-completed file(s)? They uploaded successfully before — force start will upload them again.  # {0} = completed file count
```

---

## Uploaded tab

```
Uploaded_Toolbar_ExportJson       = Export to JSON…
Uploaded_Toolbar_ExportJsonTip    = Export all completed uploads (with every field) to a JSON file

Uploaded_Col_Name                 = Name
Uploaded_Col_Path                 = Path
Uploaded_Col_Size                 = Size
Uploaded_Col_Hoster               = Hoster
Uploaded_Col_Account              = Account
Uploaded_Col_Finished             = Finished
Uploaded_Col_Started              = Started
Uploaded_Col_URL                  = URL
Uploaded_Col_Hash                 = Hash

Uploaded_Context_Copy             = Copy
Uploaded_Context_Copy_Gesture     = Ctrl+C
Uploaded_Context_CopyURL          = Copy URL
Uploaded_Context_Remove           = Remove
Uploaded_Context_Remove_Gesture   = Del
Uploaded_Context_ExportJson       = Export to JSON…

# Reset-columns confirmation
Uploaded_ResetColumns_Title       = Reset columns
Uploaded_ResetColumns_Message     = Reset the History tab columns to their defaults? This clears any custom show/hide and ordering you've set.

# Remove confirmation
Uploaded_Remove_Title             = Remove
Uploaded_Remove_Single_Format     = Remove '{0}' from history?                                          # {0} = file name
Uploaded_Remove_Many_Format       = Remove {0} entries from history?                                    # {0} = entry count
```

---

## Settings — General

```
Settings_Sidebar_General          = General
Settings_Sidebar_Upload           = Upload
Settings_Sidebar_Connection       = Connection
Settings_Sidebar_Accounts         = Accounts

Settings_General_Language_Title        = Language
Settings_General_Language_Desc         = UI language. The change applies immediately.
Settings_General_Language_Label        = Language

Settings_General_Developer_Title       = Developer
Settings_General_Developer_Desc        = Options for local development and testing.
Settings_General_UseMockServer         = Use mock server (redirect all file hoster requests to localhost:8080/<hoster>)   # review — hidden behind a developer flag, may stay English

Settings_General_GridAppearance_Title  = Grid Appearance
Settings_General_GridAppearance_Desc   = Font used for the Uploads and History tabs. Changes apply immediately.
Settings_General_GridFont              = Grid font
Settings_General_GridFontSize          = Grid font size

Settings_General_WindowBehaviour_Title = Window Behaviour
Settings_General_WindowBehaviour_Desc  = Choose what happens when the main window is minimized or closed.
Settings_General_MinimizeToTray        = Minimize the main window to the system tray instead of the taskbar
Settings_General_CloseAction           = Close button action

Settings_General_CloseAction_Ask         = Ask each time
Settings_General_CloseAction_MinToTray   = Minimize to tray
Settings_General_CloseAction_Exit        = Exit the application

Settings_General_Notifications_Title  = Notifications
Settings_General_Notifications_Desc   = Bottom-right popup that appears when an upload finishes.
Settings_General_ShowCompletionToasts = Show a popup notification when an upload finishes

Settings_General_ConfirmationPrompts_Title = Confirmation Prompts
Settings_General_ConfirmationPrompts_Desc  = Tick a prompt to have it ask again before the action. Untick to suppress the confirmation for that action.

Settings_General_Database_Title            = Database
Settings_General_Database_Desc             = CSUploader stores your upload history (packages, files, and URLs) in a local SQLite database. Removing entries from the Uploads or History tabs only hides them — the rows stay in the database. Click Clear to permanently delete the rows that are hidden from both tabs.
Settings_General_Database_BtnClear         = Clear
Settings_General_Database_ConfirmTitle     = Clear database
Settings_General_Database_ConfirmMessage   = Permanently delete the upload records hidden from both tabs?\n\nActive and visible uploads are not affected. This cannot be undone.
Settings_General_Database_Status_Cleared_Format    = Cleared {0} file row(s) and {1} package row(s) from the database.   # {0} = file count, {1} = package count
Settings_General_Database_Status_NothingToClear    = No hidden rows to clear.
Settings_General_Database_BtnClearLogs              = Clear logs
Settings_General_Database_ConfirmClearLogsTitle     = Clear log history
Settings_General_Database_ConfirmClearLogsMessage   = Permanently delete all log entries from the database?\n\nThe Logs tab will also be emptied. This cannot be undone.
Settings_General_Database_LogsCleared_Format        = Cleared {0} log entr(ies) from the database.   # {0} = count
Settings_General_Database_LogsNothingToClear        = No log entries to clear.
```

---

## Toast notifications

```
Toast_FileCompleted_Title    = Upload finished
Toast_FileCompleted_Body     = {0}
Toast_PackageCompleted_Title = Package finished
Toast_PackageCompleted_Body  = {0} of {1} files uploaded — {2}   # {0} = succeeded count, {1} = total count, {2} = package name
```

---

## Settings — Upload

```
Settings_Upload_Mgmt_Title         = Upload Management
Settings_Upload_Mgmt_Desc          = Connection limits, priorities, ...set up the Upload controller details.
Settings_Upload_MaxConcurrent      = Max. simultaneous Uploads
Settings_Upload_MaxPerHoster       = Max. simultaneous uploads per file hoster
Settings_Upload_RemoveFinished     = Remove finished uploads
Settings_Upload_IfFileExists       = If the file already exists
Settings_Upload_MaxCpuJobs         = Max. simultaneous CPU Jobs
Settings_Upload_SpeedLimit         = Speed Limit (KB/s)

Settings_Upload_RemoveFinished_Never              = Never
Settings_Upload_RemoveFinished_Immediately        = Immediately
Settings_Upload_RemoveFinished_AtStartup          = At startup
Settings_Upload_RemoveFinished_WhenPackageReady   = When package is ready

Settings_Upload_IfExists_Ask                      = Ask for each file
Settings_Upload_IfExists_Skip                     = Skip
Settings_Upload_IfExists_Overwrite                = Overwrite
Settings_Upload_IfExists_Rename                   = Rename

Settings_Upload_Autostart_Title                   = Autostart Uploads
Settings_Upload_Autostart_Desc                    = Choose if, and when CSUploader should start pending uploads without user interaction.
Settings_Upload_Autostart                         = Autostart uploads at application start

Settings_Upload_Autostart_Always                  = Always
Settings_Upload_Autostart_OnlyIfRunning           = Only if uploads were running at last session's end
Settings_Upload_Autostart_Never                   = Never
```

---

## Settings — Connection (proxy manager)

```
Settings_Conn_Title                = Connection Manager
Settings_Conn_Desc                 = If a proxy is required to access the internet, configure them here. Multiple proxies are rotated for new uploads. Default behaviour with no enabled proxies is direct connection.
Settings_Conn_UseProxies           = Use proxies
Settings_Conn_UseProxiesTip        = Master switch for the rotation. When off, all traffic (uploads and account checks) connects directly even with proxies in the grid — handy for adding and testing proxies without committing to using them yet.
Settings_Conn_AutoDisable          = Automatically uncheck failing proxies
Settings_Conn_AutoDisableTip       = When on, a proxy that fails a manual test or an upload is unchecked so the rotation skips it. The status icon updates either way.
Settings_Conn_AllowInvalidCert     = Accept invalid server certificates (not recommended)
Settings_Conn_AllowInvalidCertTip  = Skip TLS certificate validation on every outbound request. Required for some hosters whose storage CDN nodes ship certificates that fail standard validation (e.g. FileBoom's cmb-*.filestore.app edges). Disables protection against MITM attacks — only enable when uploads otherwise fail with an SSL error.

Settings_Conn_Col_On               = On
Settings_Conn_Col_Priority         = Priority
Settings_Conn_Col_Type             = Type
Settings_Conn_Col_Host             = Host / IP
Settings_Conn_Col_Port             = Port
Settings_Conn_Col_User             = User
Settings_Conn_Col_Password         = Password
Settings_Conn_Col_Test             = Test
Settings_Conn_Col_Status           = Status

Settings_Conn_PriorityUpTip        = Move up (higher priority)
Settings_Conn_PriorityDownTip      = Move down (lower priority)

Settings_Conn_Context_Test         = Test
Settings_Conn_Context_Remove       = Remove
Settings_Conn_Context_Remove_Gesture = Del

Settings_Conn_Btn_Import           = Import
Settings_Conn_Btn_Import_FromText  = Import from text…
Settings_Conn_Btn_Import_FromFile  = Import from file…
Settings_Conn_Btn_Export           = Export
Settings_Conn_Btn_Export_AllToText = Export all proxies to text…
Settings_Conn_Btn_Export_AllToFile = Export all proxies to file…
Settings_Conn_Btn_Export_OkToText  = Export tested-OK proxies to text…
Settings_Conn_Btn_Export_OkToFile  = Export tested-OK proxies to file…
Settings_Conn_Btn_Export_SelectedToText = Export selected proxies to text…
Settings_Conn_Btn_Export_SelectedToFile = Export selected proxies to file…
Settings_Conn_Btn_Save             = Save
Settings_Conn_Btn_Add              = Add
Settings_Conn_Btn_Remove           = Remove
Settings_Conn_Btn_RemoveSelected   = Remove selected
Settings_Conn_Btn_RemoveFailed     = Remove failed
Settings_Conn_Btn_TestAll          = Test All
Settings_Conn_Btn_TestAllTip       = Test connectivity for every proxy in the list
Settings_Conn_Btn_Details          = Details

# Proxy import/export dialogs (ProxyTextDialog ctor args, ConnectionManagerViewModel)
Settings_Conn_ImportProxies_FileDialogTitle = Import proxies
Settings_Conn_ImportProxies_FileFilter      = Proxy lists (*.txt)|*.txt|All files (*.*)|*.*
Settings_Conn_ImportProxies_DialogTitle     = Import Proxies
Settings_Conn_ImportProxies_DialogDesc      = Paste proxy lines (one per line). Format: scheme://[user:pass@]host[:port] — port defaults to 80/443/1080 by scheme.
Settings_Conn_ExportAll_DialogTitle         = Export All Proxies
Settings_Conn_ExportOk_DialogTitle          = Export Tested-OK Proxies
Settings_Conn_ExportAll_Desc_Format         = {0} proxy(s):                                  # {0} = count of all proxies
Settings_Conn_ExportOk_Desc_Format          = {0} proxy(s) with a successful last test:      # {0} = count of OK proxies
Settings_Conn_ExportSelected_DialogTitle    = Export Selected Proxies
Settings_Conn_ExportSelected_Desc_Format    = {0} selected proxy(s):                         # {0} = count of selected proxies

# Proxy remove confirmations (ConnectionManagerViewModel)
Settings_Conn_RemoveProxy_Title             = Remove proxy
Settings_Conn_RemoveProxy_One_Format        = Remove proxy '{0}:{1}'?                        # {0} = host, {1} = port
Settings_Conn_RemoveProxy_Many_Format       = Remove {0} proxies?                            # {0} = count
Settings_Conn_RemoveFailedProxy_Title       = Remove failed proxies
Settings_Conn_RemoveFailedProxy_One_Format  = Remove the failed proxy '{0}:{1}'?             # {0} = host, {1} = port
Settings_Conn_RemoveFailedProxy_Many_Format = Remove {0} failed proxies?                     # {0} = count

# Proxy test/save status strings (ConnectionManagerViewModel / ProxySettingItem)
Settings_Conn_Status_Queued                 = Queued…
Settings_Conn_Status_Testing                = Testing…
Settings_Conn_Status_OkLive                 = OK (live)
Settings_Conn_Status_OkLatencyIp_Format     = OK {0}ms ({1})                                 # {0} = ms, {1} = detected IP
Settings_Conn_Status_OkLatencyUnknown_Format = OK {0}ms (unexpected response)                # {0} = ms
Settings_Conn_Status_Failed_Format          = Failed: {0}                                    # {0} = error first line / message
Settings_Conn_Status_Saved                  = Saved
Settings_Conn_Status_SaveFailed_Format      = Save failed: {0}                               # {0} = error message
Settings_Conn_Status_Imported_Format        = Imported {0} proxy(s)                          # {0} = proxy count
Settings_Conn_Status_ExportedToFile_Format  = Exported {0} proxy(s) to {1}                   # {0} = count, {1} = file name
```

---

## Settings — Accounts

```
Settings_Accounts_Title            = Account Manager
Settings_Accounts_Desc             = Enter and manage all your Premium/Gold/Platinum accounts.

Settings_Accounts_Col_Enabled      = ✓                                                       # check-mark glyph header — leave as glyph or localize as e.g. "On"
Settings_Accounts_Col_Hoster       = Hoster
Settings_Accounts_Col_Status       = Status
Settings_Accounts_Col_Username     = Username
Settings_Accounts_Col_Password     = Password
Settings_Accounts_Col_Type         = Type
Settings_Accounts_Col_Used         = Used
Settings_Accounts_Col_Available    = Available
Settings_Accounts_Col_AddedAt      = Added at
Settings_Accounts_Col_RefreshedAt  = Refreshed at
Settings_Accounts_Storage_Unlimited = Unlimited

Settings_Accounts_Context_Edit     = Edit Account...
Settings_Accounts_Context_Refresh  = Check / Refresh
Settings_Accounts_Context_Enable   = Enable
Settings_Accounts_Context_Disable  = Disable
Settings_Accounts_Context_Delete   = Delete

Settings_Accounts_Btn_Add          = Add
Settings_Accounts_Btn_Remove       = Remove
Settings_Accounts_Btn_Refresh      = Refresh

# Account remove / validation
Settings_Accounts_Remove_Title             = Remove Account
Settings_Accounts_Remove_Message_Format    = Remove account '{0}' for {1}?                  # {0} = username, {1} = file hoster name
Settings_Accounts_Remove_MessageBulk_Format = Remove {0} selected accounts?                  # {0} = count

Settings_Accounts_Validation_FillHosterUser = Please fill in the file hoster, username, and password.
Settings_Accounts_Check_DialogTitle         = Account Check
Settings_Accounts_Check_FailedAddAnyway_Format = Account check failed: {0}\n\nAdd anyway?    # {0} = error message
Settings_Accounts_Check_CouldNotVerifyAddAnyway_Format = Could not verify account: {0}\n\nAdd anyway?   # {0} = error message

# CheckAccountStatus inline status messages
Settings_Accounts_Status_Verifying          = Verifying credentials...
Settings_Accounts_Status_Checking           = Checking account...
Settings_Accounts_Status_NoAccountsToRefresh = No accounts to refresh.
Settings_Accounts_Status_Verified_Format    = Verified: {0}                                  # {0} = result.Message
Settings_Accounts_Status_Warning_Format     = Warning: {0}                                   # {0} = result.Message
Settings_Accounts_Status_Valid_Format       = Valid: {0}                                     # {0} = result.Message
Settings_Accounts_Status_ValidExclaim_Format = Valid! {0}                                    # {0} = result.Message  (separate from above — different exclamation)
Settings_Accounts_Status_Failed_Format      = Failed: {0}                                    # {0} = result.Message
Settings_Accounts_Status_CheckError_Format  = Check error: {0}                               # {0} = exception.Message
Settings_Accounts_Status_Error_Format       = Error: {0}                                     # {0} = exception.Message
Settings_Accounts_Status_AccountAdded_Format = Account added for {0}!                        # {0} = file hoster name
Settings_Accounts_Status_NoImpl_Format      = No implementation for {0}. Cannot check.       # {0} = file hoster name
Settings_Accounts_Status_NoImplWillSave_Format = No implementation for {0}. Account will be saved without verification.   # {0} = file hoster name
Settings_Accounts_Status_CheckingProgress_Format = Checking {0}@{1}... ({2}/{3})             # {0} = username, {1} = hoster, {2} = current, {3} = total
Settings_Accounts_Status_CheckingShort      = Checking...
Settings_Accounts_Status_NoImpl             = No implementation
Settings_Accounts_Status_RefreshSummary_Format = Refreshed {0} accounts. {1} updated.        # {0} = checked count, {1} = updated count
Settings_Accounts_Status_AccountDisabled_Format = Account '{0}' disabled.                    # {0} = username
Settings_Accounts_Status_AccountEnabled_Format  = Account '{0}' enabled.                     # {0} = username
Settings_Accounts_Status_AccountsBulkDisabled_Format = {0} accounts disabled.                 # {0} = count
Settings_Accounts_Status_AccountsBulkEnabled_Format  = {0} accounts enabled.                  # {0} = count

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
Logs_Col_Url                       = URL
Logs_Col_Proxy                     = Proxy
Logs_Col_Method                    = Method

# Column show/hide "Reset columns" confirmation (Logs tab grids).
Logs_ResetColumns_Title            = Reset columns
Logs_ResetColumns_Message          = Reset the Logs columns to their defaults? This clears any custom show/hide and ordering you've set.

# Status messages logged to the Status tab (UploadedViewModel) — these surface to the
# user via the Logs tab so they're worth localising.
Logs_Status_NoUrlsClipboardCleared = No URLs in selection; clipboard cleared
Logs_Status_CopiedUrls_Format      = Copied {0} URL(s) to clipboard                          # {0} = url count
Logs_Status_HiddenFiles_Format     = Hid {0} file(s) from the History tab                    # {0} = file count
Logs_Status_ExportedPackages_Format = Exported {0} package(s) to {1}                         # {0} = pkg count, {1} = file path
```

---

## Upload Wizard

```
Wizard_Title                       = Upload Wizard

Wizard_Step_DirectorySource        = 1. Directory
Wizard_Step_FileHosters            = 2. File Hosters
Wizard_Step_Summary                = 3. Summary
Wizard_Step_Start                  = 4. Start
Wizard_Summary_Title               = Upload Summary
Wizard_Summary_Desc                = Review what will be uploaded to each hoster. Hosters with no eligible files are omitted.
Wizard_Summary_FileCount_Suffix    = files
Wizard_Summary_OrphanWarning_Suffix = files won't be uploaded to any hoster:
Wizard_Summary_MaxFileSize_Format  = max {0} per file                          # {0} = formatted byte unit (e.g. "250 MiB")
Wizard_Step_FilesSource            = 1. Files

Wizard_Step0_Mode_Directory        = Upload directory
Wizard_Step0_Mode_Files            = Upload files

Wizard_Step0_Title                 = Select Upload Directory
Wizard_Step0_Desc                  = Choose the directory containing the files you want to upload.
Wizard_Step0_Browse                = Browse
Wizard_Step0_BrowseDialogTitle     = Select Upload Directory                                 # used when calling BrowseFolder

Wizard_Step1_Title                 = Select Files
Wizard_Step1_PackageTitleLabel     = Package title:
Wizard_Step1_FilterLabel           = Filter:
Wizard_Step1_BtnSelectAll          = Select all
Wizard_Step1_BtnDeselectAll        = Deselect all
Wizard_Step1_BtnRemove             = Remove
Wizard_Step1_Col_File              = File
Wizard_Step1_Col_Size              = Size
Wizard_Step1_SelectedLabel         = Selected:
Wizard_Step1_FilesUnit             = file(s)

Wizard_Step2_Title                 = Select File Hosters
Wizard_Step2_Desc                  = Choose which file hosters to upload to and select accounts.
Wizard_Step2_Col_Use               = Use
Wizard_Step2_Col_FileHoster        = File Hoster
Wizard_Step2_Col_Account           = Account
Wizard_Step2_AccountAnonymous      = (anonymous)
Wizard_Step2_AccountSelect         = (select account)
Wizard_Step2_AddAccountLink        = Add account…
Wizard_Step2_AccountRequiredTooltip = This hoster requires an account. Click "Add account…" to add one.
Wizard_Hoster_LimitsHeader         = This hoster's limits would be exceeded:
Wizard_Hoster_FileTooLarge_Format  = {0}: The following files exceed the per-file limit of {1} and won't be uploaded:\n{2}
Wizard_Hoster_TooManyFiles_Format  = {0}: {1} files selected, but the per-package limit is {2}.

Wizard_Step3_Title                 = When to Start
Wizard_Step3_Desc                  = Choose when the upload should begin.
Wizard_Step3_Mode_Immediately      = Start immediately after closing the wizard
Wizard_Step3_Mode_Later            = Add to queue but start later (manual start)
Wizard_Step3_Mode_Scheduled        = Schedule for a specific date and time
Wizard_Step3_TimeFormatHint        = (HH:mm)

Wizard_Btn_Back                    = Back
Wizard_Btn_Cancel                  = Cancel
Wizard_Btn_Next                    = Next
Wizard_Btn_Add                     = Add

# Validation errors (UploadWizardViewModel.ShowError)
Wizard_Validation_PickValidDir     = Please select a valid directory.
Wizard_Validation_PickFile         = Please select at least one file.
Wizard_Validation_PickHoster       = Please select at least one file hoster.
Wizard_Error_Format                = Error: {0}                                              # {0} = exception.Message

Wizard_Step0_Files_Title           = Select files
Wizard_Step0_Files_Desc            = Pick the files you want to upload. You can add more later.
Wizard_Step0_Files_Pick            = Add files…
Wizard_Step0_Files_BrowseDialogTitle = Pick files to upload                                  # used when calling BrowseFiles

Wizard_Step1_DuplicateFilenameSuffixFormat = {0} (in {1})                                    # {0} = filename, {1} = parent folder name

Wizard_Validation_PickAtLeastOneFile = Pick at least one file before continuing.
Wizard_Validation_TitleRequired    = Enter a package title.
```

---

## Confirmation Prompts (Settings → General list labels)

These are the user-visible labels for `ConfirmationKeys.All` — the strings shown in the
"Confirmation Prompts" section of Settings. Stable IDs (`remove-upload-package-or-file`
etc.) stay English.

```
Confirm_RemoveUploadPackageOrFile  = Remove package or file from Uploads tab
Confirm_RemoveUploadedEntry        = Remove entries from the History tab
Confirm_RemoveFileHosterAccount    = Remove a file hoster account
Confirm_RemoveProxy                = Remove a proxy from Connection Manager
Confirm_ResetCompletedUpload       = Reset a completed upload (re-hash and re-upload)
Confirm_ResetColumns               = Reset columns to their defaults on the Uploads / History tab
```

---

## Dialog windows

### About

```
About_WindowTitle                  = About CSUploader
About_AppName                      = CSUploader
About_Version_Format               = Version {0}                                             # {0} = assembly version, e.g. "1.2.3"
About_Description                  = A powerful file upload manager for multiple hosting services. Features include hashing, queue management, and real-time progress tracking.
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
CloseAction_WindowTitle            = Close CSUploader
CloseAction_Heading                = What would you like the close button to do?
CloseAction_Subheading             = Pick one — you can change this later under Settings → General.
CloseAction_Remember               = Remember my choice
CloseAction_BtnMinimize            = Minimize to tray
CloseAction_BtnExit                = Exit
CloseAction_BtnCancel              = Cancel
```

### Confirmation dialog

```
Confirmation_WindowTitle           = Confirm
Confirmation_DontAskAgain          = Don't ask me again for this action
Confirmation_BtnYes                = Yes
Confirmation_BtnNo                 = No
```

### EditAccount dialog

```
EditAccount_WindowTitle            = Edit Account
EditAccount_AddTitle               = Add Account                                             # used by SettingsViewModel.AddAccountDialog
EditAccount_FileHosterLabel        = File Hoster:
EditAccount_UsernameLabel          = Username:
EditAccount_PasswordLabel          = Password:
EditAccount_AccountEnabled         = Account enabled
EditAccount_BtnSave                = Save
EditAccount_BtnCancel              = Cancel
EditAccount_Validation_RequireUsernameAndPassword = Please enter both a username and a password.
EditAccount_OrLabel                = — or —                                                  # separator between Sign-in and manual API-key entry (XFileSharing hosters)
EditAccount_ApiKeyLabel            = API Key:
EditAccount_SignInLabel            = Account:
EditAccount_SignInButton           = Sign in…                                                # opens the captcha WebView for XFileSharing-API hosters
EditAccount_SignIn_InProgress      = Opening sign-in…
EditAccount_SignIn_Success         = ✓ Signed in
EditAccount_SignIn_SuccessAs_Format = ✓ Signed in as {0}
EditAccount_SignIn_Failed_Format   = ✗ {0}
EditAccount_SignIn_FailedGeneric   = Sign-in failed.
EditAccount_SignIn_Unavailable     = Sign-in is unavailable in this context.
EditAccount_Validation_RequireLoginOrApiKey = Click Sign in to authenticate, or paste an API key.

EditProxy_AddTitle                 = Add Proxy
EditProxy_EditTitle                = Edit Proxy
EditProxy_EnabledLabel             = Proxy enabled
EditProxy_BtnSave                  = Save
EditProxy_BtnCancel                = Cancel
EditProxy_BtnTest                  = Test
EditProxy_Validation_HostRequired  = Please enter a host or IP address.
EditProxy_Validation_PortInvalid   = Please enter a valid port between 1 and 65535.
EditProxy_Status_Testing           = Testing…
EditProxy_Status_OkLatency_Format  = OK {0}ms (unexpected response)
EditProxy_Status_OkLatencyIp_Format = OK {0}ms ({1})
EditProxy_Status_Failed_Format     = Failed: {0}

# WebView sign-in window (WebViewLoginWindow) — the captcha sign-in for XFileSharing-API
# hosters (Ex-Load, KatFile, FlashBit, TakeFile). {0} placeholders as noted.
WebViewLogin_WindowTitle                  = Sign in
WebViewLogin_Header_Format                = Sign in to {0}                                  # {0} = hoster name
WebViewLogin_Instructions                 = Complete the sign-in (including any captcha) in the browser below. This window will close automatically once your session is captured.
WebViewLogin_Status_Initializing          = Initializing browser...
WebViewLogin_Status_Loading_Format        = Loading {0}...                                  # {0} = URL
WebViewLogin_Status_CookieReadFailed_Format = Cookie read failed: {0}                       # {0} = error detail
WebViewLogin_Error_InitFailed_Format      = Could not initialize the WebView2 browser. The WebView2 runtime may not be installed. Details: {0}
WebViewLogin_Error_UnsupportedProxy_Title = Proxy not supported for sign-in
WebViewLogin_Error_SocksAuthUnsupported_Format = The sign-in browser cannot use a SOCKS proxy that requires a username and password ({0}). Use a SOCKS proxy without credentials, or use an HTTP/HTTPS proxy instead.
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
HttpDetails_Timing_Format          = Started: {0}  |  Duration: {1}ms  |  Size: {2} bytes    # {0} = HH:mm:ss.fff, {1} = ms, {2} = byte count
HttpDetails_Proxy_Format           = Proxy: {0}                                              # {0} = proxy display string
HttpDetails_NoData                 = (no data)
HttpDetails_NoBody                 = (no body)
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
LogDetails_Btn_Close               = Close
```

### Progress / UpdateProgress windows

```
Progress_WindowTitle               = Please wait...
Progress_DefaultLabel              = Loading...
Progress_LabelSuffix               = Please wait...                                          # appended to the caller-supplied label on a new line
Progress_BtnCancel                 = Cancel
Progress_BtnCancelling             = Cancelling...

UpdateProgress_WindowTitle         = Updating CSUploader
UpdateProgress_StatusInitial       = Preparing…
UpdateProgress_StatusDownloading_Format = Downloading update v{0}…                           # {0} = available semver
UpdateProgress_StatusRestarting    = Restarting…
UpdateProgress_StatusFailed_Format = Update failed: {0}                                      # {0} = exception.Message
UpdateProgress_PercentInitial      = 0%
```

### ProxyText dialog

```
ProxyText_WindowTitle              = Proxies                                                 # XAML default — overridden via ctor with Import/Export titles
ProxyText_BtnImport                = Import
ProxyText_BtnCopy                  = Copy
ProxyText_BtnCancel                = Cancel
ProxyText_BtnClose                 = Close                                                   # replaces Cancel in read-only export mode
```

### SpeedLimit dialog

```
SpeedLimit_WindowTitle             = Speed Limit
SpeedLimit_Heading                 = Set Speed Limit
SpeedLimit_Subheading              = Per-package override. Leave empty to use the global setting.
SpeedLimit_Unit                    = KB/s
SpeedLimit_BtnClear                = Clear
SpeedLimit_BtnCancel               = Cancel
SpeedLimit_BtnOk                   = OK
SpeedLimit_Validation_Title        = Invalid value
SpeedLimit_Validation_Message      = Please enter a positive integer (KB/s), or leave empty to clear.
```

---

## Status / inline messages

These are short status strings shown in non-dialog inline UI. Several have already been
listed in their owning section above; this section catches the rest, plus dialog-service
default titles.

```
Dialog_DefaultErrorTitle           = Error
Dialog_DefaultConfirmTitle         = Confirm
Dialog_DefaultBrowseFolderTitle    = Select Folder
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
Tray_Menu_Show                     = Show CSUploader                                         # "Show " + brand; localise the verb only
Tray_Menu_Exit                     = Exit
Tray_Balloon_Title                 = CSUploader                                              # brand — do not translate
Tray_Balloon_Body                  = Still running in the tray. Click the icon to restore the window, or right-click for Exit.
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

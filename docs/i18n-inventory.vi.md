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

<!-- Translation notes (vi):
     - "file hoster" is rendered as "nhà lưu trữ tập tin" (or "nhà lưu trữ" where the
       column/label needs to stay short). Used consistently throughout.
     - "package" → "gói"; "upload" → "tải lên"; "proxy" → "proxy" (loanword).
     - Menu mnemonics use the Vietnamese-localized "Tệp(_F)" form, mirroring zh-Hans/ja.
     - Loanwords kept as-is: URL, ETA, JSON, HTTP, Hex, proxy, IP, port.
-->

---

## Common

```
Common_OK                         = OK
Common_Cancel                     = Hủy
Common_Yes                        = Có
Common_No                         = Không
Common_Save                       = Lưu
Common_Add                        = Thêm
Common_Remove                     = Xóa
Common_Close                      = Đóng
Common_Edit                       = Sửa
Common_Delete                     = Xóa
Common_Browse                     = Duyệt
Common_Back                       = Quay lại
Common_Next                       = Tiếp theo
Common_Apply                      = Áp dụng
Common_Refresh                    = Làm mới
Common_Details                    = Chi tiết
Common_Copy                       = Sao chép
Common_Import                     = Nhập
Common_Export                     = Xuất
Common_All                        = Tất cả
Common_None                       = Không có
Common_Test                       = Kiểm tra
Common_Confirm                    = Xác nhận
Common_Error                      = Lỗi
Common_Warning                    = Cảnh báo
Common_SelectFolder               = Chọn thư mục
Common_SelectFiles                 = Chọn tệp
Common_PleaseWait                 = Vui lòng chờ...
Common_Loading                    = Đang tải...
Common_Cancelling                 = Đang hủy...
Common_Preparing                  = Đang chuẩn bị…
Common_Unknown                    = không xác định
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
Main_Title_UpdateAvailable_Format = CSUploader — Có bản cập nhật (v{0}) — nhấn Trợ giúp → Cài đặt bản cập nhật     # {0} = available semver
Main_Menu_File                    = Tệp(_F)
Main_Menu_File_Exit               = Thoát(_E)
Main_Menu_File_Exit_Gesture       = Alt+F4
Main_Menu_View                    = Xem(_V)
Main_Menu_View_UploadOverview     = Tổng quan tải lên(_O)
Main_Menu_View_DarkMode           = Chế độ tối(_D)
Main_Menu_View_LightMode          = Chế độ sáng(_L)
Main_Menu_Help                    = Trợ giúp(_H)
Main_Menu_Help_CheckForUpdates    = Kiểm tra cập nhật(_C)…
Main_Menu_Help_InstallUpdate      = Cài đặt bản cập nhật(_I)
Main_Menu_Help_About              = Giới thiệu CSUploader(_A)

Main_Tab_Uploads                  = Đang tải lên
Main_Tab_Uploaded                 = Lịch sử
Main_Tab_Settings                 = Cài đặt
Main_Tab_Logs                     = Nhật ký

Main_CheckForUpdates_DialogTitle  = Kiểm tra cập nhật
Main_CheckForUpdates_AlreadyLatest = Bạn đang dùng phiên bản mới nhất.
Main_CheckForUpdates_Available_Format = Có bản cập nhật: v{0}.\n\nVui lòng dùng Trợ giúp → Cài đặt bản cập nhật để tải xuống và cài đặt.   # {0} = available semver
```

---

## Uploads tab

Toolbar, context menu, columns, overview panel, filter, and footer link.

```
Uploads_Toolbar_AddTip            = Thêm lượt tải lên mới
Uploads_Toolbar_StartTip          = Bắt đầu tất cả các lượt tải lên
Uploads_Toolbar_PauseTip          = Tạm dừng / Tiếp tục
Uploads_Toolbar_StopTip           = Dừng tất cả các lượt tải lên
Uploads_Toolbar_MoveUpTip         = Di chuyển gói lên
Uploads_Toolbar_MoveDownTip       = Di chuyển gói xuống
Uploads_Toolbar_RemoveTip         = Xóa mục đã chọn

Uploads_Context_Start             = Bắt đầu
Uploads_Context_Stop              = Dừng
Uploads_Context_SkipUpload        = Bỏ qua tải lên
Uploads_Context_Reset             = Đặt lại
Uploads_Context_OpenSourceDir     = Mở thư mục nguồn
Uploads_Context_SetSpeedLimit     = Đặt giới hạn tốc độ...
Uploads_Context_Remove            = Xóa

Uploads_Col_Name                  = Tên
Uploads_Col_Size                  = Kích thước
Uploads_Col_Hoster                = Nhà lưu trữ
Uploads_Col_Status                = Trạng thái
Uploads_Col_Speed                 = Tốc độ
Uploads_Col_ETA                   = ETA
Uploads_Col_BytesLoaded           = Byte đã tải
Uploads_Col_Progress              = Tiến độ
Uploads_Col_SaveTo                = Lưu vào
Uploads_Col_Added                 = Đã thêm
Uploads_Col_Finished              = Đã hoàn tất
Uploads_Col_Duration              = Thời lượng
Uploads_Col_Priority              = Ưu tiên
Uploads_Col_SpeedLimit            = Giới hạn tốc độ
Uploads_Col_URL                   = URL
Uploads_Col_Hash                  = Hash
Uploads_Col_Error                 = Lỗi

Uploads_ColumnHeader_LockTip      = Khóa độ rộng cột
Uploads_ColumnMenu_Reset          = Đặt lại các cột
Uploads_ColumnMenu_DefaultLabel   = Cột                                              # fallback when a column has no header text

Uploads_Overview_Title            = Tổng quan tải lên
Uploads_Overview_CloseTip         = Đóng tổng quan
Uploads_Overview_ToggleTip        = Hiện / ẩn thống kê tổng quan

Uploads_Overview_Packages         = Gói
Uploads_Overview_Links            = Liên kết
Uploads_Overview_TotalBytes       = Tổng số byte
Uploads_Overview_Uploadspeed      = Tốc độ tải lên
Uploads_Overview_BytesLoaded      = Byte đã tải
Uploads_Overview_RemainingBytes   = Byte còn lại
Uploads_Overview_Eta              = ETA
Uploads_Overview_RunningUploads   = Lượt tải lên đang chạy
Uploads_Overview_OpenConnections  = Kết nối đang mở
Uploads_Overview_FinishedLinks    = Liên kết đã hoàn tất
Uploads_Overview_SkippedLinks     = Liên kết đã bỏ qua
Uploads_Overview_FailedLinks      = Liên kết thất bại

# Same labels appear inline with a trailing colon. Splitting them so a translator can
# control whether the colon is part of the term (typographic spacing differs in CJK).
Uploads_Overview_PackagesLabel        = Gói:
Uploads_Overview_LinksLabel           = Liên kết:
Uploads_Overview_TotalBytesLabel      = Tổng số byte:
Uploads_Overview_UploadspeedLabel     = Tốc độ tải lên:
Uploads_Overview_BytesLoadedLabel     = Byte đã tải:
Uploads_Overview_RemainingBytesLabel  = Byte còn lại:
Uploads_Overview_EtaLabel             = ETA:
Uploads_Overview_RunningUploadsLabel  = Lượt tải lên đang chạy:
Uploads_Overview_OpenConnectionsLabel = Kết nối đang mở:
Uploads_Overview_FinishedLinksLabel   = Liên kết đã hoàn tất:
Uploads_Overview_SkippedLinksLabel    = Liên kết đã bỏ qua:
Uploads_Overview_FailedLinksLabel     = Liên kết thất bại:

Uploads_FilterLabel               = Tên tệp
Uploads_FilterTip                 = Lọc theo tên tệp hoặc tên gói
Uploads_FooterPremiumLink         = Thêm tài khoản Premium…

# Item-state names rendered in the Status column (FileStateDisplayConverter).
Uploads_State_Idle                = Nhàn rỗi
Uploads_State_HashQueued          = Đang chờ băm
Uploads_State_Hashing             = Đang băm
Uploads_State_UploadQueued        = Đang chờ tải lên
Uploads_State_Uploading           = Đang tải lên
Uploads_State_Completed           = Đã hoàn tất
Uploads_State_Failed              = Thất bại
Uploads_State_Paused              = Đã tạm dừng
Uploads_State_Cancelled           = Đã hủy

# ETA fallback (UploadsViewModel)
Uploads_Eta_NotApplicable         = ~

# Reset-columns confirmation (different wording per tab)
Uploads_ResetColumns_Title        = Đặt lại các cột
Uploads_ResetColumns_Message      = Đặt lại các cột của thẻ Đang tải lên về mặc định? Thao tác này sẽ xóa mọi thiết lập hiện/ẩn và sắp xếp tùy chỉnh của bạn.

# Remove confirmation prompts (UploadsViewModel)
Uploads_Remove_Title              = Xóa
Uploads_Remove_Package_Format     = Xóa gói '{0}' và {1} tệp của nó?                            # {0} = package name, {1} = file count
Uploads_Remove_File_Format        = Xóa '{0}' khỏi danh sách tải lên?                                   # {0} = file name
Uploads_Remove_Generic            = Xóa mục này?
Uploads_Remove_PackagesOnly_Format     = Xóa {0} gói ({1} tệp)?                            # {0} = package count, {1} = total file count
Uploads_Remove_FilesOnly_Format        = Xóa {0} tệp khỏi danh sách tải lên?                       # {0} = file count
Uploads_Remove_PackagesAndFiles_Format = Xóa {0} gói và {1} tệp (tổng {2} mục)?      # {0} = packages, {1} = loose files, {2} = total
```

---

## Uploaded tab

```
Uploaded_Toolbar_ExportJson       = Xuất ra JSON…
Uploaded_Toolbar_ExportJsonTip    = Xuất tất cả các lượt tải lên đã hoàn tất (kèm mọi trường) ra tệp JSON

Uploaded_Col_Name                 = Tên
Uploaded_Col_Path                 = Đường dẫn
Uploaded_Col_Size                 = Kích thước
Uploaded_Col_Hoster               = Nhà lưu trữ
Uploaded_Col_Finished             = Đã hoàn tất
Uploaded_Col_URL                  = URL
Uploaded_Col_Hash                 = Hash

Uploaded_Context_Copy             = Sao chép
Uploaded_Context_Copy_Gesture     = Ctrl+C
Uploaded_Context_CopyURL          = Sao chép URL
Uploaded_Context_Remove           = Xóa
Uploaded_Context_Remove_Gesture   = Del
Uploaded_Context_ExportJson       = Xuất ra JSON…

# Reset-columns confirmation
Uploaded_ResetColumns_Title       = Đặt lại các cột
Uploaded_ResetColumns_Message     = Đặt lại các cột của thẻ Lịch sử về mặc định? Thao tác này sẽ xóa mọi thiết lập hiện/ẩn và sắp xếp tùy chỉnh của bạn.

# Remove confirmation
Uploaded_Remove_Title             = Xóa
Uploaded_Remove_Single_Format     = Xóa '{0}' khỏi lịch sử?                                          # {0} = file name
Uploaded_Remove_Many_Format       = Xóa {0} mục khỏi lịch sử?                                    # {0} = entry count
```

---

## Settings — General

```
Settings_Sidebar_General          = Chung
Settings_Sidebar_Upload           = Tải lên
Settings_Sidebar_Connection       = Kết nối
Settings_Sidebar_Accounts         = Tài khoản

Settings_General_Language_Title        = Ngôn ngữ
Settings_General_Language_Desc         = Ngôn ngữ giao diện. Thay đổi sẽ áp dụng ngay lập tức.
Settings_General_Language_Label        = Ngôn ngữ

Settings_General_Developer_Title       = Nhà phát triển
Settings_General_Developer_Desc        = Tùy chọn dành cho phát triển và kiểm thử cục bộ.
Settings_General_UseMockServer         = Dùng máy chủ giả (chuyển hướng mọi yêu cầu nhà lưu trữ tập tin đến localhost:8080/<hoster>)   # review — hidden behind a developer flag, may stay English

Settings_General_GridAppearance_Title  = Giao diện bảng
Settings_General_GridAppearance_Desc   = Phông chữ dùng cho thẻ Đang tải lên và Lịch sử. Thay đổi áp dụng ngay lập tức.
Settings_General_GridFont              = Phông chữ bảng
Settings_General_GridFontSize          = Cỡ chữ bảng

Settings_General_WindowBehaviour_Title = Hành vi cửa sổ
Settings_General_WindowBehaviour_Desc  = Chọn điều xảy ra khi cửa sổ chính được thu nhỏ hoặc đóng.
Settings_General_MinimizeToTray        = Thu nhỏ cửa sổ chính vào khay hệ thống thay vì thanh tác vụ
Settings_General_CloseAction           = Hành động của nút đóng

Settings_General_CloseAction_Ask         = Hỏi mỗi lần
Settings_General_CloseAction_MinToTray   = Thu nhỏ vào khay
Settings_General_CloseAction_Exit        = Thoát ứng dụng

Settings_General_Notifications_Title  = Thông báo
Settings_General_Notifications_Desc   = Cửa sổ bật lên ở góc dưới bên phải khi quá trình tải lên hoàn tất.
Settings_General_ShowCompletionToasts = Hiển thị thông báo bật lên khi tải lên hoàn tất

Settings_General_ConfirmationPrompts_Title = Hộp thoại xác nhận
Settings_General_ConfirmationPrompts_Desc  = Đánh dấu một mục để được hỏi lại trước khi thực hiện hành động. Bỏ đánh dấu để tắt xác nhận cho hành động đó.

Settings_General_Database_Title            = Cơ sở dữ liệu
Settings_General_Database_Desc             = CSUploader lưu lịch sử tải lên (gói, tệp và URL) trong một cơ sở dữ liệu SQLite cục bộ. Khi xóa các mục khỏi tab "Tải lên" hoặc "Lịch sử", chúng chỉ bị ẩn — các dòng vẫn còn trong cơ sở dữ liệu. Nhấp vào "Xóa" để xóa vĩnh viễn các dòng đã bị ẩn khỏi cả hai tab.
Settings_General_Database_BtnClear         = Xóa
Settings_General_Database_ConfirmTitle     = Xóa cơ sở dữ liệu
Settings_General_Database_ConfirmMessage   = Xóa vĩnh viễn các bản ghi tải lên đã bị ẩn khỏi cả hai tab?\n\nCác lượt tải lên đang hoạt động và hiển thị sẽ không bị ảnh hưởng. Hành động này không thể hoàn tác.
Settings_General_Database_Status_Cleared_Format    = Đã xóa {0} dòng tệp và {1} dòng gói khỏi cơ sở dữ liệu.
Settings_General_Database_Status_NothingToClear    = Không có dòng ẩn nào để xóa.
Settings_General_Database_BtnClearLogs              = Clear logs
Settings_General_Database_ConfirmClearLogsTitle     = Clear log history
Settings_General_Database_ConfirmClearLogsMessage   = Permanently delete all log entries from the database?\n\nThis affects only the persisted history loaded on startup. The Logs tab keeps showing this session's entries until you close the app. This cannot be undone.
Settings_General_Database_LogsCleared_Format        = Cleared {0} log entr(ies) from the database.
Settings_General_Database_LogsNothingToClear        = No log entries to clear.
```

---

## Toast notifications

```
Toast_FileCompleted_Title    = Tải lên hoàn tất
Toast_FileCompleted_Body     = {0}
Toast_PackageCompleted_Title = Gói tải lên hoàn tất
Toast_PackageCompleted_Body  = Đã tải lên {0} trên {1} tệp — {2}
```

---

## Settings — Upload

```
Settings_Upload_Mgmt_Title         = Quản lý tải lên
Settings_Upload_Mgmt_Desc          = Giới hạn kết nối, ưu tiên, ...thiết lập chi tiết bộ điều khiển tải lên.
Settings_Upload_MaxConcurrent      = Số lượt tải lên đồng thời tối đa
Settings_Upload_MaxPerHoster       = Số lượt tải lên đồng thời tối đa cho mỗi nhà lưu trữ tập tin
Settings_Upload_RemoveFinished     = Xóa các lượt tải lên đã hoàn tất
Settings_Upload_IfFileExists       = Nếu tệp đã tồn tại
Settings_Upload_MaxCpuJobs         = Số tác vụ CPU đồng thời tối đa
Settings_Upload_SpeedLimit         = Giới hạn tốc độ (KB/s)

Settings_Upload_RemoveFinished_Never              = Không bao giờ
Settings_Upload_RemoveFinished_Immediately        = Ngay lập tức
Settings_Upload_RemoveFinished_AtStartup          = Khi khởi động
Settings_Upload_RemoveFinished_WhenPackageReady   = Khi gói sẵn sàng

Settings_Upload_IfExists_Ask                      = Hỏi cho từng tệp
Settings_Upload_IfExists_Skip                     = Bỏ qua
Settings_Upload_IfExists_Overwrite                = Ghi đè
Settings_Upload_IfExists_Rename                   = Đổi tên

Settings_Upload_Autostart_Title                   = Tự động bắt đầu tải lên
Settings_Upload_Autostart_Desc                    = Chọn xem CSUploader có và khi nào nên bắt đầu các lượt tải lên đang chờ mà không cần tương tác từ người dùng.
Settings_Upload_Autostart                         = Tự động bắt đầu tải lên khi khởi động ứng dụng

Settings_Upload_Autostart_Always                  = Luôn luôn
Settings_Upload_Autostart_OnlyIfRunning           = Chỉ khi có lượt tải lên đang chạy lúc kết thúc phiên trước
Settings_Upload_Autostart_Never                   = Không bao giờ
```

---

## Settings — Connection (proxy manager)

```
Settings_Conn_Title                = Trình quản lý kết nối
Settings_Conn_Desc                 = Nếu cần dùng proxy để truy cập internet, hãy cấu hình tại đây. Nhiều proxy sẽ được luân phiên cho các lượt tải lên mới. Hành vi mặc định khi không có proxy nào được bật là kết nối trực tiếp.
Settings_Conn_UseProxies           = Dùng proxy cho các lượt tải lên
Settings_Conn_UseProxiesTip        = Công tắc tổng cho việc luân phiên. Khi tắt, các lượt tải lên sẽ kết nối trực tiếp ngay cả khi có proxy trong bảng — tiện cho việc thêm và kiểm tra proxy mà chưa cam kết sử dụng.
Settings_Conn_AutoDisable          = Tự động bỏ chọn các proxy bị lỗi
Settings_Conn_AutoDisableTip       = Khi bật, một proxy thất bại trong kiểm tra thủ công hoặc khi tải lên sẽ bị bỏ chọn để vòng luân phiên bỏ qua nó. Biểu tượng trạng thái sẽ luôn cập nhật dù bật hay tắt.

Settings_Conn_Col_On               = Bật
Settings_Conn_Col_Priority         = Ưu tiên
Settings_Conn_Col_Type             = Loại
Settings_Conn_Col_Host             = Máy chủ / IP
Settings_Conn_Col_Port             = Cổng
Settings_Conn_Col_User             = Người dùng
Settings_Conn_Col_Password         = Mật khẩu
Settings_Conn_Col_Test             = Kiểm tra
Settings_Conn_Col_Status           = Trạng thái

Settings_Conn_PriorityUpTip        = Di chuyển lên (ưu tiên cao hơn)
Settings_Conn_PriorityDownTip      = Di chuyển xuống (ưu tiên thấp hơn)

Settings_Conn_Context_Test         = Kiểm tra
Settings_Conn_Context_Remove       = Xóa
Settings_Conn_Context_Remove_Gesture = Del

Settings_Conn_Btn_Import           = Nhập
Settings_Conn_Btn_Import_FromText  = Nhập từ văn bản…
Settings_Conn_Btn_Import_FromFile  = Nhập từ tệp…
Settings_Conn_Btn_Export           = Xuất
Settings_Conn_Btn_Export_AllToText = Xuất tất cả proxy ra văn bản…
Settings_Conn_Btn_Export_AllToFile = Xuất tất cả proxy ra tệp…
Settings_Conn_Btn_Export_OkToText  = Xuất các proxy đã kiểm tra OK ra văn bản…
Settings_Conn_Btn_Export_OkToFile  = Xuất các proxy đã kiểm tra OK ra tệp…
Settings_Conn_Btn_Save             = Lưu
Settings_Conn_Btn_Add              = Thêm
Settings_Conn_Btn_Remove           = Xóa
Settings_Conn_Btn_RemoveSelected   = Xóa mục đã chọn
Settings_Conn_Btn_RemoveFailed     = Xóa các mục thất bại
Settings_Conn_Btn_TestAll          = Kiểm tra tất cả
Settings_Conn_Btn_TestAllTip       = Kiểm tra kết nối cho mọi proxy trong danh sách
Settings_Conn_Btn_Details          = Chi tiết

# Proxy import/export dialogs (ProxyTextDialog ctor args, ConnectionManagerViewModel)
Settings_Conn_ImportProxies_FileDialogTitle = Nhập proxy
Settings_Conn_ImportProxies_FileFilter      = Danh sách proxy (*.txt)|*.txt|Tất cả tệp (*.*)|*.*
Settings_Conn_ImportProxies_DialogTitle     = Nhập proxy
Settings_Conn_ImportProxies_DialogDesc      = Dán các dòng proxy (mỗi dòng một proxy). Định dạng: scheme://[user:pass@]host[:port] — cổng mặc định theo scheme là 80/443/1080.
Settings_Conn_ExportAll_DialogTitle         = Xuất tất cả proxy
Settings_Conn_ExportOk_DialogTitle          = Xuất các proxy đã kiểm tra OK
Settings_Conn_ExportAll_Desc_Format         = {0} proxy:                                  # {0} = count of all proxies
Settings_Conn_ExportOk_Desc_Format          = {0} proxy có lần kiểm tra cuối thành công:      # {0} = count of OK proxies

# Proxy remove confirmations (ConnectionManagerViewModel)
Settings_Conn_RemoveProxy_Title             = Xóa proxy
Settings_Conn_RemoveProxy_One_Format        = Xóa proxy '{0}:{1}'?                        # {0} = host, {1} = port
Settings_Conn_RemoveProxy_Many_Format       = Xóa {0} proxy?                            # {0} = count
Settings_Conn_RemoveFailedProxy_Title       = Xóa các proxy thất bại
Settings_Conn_RemoveFailedProxy_One_Format  = Xóa proxy thất bại '{0}:{1}'?             # {0} = host, {1} = port
Settings_Conn_RemoveFailedProxy_Many_Format = Xóa {0} proxy thất bại?                     # {0} = count

# Proxy test/save status strings (ConnectionManagerViewModel / ProxySettingItem)
Settings_Conn_Status_Queued                 = Đã xếp hàng…
Settings_Conn_Status_Testing                = Đang kiểm tra…
Settings_Conn_Status_OkLive                 = OK (trực tiếp)
Settings_Conn_Status_OkLatencyIp_Format     = OK {0}ms ({1})                                 # {0} = ms, {1} = detected IP
Settings_Conn_Status_OkLatencyUnknown_Format = OK {0}ms (phản hồi không như mong đợi)                # {0} = ms
Settings_Conn_Status_Failed_Format          = Thất bại: {0}                                    # {0} = error first line / message
Settings_Conn_Status_Saved                  = Đã lưu
Settings_Conn_Status_SaveFailed_Format      = Lưu thất bại: {0}                               # {0} = error message
Settings_Conn_Status_ImportedNeedsSave_Format = Đã nhập {0} proxy — nhấn Lưu để lưu lại  # {0} = proxy count
Settings_Conn_Status_ExportedToFile_Format  = Đã xuất {0} proxy ra {1}                   # {0} = count, {1} = file name
```

---

## Settings — Accounts

```
Settings_Accounts_Title            = Trình quản lý tài khoản
Settings_Accounts_Desc             = Nhập và quản lý tất cả tài khoản Premium/Gold/Platinum của bạn.

Settings_Accounts_Col_Enabled      = ✓                                                       # check-mark glyph header — leave as glyph or localize as e.g. "On"
Settings_Accounts_Col_Hoster       = Nhà lưu trữ
Settings_Accounts_Col_Status       = Trạng thái
Settings_Accounts_Col_Username     = Tên đăng nhập
Settings_Accounts_Col_Password     = Mật khẩu
Settings_Accounts_Col_Type         = Loại

Settings_Accounts_Context_Edit     = Sửa tài khoản...
Settings_Accounts_Context_Refresh  = Kiểm tra / Làm mới
Settings_Accounts_Context_Enable   = Bật
Settings_Accounts_Context_Disable  = Tắt
Settings_Accounts_Context_Delete   = Xóa

Settings_Accounts_Btn_Add          = Thêm
Settings_Accounts_Btn_Remove       = Xóa
Settings_Accounts_Btn_Refresh      = Làm mới

# Account remove / validation
Settings_Accounts_Remove_Title             = Xóa tài khoản
Settings_Accounts_Remove_Message_Format    = Xóa tài khoản '{0}' của {1}?                  # {0} = username, {1} = file hoster name

Settings_Accounts_Validation_FillHosterUser = Vui lòng điền nhà lưu trữ tập tin, tên đăng nhập và mật khẩu.
Settings_Accounts_Check_DialogTitle         = Kiểm tra tài khoản
Settings_Accounts_Check_FailedAddAnyway_Format = Kiểm tra tài khoản thất bại: {0}\n\nVẫn thêm?    # {0} = error message
Settings_Accounts_Check_CouldNotVerifyAddAnyway_Format = Không thể xác minh tài khoản: {0}\n\nVẫn thêm?   # {0} = error message

# CheckAccountStatus inline status messages
Settings_Accounts_Status_Verifying          = Đang xác minh thông tin đăng nhập...
Settings_Accounts_Status_Checking           = Đang kiểm tra tài khoản...
Settings_Accounts_Status_NoAccountsToRefresh = Không có tài khoản nào để làm mới.
Settings_Accounts_Status_Verified_Format    = Đã xác minh: {0}                                  # {0} = result.Message
Settings_Accounts_Status_Warning_Format     = Cảnh báo: {0}                                   # {0} = result.Message
Settings_Accounts_Status_Valid_Format       = Hợp lệ: {0}                                     # {0} = result.Message
Settings_Accounts_Status_ValidExclaim_Format = Hợp lệ! {0}                                    # {0} = result.Message  (separate from above — different exclamation)
Settings_Accounts_Status_Failed_Format      = Thất bại: {0}                                    # {0} = result.Message
Settings_Accounts_Status_CheckError_Format  = Lỗi kiểm tra: {0}                               # {0} = exception.Message
Settings_Accounts_Status_Error_Format       = Lỗi: {0}                                     # {0} = exception.Message
Settings_Accounts_Status_AccountAdded_Format = Đã thêm tài khoản cho {0}!                        # {0} = file hoster name
Settings_Accounts_Status_NoImpl_Format      = Chưa hỗ trợ {0}. Không thể kiểm tra.       # {0} = file hoster name
Settings_Accounts_Status_NoImplWillSave_Format = Chưa hỗ trợ {0}. Tài khoản sẽ được lưu mà không xác minh.   # {0} = file hoster name
Settings_Accounts_Status_CheckingProgress_Format = Đang kiểm tra {0}@{1}... ({2}/{3})             # {0} = username, {1} = hoster, {2} = current, {3} = total
Settings_Accounts_Status_CheckingShort      = Đang kiểm tra...
Settings_Accounts_Status_NoImpl             = Chưa hỗ trợ
Settings_Accounts_Status_RefreshSummary_Format = Đã làm mới {0} tài khoản. {1} đã cập nhật.        # {0} = checked count, {1} = updated count
Settings_Accounts_Status_AccountDisabled_Format = Đã tắt tài khoản '{0}'.                    # {0} = username
Settings_Accounts_Status_AccountEnabled_Format  = Đã bật tài khoản '{0}'.                     # {0} = username

# AccountCheckResult fallback strings (SettingsViewModel)
Settings_Accounts_DefaultStatus_OK        = OK
Settings_Accounts_DefaultStatus_Failed    = Thất bại

# Password column placeholder
Settings_Accounts_PasswordMask            = ******
```

---

## Logs tab

```
Logs_AutoScroll                    = Tự động cuộn
Logs_BtnClear                      = Clear
Logs_Tab_Status                    = Trạng thái
Logs_Tab_Http                      = HTTP
Logs_Tab_Errors                    = Lỗi
Logs_Tab_UI                        = Giao diện

Logs_Col_DateTime                  = Thời gian
Logs_Col_Filename                  = Tên tệp
Logs_Col_Function                  = Hàm
Logs_Col_Line                      = Dòng
Logs_Col_Message                   = Thông điệp
Logs_Col_Thread                    = Luồng

# Status messages logged to the Status tab (UploadedViewModel) — these surface to the
# user via the Logs tab so they're worth localising.
Logs_Status_NoUrlsClipboardCleared = Không có URL nào trong vùng chọn; bộ nhớ tạm đã được xóa
Logs_Status_CopiedUrls_Format      = Đã sao chép {0} URL vào bộ nhớ tạm                          # {0} = url count
Logs_Status_HiddenFiles_Format     = Đã ẩn {0} tệp khỏi thẻ Lịch sử                   # {0} = file count
Logs_Status_ExportedPackages_Format = Đã xuất {0} gói ra {1}                         # {0} = pkg count, {1} = file path
```

---

## Upload Wizard

```
Wizard_Title                       = Trình hướng dẫn tải lên

Wizard_Step_DirectorySource        = 1. Thư mục
Wizard_Step_FileHosters            = 2. Nhà lưu trữ tập tin
Wizard_Step_Start                  = 3. Bắt đầu
Wizard_Step_FilesSource            = 1. Tệp

Wizard_Step0_Mode_Directory        = Tải lên thư mục
Wizard_Step0_Mode_Files            = Tải lên tệp

Wizard_Step0_Title                 = Chọn thư mục tải lên
Wizard_Step0_Desc                  = Chọn thư mục chứa các tệp bạn muốn tải lên.
Wizard_Step0_Browse                = Duyệt
Wizard_Step0_BrowseDialogTitle     = Chọn thư mục tải lên                                 # used when calling BrowseFolder

Wizard_Step1_Title                 = Chọn tệp
Wizard_Step1_PackageTitleLabel     = Tiêu đề gói:
Wizard_Step1_FilterLabel           = Bộ lọc:
Wizard_Step1_BtnSelectAll          = Chọn tất cả
Wizard_Step1_BtnDeselectAll        = Bỏ chọn tất cả
Wizard_Step1_Col_File              = Tệp
Wizard_Step1_Col_Size              = Kích thước
Wizard_Step1_SelectedLabel         = Đã chọn:
Wizard_Step1_FilesUnit             = tệp

Wizard_Step2_Title                 = Chọn nhà lưu trữ tập tin
Wizard_Step2_Desc                  = Chọn các nhà lưu trữ tập tin để tải lên và chọn tài khoản.
Wizard_Step2_Col_Use               = Dùng
Wizard_Step2_Col_FileHoster        = Nhà lưu trữ tập tin
Wizard_Step2_Col_Account           = Tài khoản
Wizard_Step2_AccountAnonymous      = (ẩn danh)
Wizard_Step2_AccountSelect         = (chọn tài khoản)
Wizard_Step2_AddAccountLink        = Thêm tài khoản…
Wizard_Step2_AccountRequiredTooltip = Nhà lưu trữ này yêu cầu tài khoản. Nhấn "Thêm tài khoản…" để thêm.

Wizard_Step3_Title                 = Khi nào bắt đầu
Wizard_Step3_Desc                  = Chọn khi nào lượt tải lên sẽ bắt đầu.
Wizard_Step3_Mode_Immediately      = Bắt đầu ngay sau khi đóng trình hướng dẫn
Wizard_Step3_Mode_Later            = Thêm vào hàng đợi nhưng bắt đầu sau (khởi động thủ công)
Wizard_Step3_Mode_Scheduled        = Lên lịch cho ngày và giờ cụ thể
Wizard_Step3_TimeFormatHint        = (HH:mm)

Wizard_Btn_Back                    = Quay lại
Wizard_Btn_Cancel                  = Hủy
Wizard_Btn_Next                    = Tiếp theo
Wizard_Btn_Add                     = Thêm

# Validation errors (UploadWizardViewModel.ShowError)
Wizard_Validation_PickValidDir     = Vui lòng chọn một thư mục hợp lệ.
Wizard_Validation_PickFile         = Vui lòng chọn ít nhất một tệp.
Wizard_Validation_PickHoster       = Vui lòng chọn ít nhất một nhà lưu trữ tập tin.
Wizard_Error_Format                = Lỗi: {0}                                              # {0} = exception.Message

Wizard_Step0_Files_Title           = Chọn tệp
Wizard_Step0_Files_Desc            = Chọn các tệp bạn muốn tải lên. Bạn có thể thêm nhiều hơn sau.
Wizard_Step0_Files_Pick            = Chọn tệp…
Wizard_Step0_Files_BrowseDialogTitle = Chọn tệp để tải lên                                  # used when calling BrowseFiles

Wizard_Step1_BtnAddMore            = Thêm tệp khác…
Wizard_Step1_DuplicateFilenameSuffixFormat = {0} (trong {1})                                    # {0} = filename, {1} = parent folder name

Wizard_Validation_PickAtLeastOneFile = Vui lòng chọn ít nhất một tệp trước khi tiếp tục.
Wizard_Validation_TitleRequired    = Vui lòng nhập tiêu đề gói.
```

---

## Confirmation Prompts (Settings → General list labels)

These are the user-visible labels for `ConfirmationKeys.All` — the strings shown in the
"Confirmation Prompts" section of Settings. Stable IDs (`remove-upload-package-or-file`
etc.) stay English.

```
Confirm_RemoveUploadPackageOrFile  = Xóa gói hoặc tệp khỏi thẻ Đang tải lên
Confirm_RemoveUploadedEntry        = Xóa các mục khỏi thẻ Lịch sử
Confirm_RemoveFileHosterAccount    = Xóa một tài khoản nhà lưu trữ tập tin
Confirm_RemoveProxy                = Xóa một proxy khỏi Trình quản lý kết nối
Confirm_ResetColumns               = Đặt lại các cột về mặc định trên thẻ Đang tải lên / Lịch sử
```

---

## Dialog windows

### About

```
About_WindowTitle                  = Giới thiệu CSUploader
About_AppName                      = CSUploader
About_Version_Format               = Phiên bản {0}                                             # {0} = assembly version, e.g. "1.2.3"
About_Description                  = Trình quản lý tải tệp mạnh mẽ cho nhiều dịch vụ lưu trữ. Tính năng bao gồm băm, quản lý hàng đợi và theo dõi tiến độ thời gian thực.
About_Field_Framework              = Nền tảng:
About_Field_Framework_Value        = .NET 10.0 (WPF)
About_Field_Database               = Cơ sở dữ liệu:
About_Field_Database_Value         = SQLite via EF Core 10
About_Field_License                = Giấy phép:
About_Field_License_Value          = MIT
About_Field_Source                 = Mã nguồn:
About_OK                           = OK
```

### CloseAction dialog

```
CloseAction_WindowTitle            = Đóng CSUploader
CloseAction_Heading                = Bạn muốn nút đóng làm gì?
CloseAction_Subheading             = Chọn một mục — bạn có thể thay đổi sau trong Cài đặt → Chung.
CloseAction_Remember               = Ghi nhớ lựa chọn của tôi
CloseAction_BtnMinimize            = Thu nhỏ vào khay
CloseAction_BtnExit                = Thoát
CloseAction_BtnCancel              = Hủy
```

### Confirmation dialog

```
Confirmation_WindowTitle           = Xác nhận
Confirmation_DontAskAgain          = Không hỏi lại cho hành động này
Confirmation_BtnYes                = Có
Confirmation_BtnNo                 = Không
```

### EditAccount dialog

```
EditAccount_WindowTitle            = Sửa tài khoản
EditAccount_AddTitle               = Thêm tài khoản                                             # used by SettingsViewModel.AddAccountDialog
EditAccount_FileHosterLabel        = Nhà lưu trữ tập tin:
EditAccount_UsernameLabel          = Tên đăng nhập:
EditAccount_PasswordLabel          = Mật khẩu:
EditAccount_AccountEnabled         = Tài khoản đã bật
EditAccount_BtnSave                = Lưu
EditAccount_BtnCancel              = Hủy
EditAccount_Validation_RequireUsernameAndPassword = Vui lòng nhập cả tên đăng nhập và mật khẩu.
```

### HttpDetails window

```
HttpDetails_WindowTitle            = Chi tiết giao dịch HTTP
HttpDetails_Tab_Request            = Yêu cầu
HttpDetails_Tab_Response           = Phản hồi
HttpDetails_Tab_FullDump           = Toàn bộ
HttpDetails_SubTab_Headers         = Tiêu đề
HttpDetails_SubTab_BodyRaw         = Nội dung (Thô)
HttpDetails_SubTab_BodyJson        = Nội dung (JSON)
HttpDetails_SubTab_Hex             = Hex

# Header strip
HttpDetails_Timing_Format          = Bắt đầu: {0}  |  Thời lượng: {1}ms  |  Kích thước: {2} byte    # {0} = HH:mm:ss.fff, {1} = ms, {2} = byte count
HttpDetails_Proxy_Format           = Proxy: {0}                                              # {0} = proxy display string
HttpDetails_NoData                 = (không có dữ liệu)
HttpDetails_NoBody                 = (không có nội dung)
# Section dividers used in the Full Dump (these are framed in box-drawing chars; the
# label words are the only translatable parts).
HttpDetails_FullDump_Request       = YÊU CẦU
HttpDetails_FullDump_Response      = PHẢN HỒI
```

### LogDetails window

```
LogDetails_WindowTitle             = Chi tiết nhật ký
LogDetails_Field_DateTime          = Thời gian:
LogDetails_Field_ThreadId          = ID luồng:
LogDetails_Field_Filename          = Tên tệp:
LogDetails_Field_Function          = Hàm:
LogDetails_Field_Line              = Dòng:
LogDetails_Tab_Text                = Văn bản
LogDetails_Tab_Html                = HTML
LogDetails_Btn_Close               = Đóng
```

### Progress / UpdateProgress windows

```
Progress_WindowTitle               = Vui lòng chờ...
Progress_DefaultLabel              = Đang tải...
Progress_LabelSuffix               = Vui lòng chờ...                                          # appended to the caller-supplied label on a new line
Progress_BtnCancel                 = Hủy
Progress_BtnCancelling             = Đang hủy...

UpdateProgress_WindowTitle         = Đang cập nhật CSUploader
UpdateProgress_StatusInitial       = Đang chuẩn bị…
UpdateProgress_StatusDownloading_Format = Đang tải bản cập nhật v{0}…                           # {0} = available semver
UpdateProgress_StatusRestarting    = Đang khởi động lại…
UpdateProgress_StatusFailed_Format = Cập nhật thất bại: {0}                                      # {0} = exception.Message
UpdateProgress_PercentInitial      = 0%
```

### ProxyText dialog

```
ProxyText_WindowTitle              = Proxy                                                 # XAML default — overridden via ctor with Import/Export titles
ProxyText_BtnImport                = Nhập
ProxyText_BtnCopy                  = Sao chép
ProxyText_BtnCancel                = Hủy
ProxyText_BtnClose                 = Đóng                                                   # replaces Cancel in read-only export mode
```

### SpeedLimit dialog

```
SpeedLimit_WindowTitle             = Giới hạn tốc độ
SpeedLimit_Heading                 = Đặt giới hạn tốc độ
SpeedLimit_Subheading              = Ghi đè cho từng gói. Để trống để dùng cài đặt chung.
SpeedLimit_Unit                    = KB/s
SpeedLimit_BtnClear                = Xóa
SpeedLimit_BtnCancel               = Hủy
SpeedLimit_BtnOk                   = OK
SpeedLimit_Validation_Title        = Giá trị không hợp lệ
SpeedLimit_Validation_Message      = Vui lòng nhập một số nguyên dương (KB/s), hoặc để trống để xóa.
```

---

## Status / inline messages

These are short status strings shown in non-dialog inline UI. Several have already been
listed in their owning section above; this section catches the rest, plus dialog-service
default titles.

```
Dialog_DefaultErrorTitle           = Lỗi
Dialog_DefaultConfirmTitle         = Xác nhận
Dialog_DefaultBrowseFolderTitle    = Chọn thư mục
Dialog_GenericErrorTitle           = Lỗi                                                   # ProgressWindow exception fallback

# File-picker filters (Microsoft.Win32 OpenFileDialog / SaveFileDialog)
Picker_Filter_Json                 = Tệp JSON (*.json)|*.json|Tất cả tệp (*.*)|*.*
Picker_Filter_ProxyLists           = Danh sách proxy (*.txt)|*.txt|Tất cả tệp (*.*)|*.*
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
Tray_Menu_Show                     = Hiện CSUploader                                         # "Show " + brand; localise the verb only
Tray_Menu_Exit                     = Thoát
Tray_Balloon_Title                 = CSUploader                                              # brand — do not translate
Tray_Balloon_Body                  = Vẫn đang chạy trong khay. Nhấn vào biểu tượng để khôi phục cửa sổ, hoặc nhấn chuột phải để chọn Thoát.
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

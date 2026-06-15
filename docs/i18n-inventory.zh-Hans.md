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
Common_OK                         = 确定
Common_Cancel                     = 取消
Common_Yes                        = 是
Common_No                         = 否
Common_Save                       = 保存
Common_Add                        = 添加
Common_Remove                     = 移除
Common_Close                      = 关闭
Common_Edit                       = 编辑
Common_Delete                     = 删除
Common_Browse                     = 浏览
Common_Back                       = 上一步
Common_Next                       = 下一步
Common_Apply                      = 应用
Common_Refresh                    = 刷新
Common_Details                    = 详情
Common_Copy                       = 复制
Common_Import                     = 导入
Common_Export                     = 导出
Common_All                        = 全部
Common_None                       = 无
Common_Test                       = 测试
Common_Confirm                    = 确认
Common_Error                      = 错误
Common_Warning                    = 警告
Common_SelectFolder               = 选择文件夹
Common_SelectFiles                 = 选择文件
Common_PleaseWait                 = 请稍候…
Common_Loading                    = 加载中…
Common_Cancelling                 = 正在取消…
Common_Preparing                  = 准备中…
Common_Unknown                    = 未知
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
Main_Title_UpdateAvailable_Format = CSUploader — 有可用更新（v{0}）— 点击 帮助 → 安装更新     # {0} = available semver
Main_Menu_File                    = 文件(_F)
Main_Menu_File_Exit               = 退出(_E)
Main_Menu_File_Exit_Gesture       = Alt+F4
Main_Menu_View                    = 视图(_V)
Main_Menu_View_UploadOverview     = 上传概览(_O)
Main_Menu_View_DarkMode           = 深色模式(_D)
Main_Menu_View_LightMode          = 浅色模式(_L)
Main_Menu_Help                    = 帮助(_H)
Main_Menu_Help_CheckForUpdates    = 检查更新(_C)…
Main_Menu_Help_InstallUpdate      = 安装更新(_I)
Main_Menu_Help_About              = 关于 CSUploader(_A)

Main_Tab_Uploads                  = 上传中
Main_Tab_Uploaded                 = 历史
Main_Tab_Settings                 = 设置
Main_Tab_Logs                     = 日志

Main_CheckForUpdates_DialogTitle  = 检查更新
Main_CheckForUpdates_AlreadyLatest = 您当前已是最新版本。
Main_CheckForUpdates_Available_Format = 有可用更新：v{0}。\n\n请使用 帮助 → 安装更新 来下载并安装。   # {0} = available semver
```

---

## Uploads tab

Toolbar, context menu, columns, overview panel, filter, and footer link.

```
Uploads_Toolbar_AddTip            = 添加新上传
Uploads_Toolbar_StartTip          = 开始所有上传
Uploads_Toolbar_PauseTip          = 暂停 / 恢复
Uploads_Toolbar_StopTip           = 停止所有上传
Uploads_Toolbar_RemoveTip         = 移除所选项

Uploads_Context_Start             = 开始
Uploads_Context_StartNow          = 立即开始
Uploads_Context_Stop              = 停止
Uploads_Context_SkipUpload        = 跳过上传
Uploads_Context_Reset             = 重置
Uploads_Context_OpenSourceDir     = 打开源目录
Uploads_Context_SetSpeedLimit     = 设置速度限制…
Uploads_Context_Priority          = 优先级
Uploads_Context_Remove            = 移除
Uploads_Priority_Highest          = 最高
Uploads_Priority_High             = 高
Uploads_Priority_Normal           = 普通
Uploads_Priority_Low              = 低
Uploads_Priority_Lowest           = 最低
Uploads_Tooltip_IncreasePriority  = 提高优先级
Uploads_Tooltip_DecreasePriority  = 降低优先级

Uploads_Col_Name                  = 名称
Uploads_Col_Size                  = 大小
Uploads_Col_Hoster                = 文件托管商
Uploads_Col_Account               = 账户
Uploads_Col_Status                = 状态
Uploads_Col_Speed                 = 速度
Uploads_Col_ETA                   = 剩余时间
Uploads_Col_BytesLoaded           = 已传字节
Uploads_Col_BytesRemaining        = 剩余字节
Uploads_Col_Progress              = 进度
Uploads_Col_SaveTo                = 保存至
Uploads_Col_Added                 = 添加时间
Uploads_Col_Finished              = 完成时间
Uploads_Col_ScheduledAt           = 计划开始时间
Uploads_Col_Duration              = 耗时
Uploads_Col_Priority              = 优先级
Uploads_Col_SpeedLimit            = 速度限制
Uploads_Col_URL                   = URL
Uploads_Col_Hash                  = 哈希
Uploads_Col_Error                 = 错误

Uploads_ColumnHeader_LockTip      = 锁定列宽
Uploads_ColumnMenu_Reset          = 重置列
Uploads_ColumnMenu_DefaultLabel   = 列                                              # fallback when a column has no header text

Uploads_Overview_Title            = 上传概览
Uploads_Overview_CloseTip         = 关闭概览
Uploads_Overview_ToggleTip        = 显示 / 隐藏概览统计

Uploads_Overview_Packages         = 包数
Uploads_Overview_Links            = 链接数
Uploads_Overview_TotalBytes       = 总字节数
Uploads_Overview_Uploadspeed      = 上传速度
Uploads_Overview_BytesLoaded      = 已传字节
Uploads_Overview_RemainingBytes   = 剩余字节
Uploads_Overview_Eta              = 剩余时间
Uploads_Overview_RunningUploads   = 运行中的上传
Uploads_Overview_OpenConnections  = 打开的连接
Uploads_Overview_FinishedLinks    = 已完成链接
Uploads_Overview_SkippedLinks     = 已跳过链接
Uploads_Overview_FailedLinks      = 失败的链接

# Same labels appear inline with a trailing colon. Splitting them so a translator can
# control whether the colon is part of the term (typographic spacing differs in CJK).
Uploads_Overview_PackagesLabel        = 包数：
Uploads_Overview_LinksLabel           = 链接数：
Uploads_Overview_TotalBytesLabel      = 总字节数：
Uploads_Overview_UploadspeedLabel     = 上传速度：
Uploads_Overview_BytesLoadedLabel     = 已传字节：
Uploads_Overview_RemainingBytesLabel  = 剩余字节：
Uploads_Overview_EtaLabel             = 剩余时间：
Uploads_Overview_RunningUploadsLabel  = 运行中的上传：
Uploads_Overview_OpenConnectionsLabel = 打开的连接：
Uploads_Overview_FinishedLinksLabel   = 已完成链接：
Uploads_Overview_SkippedLinksLabel    = 已跳过链接：
Uploads_Overview_FailedLinksLabel     = 失败的链接：

Uploads_FilterLabel               = 文件名
Uploads_FilterTip                 = 按文件或包名筛选
Uploads_FooterPremiumLink         = 添加高级账户…

# Item-state names rendered in the Status column (FileStateDisplayConverter).
Uploads_State_Idle                = 空闲
Uploads_State_HashQueued          = 校验排队中
Uploads_State_Hashing             = 校验中
Uploads_State_UploadQueued        = 上传排队中
Uploads_State_Uploading           = 上传中
Uploads_State_Completed           = 已完成
Uploads_State_CompletedWithErrors = 完成（有错误）
Uploads_State_Failed              = 失败
Uploads_State_Paused              = 已暂停
Uploads_State_Cancelled           = 已取消

# ETA fallback (UploadsViewModel)
Uploads_Eta_NotApplicable         = ~

# Reset-columns confirmation (different wording per tab)
Uploads_ResetColumns_Title        = 重置列
Uploads_ResetColumns_Message      = 是否将"上传中"标签页的列重置为默认设置？这将清除您自定义的显示/隐藏和排序。

# Remove confirmation prompts (UploadsViewModel)
Uploads_Remove_Title              = 移除
Uploads_Remove_Package_Format     = 是否移除包"{0}"及其 {1} 个文件？                            # {0} = package name, {1} = file count
Uploads_Remove_File_Format        = 是否将"{0}"从上传列表中移除？                                   # {0} = file name
Uploads_Remove_Generic            = 是否移除此项？
Uploads_Remove_PackagesOnly_Format     = 是否移除 {0} 个包（共 {1} 个文件）？                            # {0} = package count, {1} = total file count
Uploads_Remove_FilesOnly_Format        = 是否从上传列表中移除 {0} 个文件？                       # {0} = file count
Uploads_Remove_PackagesAndFiles_Format = 是否移除 {0} 个包和 {1} 个文件（共 {2} 项）？      # {0} = packages, {1} = loose files, {2} = total

# Reset confirmation prompts
Uploads_Reset_Title                  = 重置
Uploads_Reset_Package_Format         = 是否重置包 '{0}'？这将重新哈希并重新上传该包中 {1} 个已完成的文件。  # {0} = package name, {1} = completed file count
Uploads_Reset_File_Format            = 是否重置 '{0}'？该文件已成功上传 — 重置将重新哈希并重新上传。  # {0} = file name
```

---

## Uploaded tab

```
Uploaded_Toolbar_ExportJson       = 导出为 JSON…
Uploaded_Toolbar_ExportJsonTip    = 将所有已完成的上传（包含全部字段）导出到 JSON 文件

Uploaded_Col_Name                 = 名称
Uploaded_Col_Path                 = 路径
Uploaded_Col_Size                 = 大小
Uploaded_Col_Hoster               = 文件托管商
Uploaded_Col_Account              = 账户
Uploaded_Col_Finished             = 完成时间
Uploaded_Col_URL                  = URL
Uploaded_Col_Hash                 = 哈希

Uploaded_Context_Copy             = 复制
Uploaded_Context_Copy_Gesture     = Ctrl+C
Uploaded_Context_CopyURL          = 复制 URL
Uploaded_Context_Remove           = 移除
Uploaded_Context_Remove_Gesture   = Del
Uploaded_Context_ExportJson       = 导出为 JSON…

# Reset-columns confirmation
Uploaded_ResetColumns_Title       = 重置列
Uploaded_ResetColumns_Message     = 是否将"历史"标签页的列重置为默认设置？这将清除您自定义的显示/隐藏和排序。

# Remove confirmation
Uploaded_Remove_Title             = 移除
Uploaded_Remove_Single_Format     = 是否将"{0}"从历史记录中移除？                                          # {0} = file name
Uploaded_Remove_Many_Format       = 是否从历史记录中移除 {0} 条记录？                                    # {0} = entry count
```

---

## Settings — General

```
Settings_Sidebar_General          = 常规
Settings_Sidebar_Upload           = 上传
Settings_Sidebar_Connection       = 连接
Settings_Sidebar_Accounts         = 账户

Settings_General_Language_Title        = 语言
Settings_General_Language_Desc         = 界面语言。更改将立即生效。
Settings_General_Language_Label        = 语言

Settings_General_Developer_Title       = 开发者
Settings_General_Developer_Desc        = 用于本地开发和测试的选项。
Settings_General_UseMockServer         = 使用模拟服务器（将所有文件托管商请求重定向到 localhost:8080/<hoster>）   # review — hidden behind a developer flag, may stay English

Settings_General_GridAppearance_Title  = 表格外观
Settings_General_GridAppearance_Desc   = 用于"上传中"和"历史"标签页的字体。修改将立即生效。
Settings_General_GridFont              = 表格字体
Settings_General_GridFontSize          = 表格字号

Settings_General_WindowBehaviour_Title = 窗口行为
Settings_General_WindowBehaviour_Desc  = 选择主窗口最小化或关闭时的行为。
Settings_General_MinimizeToTray        = 将主窗口最小化到系统托盘而非任务栏
Settings_General_CloseAction           = 关闭按钮行为

Settings_General_CloseAction_Ask         = 每次询问
Settings_General_CloseAction_MinToTray   = 最小化到托盘
Settings_General_CloseAction_Exit        = 退出应用

Settings_General_Notifications_Title  = 通知
Settings_General_Notifications_Desc   = 上传完成时在右下角显示弹出通知。
Settings_General_ShowCompletionToasts = 上传完成时显示弹出通知

Settings_General_ConfirmationPrompts_Title = 确认提示
Settings_General_ConfirmationPrompts_Desc  = 勾选某项以在执行该操作前再次询问。取消勾选将不再显示该确认。

Settings_General_Database_Title            = 数据库
Settings_General_Database_Desc             = CSUploader 将您的上传历史记录（包、文件和 URL）保存在本地 SQLite 数据库中。从"上传中"或"历史"标签页中删除条目时只会将其隐藏 — 数据行仍保留在数据库中。点击"清除"将永久删除已从两个标签页中隐藏的数据行。
Settings_General_Database_BtnClear         = 清除
Settings_General_Database_ConfirmTitle     = 清除数据库
Settings_General_Database_ConfirmMessage   = 永久删除已从两个标签页中隐藏的上传记录吗？\n\n活动中和可见的上传不会受影响。此操作不可撤销。
Settings_General_Database_Status_Cleared_Format    = 已从数据库中清除 {0} 个文件行和 {1} 个包行。
Settings_General_Database_Status_NothingToClear    = 没有可清除的隐藏数据行。
Settings_General_Database_BtnClearLogs              = Clear logs
Settings_General_Database_ConfirmClearLogsTitle     = Clear log history
Settings_General_Database_ConfirmClearLogsMessage   = Permanently delete all log entries from the database?\n\nThe Logs tab will also be emptied. This cannot be undone.
Settings_General_Database_LogsCleared_Format        = Cleared {0} log entr(ies) from the database.
Settings_General_Database_LogsNothingToClear        = No log entries to clear.
```

---

## Toast notifications

```
Toast_FileCompleted_Title    = 上传完成
Toast_FileCompleted_Body     = {0}
Toast_PackageCompleted_Title = 包上传完成
Toast_PackageCompleted_Body  = 已上传 {0} / {1} 个文件 — {2}
```

---

## Settings — Upload

```
Settings_Upload_Mgmt_Title         = 上传管理
Settings_Upload_Mgmt_Desc          = 配置连接限制、优先级……设置上传控制器的详细参数。
Settings_Upload_MaxConcurrent      = 最大同时上传数
Settings_Upload_MaxPerHoster       = 每个文件托管商的最大同时上传数
Settings_Upload_RemoveFinished     = 移除已完成的上传
Settings_Upload_IfFileExists       = 当文件已存在时
Settings_Upload_MaxCpuJobs         = 最大同时 CPU 任务数
Settings_Upload_SpeedLimit         = 速度限制（KB/s）

Settings_Upload_RemoveFinished_Never              = 从不
Settings_Upload_RemoveFinished_Immediately        = 立即
Settings_Upload_RemoveFinished_AtStartup          = 启动时
Settings_Upload_RemoveFinished_WhenPackageReady   = 包就绪时

Settings_Upload_IfExists_Ask                      = 每个文件都询问
Settings_Upload_IfExists_Skip                     = 跳过
Settings_Upload_IfExists_Overwrite                = 覆盖
Settings_Upload_IfExists_Rename                   = 重命名

Settings_Upload_Autostart_Title                   = 自动开始上传
Settings_Upload_Autostart_Desc                    = 选择 CSUploader 是否以及何时无需用户操作即可启动待处理的上传。
Settings_Upload_Autostart                         = 应用启动时自动开始上传

Settings_Upload_Autostart_Always                  = 始终
Settings_Upload_Autostart_OnlyIfRunning           = 仅当上次会话结束时仍有上传在运行
Settings_Upload_Autostart_Never                   = 从不
```

---

## Settings — Connection (proxy manager)

```
Settings_Conn_Title                = 连接管理器
Settings_Conn_Desc                 = 如果访问互联网需要代理，请在此处配置。多个代理将在新上传中轮换使用。在没有启用任何代理时，默认行为是直接连接。
Settings_Conn_UseProxies           = 使用代理
Settings_Conn_UseProxiesTip        = 轮换的总开关。关闭后，即使列表中存在代理，所有流量（上传和账户检查）也将直接连接 — 便于在尚未正式启用前添加和测试代理。
Settings_Conn_AutoDisable          = 自动取消勾选失败的代理
Settings_Conn_AutoDisableTip       = 启用后，手动测试或上传失败的代理将被取消勾选，从而在轮换中跳过该代理。无论如何状态图标都会更新。
Settings_Conn_AllowInvalidCert    = 接受无效的服务器证书（不推荐）
Settings_Conn_AllowInvalidCertTip = 跳过所有出站请求的 TLS 证书验证。某些托管服务的存储 CDN 节点（例如 FileBoom 的 cmb-*.filestore.app 边缘）使用的证书无法通过标准验证时需要。这会禁用对 MITM 攻击的保护 — 仅在上传因 SSL 错误而失败时才启用。

Settings_Conn_Col_On               = 启用
Settings_Conn_Col_Priority         = 优先级
Settings_Conn_Col_Type             = 类型
Settings_Conn_Col_Host             = 主机 / IP
Settings_Conn_Col_Port             = 端口
Settings_Conn_Col_User             = 用户名
Settings_Conn_Col_Password         = 密码
Settings_Conn_Col_Test             = 测试
Settings_Conn_Col_Status           = 状态

Settings_Conn_PriorityUpTip        = 上移（提高优先级）
Settings_Conn_PriorityDownTip      = 下移（降低优先级）

Settings_Conn_Context_Test         = 测试
Settings_Conn_Context_Remove       = 移除
Settings_Conn_Context_Remove_Gesture = Del

Settings_Conn_Btn_Import           = 导入
Settings_Conn_Btn_Import_FromText  = 从文本导入…
Settings_Conn_Btn_Import_FromFile  = 从文件导入…
Settings_Conn_Btn_Export           = 导出
Settings_Conn_Btn_Export_AllToText = 将所有代理导出到文本…
Settings_Conn_Btn_Export_AllToFile = 将所有代理导出到文件…
Settings_Conn_Btn_Export_OkToText  = 将测试通过的代理导出到文本…
Settings_Conn_Btn_Export_OkToFile  = 将测试通过的代理导出到文件…
Settings_Conn_Btn_Export_SelectedToText = 将选定的代理导出到文本…
Settings_Conn_Btn_Export_SelectedToFile = 将选定的代理导出到文件…
Settings_Conn_Btn_Save             = 保存
Settings_Conn_Btn_Add              = 添加
Settings_Conn_Btn_Remove           = 移除
Settings_Conn_Btn_RemoveSelected   = 移除所选项
Settings_Conn_Btn_RemoveFailed     = 移除失败的项
Settings_Conn_Btn_TestAll          = 全部测试
Settings_Conn_Btn_TestAllTip       = 测试列表中所有代理的连通性
Settings_Conn_Btn_Details          = 详情

# Proxy import/export dialogs (ProxyTextDialog ctor args, ConnectionManagerViewModel)
Settings_Conn_ImportProxies_FileDialogTitle = 导入代理
Settings_Conn_ImportProxies_FileFilter      = 代理列表 (*.txt)|*.txt|所有文件 (*.*)|*.*
Settings_Conn_ImportProxies_DialogTitle     = 导入代理
Settings_Conn_ImportProxies_DialogDesc      = 粘贴代理行（每行一个）。格式：scheme://[user:pass@]host[:port] — 端口默认按协议为 80/443/1080。
Settings_Conn_ExportAll_DialogTitle         = 导出所有代理
Settings_Conn_ExportOk_DialogTitle          = 导出测试通过的代理
Settings_Conn_ExportAll_Desc_Format         = 共 {0} 个代理：                                  # {0} = count of all proxies
Settings_Conn_ExportOk_Desc_Format          = 共 {0} 个最近一次测试成功的代理：      # {0} = count of OK proxies
Settings_Conn_ExportSelected_DialogTitle    = 导出选定的代理
Settings_Conn_ExportSelected_Desc_Format    = 共 {0} 个选定的代理：                         # {0} = count of selected proxies

# Proxy remove confirmations (ConnectionManagerViewModel)
Settings_Conn_RemoveProxy_Title             = 移除代理
Settings_Conn_RemoveProxy_One_Format        = 是否移除代理"{0}:{1}"？                        # {0} = host, {1} = port
Settings_Conn_RemoveProxy_Many_Format       = 是否移除 {0} 个代理？                            # {0} = count
Settings_Conn_RemoveFailedProxy_Title       = 移除失败的代理
Settings_Conn_RemoveFailedProxy_One_Format  = 是否移除失败的代理"{0}:{1}"？             # {0} = host, {1} = port
Settings_Conn_RemoveFailedProxy_Many_Format = 是否移除 {0} 个失败的代理？                     # {0} = count

# Proxy test/save status strings (ConnectionManagerViewModel / ProxySettingItem)
Settings_Conn_Status_Queued                 = 已排队…
Settings_Conn_Status_Testing                = 测试中…
Settings_Conn_Status_OkLive                 = 正常（在线）
Settings_Conn_Status_OkLatencyIp_Format     = 正常 {0}ms ({1})                                 # {0} = ms, {1} = detected IP
Settings_Conn_Status_OkLatencyUnknown_Format = 正常 {0}ms（响应异常）                # {0} = ms
Settings_Conn_Status_Failed_Format          = 失败：{0}                                    # {0} = error first line / message
Settings_Conn_Status_Saved                  = 已保存
Settings_Conn_Status_SaveFailed_Format      = 保存失败：{0}                               # {0} = error message
Settings_Conn_Status_Imported_Format        = 已导入 {0} 个代理                              # {0} = proxy count
Settings_Conn_Status_ExportedToFile_Format  = 已将 {0} 个代理导出到 {1}                   # {0} = count, {1} = file name
```

---

## Settings — Accounts

```
Settings_Accounts_Title            = 账户管理器
Settings_Accounts_Desc             = 录入并管理您的所有 Premium/Gold/Platinum 账户。

Settings_Accounts_Col_Enabled      = ✓                                                       # check-mark glyph header — leave as glyph or localize as e.g. "On"
Settings_Accounts_Col_Hoster       = 文件托管商
Settings_Accounts_Col_Status       = 状态
Settings_Accounts_Col_Username     = 用户名
Settings_Accounts_Col_Password     = 密码
Settings_Accounts_Col_Type         = 类型
Settings_Accounts_Col_Used        = 已用
Settings_Accounts_Col_Available   = 可用
Settings_Accounts_Col_RefreshedAt = 刷新时间
Settings_Accounts_Storage_Unlimited= 无限制

Settings_Accounts_Context_Edit     = 编辑账户…
Settings_Accounts_Context_Refresh  = 检查 / 刷新
Settings_Accounts_Context_Enable   = 启用
Settings_Accounts_Context_Disable  = 禁用
Settings_Accounts_Context_Delete   = 删除

Settings_Accounts_Btn_Add          = 添加
Settings_Accounts_Btn_Remove       = 移除
Settings_Accounts_Btn_Refresh      = 刷新

# Account remove / validation
Settings_Accounts_Remove_Title             = 移除账户
Settings_Accounts_Remove_Message_Format    = 是否移除 {1} 的账户"{0}"？                  # {0} = username, {1} = file hoster name
Settings_Accounts_Remove_MessageBulk_Format= 是否移除 {0} 个所选账户？

Settings_Accounts_Validation_FillHosterUser = 请填写文件托管商、用户名和密码。
Settings_Accounts_Check_DialogTitle         = 账户检查
Settings_Accounts_Check_FailedAddAnyway_Format = 账户检查失败：{0}\n\n是否仍然添加？    # {0} = error message
Settings_Accounts_Check_CouldNotVerifyAddAnyway_Format = 无法验证账户：{0}\n\n是否仍然添加？   # {0} = error message

# CheckAccountStatus inline status messages
Settings_Accounts_Status_Verifying          = 正在验证凭据…
Settings_Accounts_Status_Checking           = 正在检查账户…
Settings_Accounts_Status_NoAccountsToRefresh = 没有可刷新的账户。
Settings_Accounts_Status_Verified_Format    = 已验证：{0}                                  # {0} = result.Message
Settings_Accounts_Status_Warning_Format     = 警告：{0}                                   # {0} = result.Message
Settings_Accounts_Status_Valid_Format       = 有效：{0}                                     # {0} = result.Message
Settings_Accounts_Status_ValidExclaim_Format = 有效！{0}                                    # {0} = result.Message  (separate from above — different exclamation)
Settings_Accounts_Status_Failed_Format      = 失败：{0}                                    # {0} = result.Message
Settings_Accounts_Status_CheckError_Format  = 检查错误：{0}                               # {0} = exception.Message
Settings_Accounts_Status_Error_Format       = 错误：{0}                                     # {0} = exception.Message
Settings_Accounts_Status_AccountAdded_Format = 已为 {0} 添加账户！                        # {0} = file hoster name
Settings_Accounts_Status_NoImpl_Format      = 尚未实现 {0}，无法检查。       # {0} = file hoster name
Settings_Accounts_Status_NoImplWillSave_Format = 尚未实现 {0}，账户将不经验证直接保存。   # {0} = file hoster name
Settings_Accounts_Status_CheckingProgress_Format = 正在检查 {0}@{1}…（{2}/{3}）             # {0} = username, {1} = hoster, {2} = current, {3} = total
Settings_Accounts_Status_CheckingShort      = 检查中…
Settings_Accounts_Status_NoImpl             = 尚未实现
Settings_Accounts_Status_RefreshSummary_Format = 已刷新 {0} 个账户，{1} 个有更新。        # {0} = checked count, {1} = updated count
Settings_Accounts_Status_AccountDisabled_Format = 账户"{0}"已禁用。                    # {0} = username
Settings_Accounts_Status_AccountEnabled_Format  = 账户"{0}"已启用。                     # {0} = username
Settings_Accounts_Status_AccountsBulkDisabled_Format= 已禁用 {0} 个账户。
Settings_Accounts_Status_AccountsBulkEnabled_Format= 已启用 {0} 个账户。

# AccountCheckResult fallback strings (SettingsViewModel)
Settings_Accounts_DefaultStatus_OK        = 正常
Settings_Accounts_DefaultStatus_Failed    = 失败

# Password column placeholder
Settings_Accounts_PasswordMask            = ******
```

---

## Logs tab

```
Logs_AutoScroll                    = 自动滚动
Logs_BtnClear                      = Clear
Logs_Tab_Status                    = 状态
Logs_Tab_Http                      = HTTP
Logs_Tab_Errors                    = 错误
Logs_Tab_UI                        = 界面

Logs_Col_DateTime                  = 时间
Logs_Col_Status                    = 状态
Logs_Col_Filename                  = 文件名
Logs_Col_Function                  = 函数
Logs_Col_Line                      = 行号
Logs_Col_Message                   = 消息
Logs_Col_Thread                    = 线程

# Status messages logged to the Status tab (UploadedViewModel) — these surface to the
# user via the Logs tab so they're worth localising.
Logs_Status_NoUrlsClipboardCleared = 所选项中未包含 URL；剪贴板已清空
Logs_Status_CopiedUrls_Format      = 已复制 {0} 个 URL 到剪贴板                          # {0} = url count
Logs_Status_HiddenFiles_Format     = 已从"历史"标签页隐藏 {0} 个文件                   # {0} = file count
Logs_Status_ExportedPackages_Format = 已将 {0} 个包导出到 {1}                         # {0} = pkg count, {1} = file path
```

---

## Upload Wizard

```
Wizard_Title                       = 上传向导

Wizard_Step_DirectorySource        = 1. 目录
Wizard_Step_FileHosters            = 2. 文件托管商
Wizard_Step_Summary               = 3. 摘要
Wizard_Step_Start                  = 4. 开始
Wizard_Summary_Title              = 上传摘要
Wizard_Summary_Desc               = 查看将上传到每个托管商的内容。无可上传文件的托管商将被忽略。
Wizard_Summary_FileCount_Suffix   = 个文件
Wizard_Summary_OrphanWarning_Suffix= 个文件无法上传到任何托管商：
Wizard_Summary_MaxFileSize_Format = 每个文件最多 {0}
Wizard_Step_FilesSource            = 1. 文件

Wizard_Step0_Mode_Directory        = 上传目录
Wizard_Step0_Mode_Files            = 上传文件

Wizard_Step0_Title                 = 选择上传目录
Wizard_Step0_Desc                  = 选择包含您要上传文件的目录。
Wizard_Step0_Browse                = 浏览
Wizard_Step0_BrowseDialogTitle     = 选择上传目录                                 # used when calling BrowseFolder

Wizard_Step1_Title                 = 选择文件
Wizard_Step1_PackageTitleLabel     = 包标题：
Wizard_Step1_FilterLabel           = 筛选：
Wizard_Step1_BtnSelectAll          = 全选
Wizard_Step1_BtnDeselectAll        = 取消全选
Wizard_Step1_BtnRemove            = 移除
Wizard_Step1_Col_File              = 文件
Wizard_Step1_Col_Size              = 大小
Wizard_Step1_SelectedLabel         = 已选：
Wizard_Step1_FilesUnit             = 个文件

Wizard_Step2_Title                 = 选择文件托管商
Wizard_Step2_Desc                  = 选择要上传到的文件托管商，并选择对应账户。
Wizard_Step2_Col_Use               = 使用
Wizard_Step2_Col_FileHoster        = 文件托管商
Wizard_Step2_Col_Account           = 账户
Wizard_Step2_AccountAnonymous      = （匿名）
Wizard_Step2_AccountSelect         = （选择账户）
Wizard_Step2_AddAccountLink        = 添加账户…
Wizard_Step2_AccountRequiredTooltip = 此托管商需要账户。点击"添加账户…"以添加。
Wizard_Hoster_LimitsHeader         = 该托管商的限制将被超出：
Wizard_Hoster_FileTooLarge_Format  = {0}：以下文件超过单文件 {1} 上限，将不会上传：\n{2}
Wizard_Hoster_TooManyFiles_Format  = {0}：已选 {1} 个文件，但每包上限为 {2} 个。

Wizard_Step3_Title                 = 何时开始
Wizard_Step3_Desc                  = 选择上传应何时开始。
Wizard_Step3_Mode_Immediately      = 关闭向导后立即开始
Wizard_Step3_Mode_Later            = 加入队列但稍后开始（手动启动）
Wizard_Step3_Mode_Scheduled        = 计划在指定的日期和时间开始
Wizard_Step3_TimeFormatHint        = (HH:mm)

Wizard_Btn_Back                    = 上一步
Wizard_Btn_Cancel                  = 取消
Wizard_Btn_Next                    = 下一步
Wizard_Btn_Add                     = 添加

# Validation errors (UploadWizardViewModel.ShowError)
Wizard_Validation_PickValidDir     = 请选择一个有效的目录。
Wizard_Validation_PickFile         = 请至少选择一个文件。
Wizard_Validation_PickHoster       = 请至少选择一个文件托管商。
Wizard_Error_Format                = 错误：{0}                                              # {0} = exception.Message

Wizard_Step0_Files_Title           = 选择文件
Wizard_Step0_Files_Desc            = 选择您要上传的文件。您可以稍后添加更多文件。
Wizard_Step0_Files_Pick            = 添加文件…
Wizard_Step0_Files_BrowseDialogTitle = 选择要上传的文件                                  # used when calling BrowseFiles

Wizard_Step1_DuplicateFilenameSuffixFormat = {0}（位于 {1}）                                    # {0} = filename, {1} = parent folder name

Wizard_Validation_PickAtLeastOneFile = 继续之前请至少选择一个文件。
Wizard_Validation_TitleRequired    = 请输入包标题。
```

---

## Confirmation Prompts (Settings → General list labels)

These are the user-visible labels for `ConfirmationKeys.All` — the strings shown in the
"Confirmation Prompts" section of Settings. Stable IDs (`remove-upload-package-or-file`
etc.) stay English.

```
Confirm_RemoveUploadPackageOrFile  = 从"上传中"标签页移除包或文件
Confirm_RemoveUploadedEntry        = 从"历史"标签页中移除条目
Confirm_RemoveFileHosterAccount    = 移除文件托管商账户
Confirm_RemoveProxy                = 从连接管理器移除代理
Confirm_ResetCompletedUpload       = 重置已完成的上传（重新哈希并重新上传）
Confirm_ResetColumns               = 将"上传中"/"历史"标签页的列重置为默认设置
```

---

## Dialog windows

### About

```
About_WindowTitle                  = 关于 CSUploader
About_AppName                      = CSUploader
About_Version_Format               = 版本 {0}                                             # {0} = assembly version, e.g. "1.2.3"
About_Description                  = 一款功能强大的多文件托管服务上传管理器，支持哈希校验、队列管理和实时进度跟踪。
About_Field_Framework              = 框架：
About_Field_Framework_Value        = .NET 10.0 (WPF)
About_Field_Database               = 数据库：
About_Field_Database_Value         = SQLite via EF Core 10
About_Field_License                = 许可证：
About_Field_License_Value          = MIT
About_Field_Source                 = 源码：
About_OK                           = 确定
```

### CloseAction dialog

```
CloseAction_WindowTitle            = 关闭 CSUploader
CloseAction_Heading                = 您希望关闭按钮执行什么操作？
CloseAction_Subheading             = 选择一项 — 您之后可在 设置 → 常规 中更改。
CloseAction_Remember               = 记住我的选择
CloseAction_BtnMinimize            = 最小化到托盘
CloseAction_BtnExit                = 退出
CloseAction_BtnCancel              = 取消
```

### Confirmation dialog

```
Confirmation_WindowTitle           = 确认
Confirmation_DontAskAgain          = 此操作不再询问
Confirmation_BtnYes                = 是
Confirmation_BtnNo                 = 否
```

### EditAccount dialog

```
EditAccount_WindowTitle            = 编辑账户
EditAccount_AddTitle               = 添加账户                                             # used by SettingsViewModel.AddAccountDialog
EditAccount_FileHosterLabel        = 文件托管商：
EditAccount_UsernameLabel          = 用户名：
EditAccount_PasswordLabel          = 密码：
EditAccount_AccountEnabled         = 账户已启用
EditAccount_BtnSave                = 保存
EditAccount_BtnCancel              = 取消
EditAccount_Validation_RequireUsernameAndPassword = 请输入用户名和密码。

EditProxy_AddTitle                 = 添加代理
EditProxy_EditTitle                = 编辑代理
EditProxy_EnabledLabel             = 启用代理
EditProxy_BtnSave                  = 保存
EditProxy_BtnCancel                = 取消
EditProxy_BtnTest                  = 测试
EditProxy_Validation_HostRequired  = 请输入主机或 IP 地址。
EditProxy_Validation_PortInvalid   = 请输入 1 到 65535 之间的有效端口。
EditProxy_Status_Testing           = 正在测试…
EditProxy_Status_OkLatency_Format  = 正常 {0}ms（响应异常）
EditProxy_Status_OkLatencyIp_Format = 正常 {0}ms（{1}）
EditProxy_Status_Failed_Format     = 失败：{0}
```

### HttpDetails window

```
HttpDetails_WindowTitle            = HTTP 事务详情
HttpDetails_Tab_Request            = 请求
HttpDetails_Tab_Response           = 响应
HttpDetails_Tab_FullDump           = 完整转储
HttpDetails_SubTab_Headers         = 头部
HttpDetails_SubTab_BodyRaw         = 正文（原始）
HttpDetails_SubTab_BodyJson        = 正文（JSON）
HttpDetails_SubTab_Hex             = 十六进制

# Header strip
HttpDetails_Timing_Format          = 开始时间：{0}  |  耗时：{1}ms  |  大小：{2} 字节    # {0} = HH:mm:ss.fff, {1} = ms, {2} = byte count
HttpDetails_Proxy_Format           = 代理：{0}                                              # {0} = proxy display string
HttpDetails_NoData                 = （无数据）
HttpDetails_NoBody                 = （无正文）
# Section dividers used in the Full Dump (these are framed in box-drawing chars; the
# label words are the only translatable parts).
HttpDetails_FullDump_Request       = 请求
HttpDetails_FullDump_Response      = 响应
```

### LogDetails window

```
LogDetails_WindowTitle             = 日志详情
LogDetails_Field_DateTime          = 时间：
LogDetails_Field_ThreadId          = 线程 ID：
LogDetails_Field_Filename          = 文件名：
LogDetails_Field_Function          = 函数：
LogDetails_Field_Line              = 行号：
LogDetails_Tab_Text                = 文本
LogDetails_Tab_Html                = HTML
LogDetails_Btn_Close               = 关闭
```

### Progress / UpdateProgress windows

```
Progress_WindowTitle               = 请稍候…
Progress_DefaultLabel              = 加载中…
Progress_LabelSuffix               = 请稍候…                                          # appended to the caller-supplied label on a new line
Progress_BtnCancel                 = 取消
Progress_BtnCancelling             = 正在取消…

UpdateProgress_WindowTitle         = 正在更新 CSUploader
UpdateProgress_StatusInitial       = 准备中…
UpdateProgress_StatusDownloading_Format = 正在下载更新 v{0}…                           # {0} = available semver
UpdateProgress_StatusRestarting    = 正在重启…
UpdateProgress_StatusFailed_Format = 更新失败：{0}                                      # {0} = exception.Message
UpdateProgress_PercentInitial      = 0%
```

### ProxyText dialog

```
ProxyText_WindowTitle              = 代理                                                 # XAML default — overridden via ctor with Import/Export titles
ProxyText_BtnImport                = 导入
ProxyText_BtnCopy                  = 复制
ProxyText_BtnCancel                = 取消
ProxyText_BtnClose                 = 关闭                                                   # replaces Cancel in read-only export mode
```

### SpeedLimit dialog

```
SpeedLimit_WindowTitle             = 速度限制
SpeedLimit_Heading                 = 设置速度限制
SpeedLimit_Subheading              = 单包覆盖设置。留空则使用全局设置。
SpeedLimit_Unit                    = KB/s
SpeedLimit_BtnClear                = 清除
SpeedLimit_BtnCancel               = 取消
SpeedLimit_BtnOk                   = 确定
SpeedLimit_Validation_Title        = 无效值
SpeedLimit_Validation_Message      = 请输入正整数（KB/s），或留空以清除。
```

---

## Status / inline messages

These are short status strings shown in non-dialog inline UI. Several have already been
listed in their owning section above; this section catches the rest, plus dialog-service
default titles.

```
Dialog_DefaultErrorTitle           = 错误
Dialog_DefaultConfirmTitle         = 确认
Dialog_DefaultBrowseFolderTitle    = 选择文件夹
Dialog_GenericErrorTitle           = 错误                                                   # ProgressWindow exception fallback

# File-picker filters (Microsoft.Win32 OpenFileDialog / SaveFileDialog)
Picker_Filter_Json                 = JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*
Picker_Filter_ProxyLists           = 代理列表 (*.txt)|*.txt|所有文件 (*.*)|*.*
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
Tray_Menu_Show                     = 显示 CSUploader                                         # "Show " + brand; localise the verb only
Tray_Menu_Exit                     = 退出
Tray_Balloon_Title                 = CSUploader                                              # brand — do not translate
Tray_Balloon_Body                  = 仍在托盘中运行。点击图标以恢复窗口，或右击选择"退出"。
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

# i18n インベントリ — ユーザー向け UI 文字列

このファイルは CSUploader WPF アプリでユーザーに表示されるすべての英語文字列を、
`Key = "日本語訳"` の形式で機能領域別にまとめたものです。各エントリは、マークアップ拡張への
移行が整い次第、ResX に移されます。

凡例:

- `_Format` サフィックスは、`{0}` / `{1}` プレースホルダーを含む文字列を示します。末尾の
  `# {0} = …` コメントは各プレースホルダーの意味を表します。
- ブランド名 (CSUploader、Rapidgator、JDownloader 2、Velopack、.NET、EF Core、SQLite、
  MIT、GitHub) はそのまま維持します — リソース化の際にも翻訳しないでください。
- ログメッセージ、例外メッセージ、DB 安定識別子文字列 (SettingKey、ConfirmationKeys 定数)、
  書式/カルチャパターン (`yyyy-MM-dd`、`F0`、`N0`)、URL、リソースキーは意図的に除外しています。
- `# review` コメントは、ユーザー向けかどうかが微妙なエントリに付いています。

---

## Common

```
Common_OK                         = OK
Common_Cancel                     = キャンセル
Common_Yes                        = はい
Common_No                         = いいえ
Common_Save                       = 保存
Common_Add                        = 追加
Common_Remove                     = 削除
Common_Close                      = 閉じる
Common_Edit                       = 編集
Common_Delete                     = 削除
Common_Browse                     = 参照
Common_Back                       = 戻る
Common_Next                       = 次へ
Common_Apply                      = 適用
Common_Refresh                    = 更新
Common_Details                    = 詳細
Common_Copy                       = コピー
Common_Import                     = インポート
Common_Export                     = エクスポート
Common_All                        = すべて
Common_None                       = なし
Common_Test                       = テスト
Common_Confirm                    = 確認
Common_Error                      = エラー
Common_Warning                    = 警告
Common_SelectFolder               = フォルダーを選択
Common_PleaseWait                 = お待ちください…
Common_Loading                    = 読み込み中…
Common_Cancelling                 = キャンセル中…
Common_Preparing                  = 準備中…
Common_Unknown                    = 不明
Common_Context_Copy               = Copy
Common_Context_OpenUrl            = Open URL
```

`Common_Save` は、設定 → 接続ページの保存ボタン、アカウント編集ダイアログの保存ボタン、
確認/編集ダイアログで再利用されます。コンテキストごとに異なる訳語が必要な場合は、
以下のセクション別キーから `Settings_Save`、`Account_Save` などを追加してください。

---

## MainWindow / メニュー

```
Main_Title                        = CSUploader
Main_Title_UpdateAvailable_Format = CSUploader — 更新あり (v{0}) — ヘルプ → 更新をインストール をクリック     # {0} = available semver
Main_Menu_File                    = ファイル(_F)
Main_Menu_File_Exit               = 終了(_E)
Main_Menu_File_Exit_Gesture       = Alt+F4
Main_Menu_View                    = 表示(_V)
Main_Menu_View_UploadOverview     = アップロード概要(_O)
Main_Menu_View_DarkMode           = ダークモード(_D)
Main_Menu_View_LightMode          = ライトモード(_L)
Main_Menu_Help                    = ヘルプ(_H)
Main_Menu_Help_CheckForUpdates    = 更新を確認(_C)…
Main_Menu_Help_InstallUpdate      = 更新をインストール(_I)
Main_Menu_Help_About              = CSUploader について(_A)

Main_Tab_Uploads                  = アップロード
Main_Tab_Uploaded                 = 履歴
Main_Tab_Settings                 = 設定
Main_Tab_Logs                     = ログ

Main_CheckForUpdates_DialogTitle  = 更新を確認
Main_CheckForUpdates_AlreadyLatest = 最新バージョンを使用中です。
Main_CheckForUpdates_Available_Format = 更新があります: v{0}。\n\nダウンロードとインストールには ヘルプ → 更新をインストール を使用してください。   # {0} = available semver
```

---

## アップロードタブ

ツールバー、コンテキストメニュー、列、概要パネル、フィルター、フッターリンク。

```
Uploads_Toolbar_AddTip            = 新しいアップロードを追加
Uploads_Toolbar_StartTip          = すべてのアップロードを開始
Uploads_Toolbar_PauseTip          = 一時停止 / 再開
Uploads_Toolbar_StopTip           = すべてのアップロードを停止
Uploads_Toolbar_MoveUpTip         = パッケージを上へ移動
Uploads_Toolbar_MoveDownTip       = パッケージを下へ移動
Uploads_Toolbar_RemoveTip         = 選択項目を削除

Uploads_Context_Start             = 開始
Uploads_Context_Stop              = 停止
Uploads_Context_SkipUpload        = アップロードをスキップ
Uploads_Context_Reset             = リセット
Uploads_Context_OpenSourceDir     = ソースディレクトリを開く
Uploads_Context_SetSpeedLimit     = 速度制限を設定…
Uploads_Context_Remove            = 削除

Uploads_Col_Name                  = 名前
Uploads_Col_Size                  = サイズ
Uploads_Col_Hoster                = ホスター
Uploads_Col_Status                = ステータス
Uploads_Col_Speed                 = 速度
Uploads_Col_ETA                   = 残り時間
Uploads_Col_BytesLoaded           = 読み込み済みバイト数
Uploads_Col_Progress              = 進捗
Uploads_Col_SaveTo                = 保存先
Uploads_Col_Added                 = 追加日時
Uploads_Col_Finished              = 完了日時
Uploads_Col_Duration              = 所要時間
Uploads_Col_Priority              = 優先度
Uploads_Col_SpeedLimit            = 速度制限
Uploads_Col_URL                   = URL
Uploads_Col_Hash                  = ハッシュ
Uploads_Col_Error                 = エラー

Uploads_ColumnHeader_LockTip      = 列幅をロック
Uploads_ColumnMenu_Reset          = 列をリセット
Uploads_ColumnMenu_DefaultLabel   = 列                                              # fallback when a column has no header text

Uploads_Overview_Title            = アップロード概要
Uploads_Overview_CloseTip         = 概要を閉じる
Uploads_Overview_ToggleTip        = 概要統計の表示 / 非表示

Uploads_Overview_Packages         = パッケージ
Uploads_Overview_Links            = リンク
Uploads_Overview_TotalBytes       = 総バイト数
Uploads_Overview_Uploadspeed      = アップロード速度
Uploads_Overview_BytesLoaded      = 読み込み済みバイト数
Uploads_Overview_RemainingBytes   = 残りバイト数
Uploads_Overview_Eta              = 残り時間
Uploads_Overview_RunningUploads   = 実行中のアップロード
Uploads_Overview_OpenConnections  = 接続中の接続数
Uploads_Overview_FinishedLinks    = 完了したリンク
Uploads_Overview_SkippedLinks     = スキップされたリンク
Uploads_Overview_FailedLinks      = 失敗したリンク

# Same labels appear inline with a trailing colon. Splitting them so a translator can
# control whether the colon is part of the term (typographic spacing differs in CJK).
Uploads_Overview_PackagesLabel        = パッケージ:
Uploads_Overview_LinksLabel           = リンク:
Uploads_Overview_TotalBytesLabel      = 総バイト数:
Uploads_Overview_UploadspeedLabel     = アップロード速度:
Uploads_Overview_BytesLoadedLabel     = 読み込み済みバイト数:
Uploads_Overview_RemainingBytesLabel  = 残りバイト数:
Uploads_Overview_EtaLabel             = 残り時間:
Uploads_Overview_RunningUploadsLabel  = 実行中のアップロード:
Uploads_Overview_OpenConnectionsLabel = 接続中の接続数:
Uploads_Overview_FinishedLinksLabel   = 完了したリンク:
Uploads_Overview_SkippedLinksLabel    = スキップされたリンク:
Uploads_Overview_FailedLinksLabel     = 失敗したリンク:

Uploads_FilterLabel               = ファイル名
Uploads_FilterTip                 = ファイル名またはパッケージ名で絞り込み
Uploads_FooterPremiumLink         = プレミアムアカウントを追加…

# Item-state names rendered in the Status column (FileStateDisplayConverter).
Uploads_State_Idle                = 待機中
Uploads_State_HashQueued          = ハッシュ待機中
Uploads_State_Hashing             = ハッシュ計算中
Uploads_State_UploadQueued        = アップロード待機中
Uploads_State_Uploading           = アップロード中
Uploads_State_Completed           = 完了
Uploads_State_Failed              = 失敗
Uploads_State_Paused              = 一時停止中
Uploads_State_Cancelled           = キャンセル済み

# ETA fallback (UploadsViewModel)
Uploads_Eta_NotApplicable         = ~

# Reset-columns confirmation (different wording per tab)
Uploads_ResetColumns_Title        = 列をリセット
Uploads_ResetColumns_Message      = アップロードタブの列を既定値にリセットしますか? 設定したカスタムの表示/非表示や並び順がすべてクリアされます。

# Remove confirmation prompts (UploadsViewModel)
Uploads_Remove_Title              = 削除
Uploads_Remove_Package_Format     = パッケージ「{0}」とその {1} 個のファイルを削除しますか?                            # {0} = package name, {1} = file count
Uploads_Remove_File_Format        = アップロードリストから「{0}」を削除しますか?                                   # {0} = file name
Uploads_Remove_Generic            = この項目を削除しますか?
Uploads_Remove_PackagesOnly_Format     = パッケージを {0} 個 ({1} 個のファイル) 削除しますか?                            # {0} = package count, {1} = total file count
Uploads_Remove_FilesOnly_Format        = アップロードリストから {0} 個のファイルを削除しますか?                       # {0} = file count
Uploads_Remove_PackagesAndFiles_Format = パッケージを {0} 個とファイルを {1} 個 (合計 {2} 項目) 削除しますか?      # {0} = packages, {1} = loose files, {2} = total
```

---

## アップロード済みタブ

```
Uploaded_Toolbar_ExportJson       = JSON にエクスポート…
Uploaded_Toolbar_ExportJsonTip    = 完了したアップロードをすべてのフィールドとともに JSON ファイルにエクスポート

Uploaded_Col_Name                 = 名前
Uploaded_Col_Path                 = パス
Uploaded_Col_Size                 = サイズ
Uploaded_Col_Hoster               = ホスター
Uploaded_Col_Finished             = 完了日時
Uploaded_Col_URL                  = URL
Uploaded_Col_Hash                 = ハッシュ

Uploaded_Context_Copy             = コピー
Uploaded_Context_Copy_Gesture     = Ctrl+C
Uploaded_Context_CopyURL          = URL をコピー
Uploaded_Context_Remove           = 削除
Uploaded_Context_Remove_Gesture   = Del
Uploaded_Context_ExportJson       = JSON にエクスポート…

# Reset-columns confirmation
Uploaded_ResetColumns_Title       = 列をリセット
Uploaded_ResetColumns_Message     = 履歴タブの列を既定値にリセットしますか? 設定したカスタムの表示/非表示や並び順がすべてクリアされます。

# Remove confirmation
Uploaded_Remove_Title             = 削除
Uploaded_Remove_Single_Format     = 履歴から「{0}」を削除しますか?                                          # {0} = file name
Uploaded_Remove_Many_Format       = 履歴から {0} 件のエントリを削除しますか?                                    # {0} = entry count
```

---

## 設定 — 一般

```
Settings_Sidebar_General          = 一般
Settings_Sidebar_Upload           = アップロード
Settings_Sidebar_Connection       = 接続
Settings_Sidebar_Accounts         = アカウント

Settings_General_Language_Title        = 言語
Settings_General_Language_Desc         = UI の言語です。変更は即座に適用されます。
Settings_General_Language_Label        = 言語

Settings_General_Developer_Title       = 開発者向け
Settings_General_Developer_Desc        = ローカル開発とテスト用のオプションです。
Settings_General_UseMockServer         = モックサーバーを使用 (すべてのファイルホスターリクエストを localhost:8080/<hoster> へリダイレクト)   # review — hidden behind a developer flag, may stay English

Settings_General_GridAppearance_Title  = グリッドの外観
Settings_General_GridAppearance_Desc   = アップロードタブと履歴タブで使用するフォントです。変更は即座に適用されます。
Settings_General_GridFont              = グリッドフォント
Settings_General_GridFontSize          = グリッドフォントサイズ

Settings_General_WindowBehaviour_Title = ウィンドウの動作
Settings_General_WindowBehaviour_Desc  = メインウィンドウを最小化または閉じたときの動作を選択します。
Settings_General_MinimizeToTray        = メインウィンドウをタスクバーではなくシステムトレイに最小化する
Settings_General_CloseAction           = 閉じるボタンの動作

Settings_General_CloseAction_Ask         = 毎回確認する
Settings_General_CloseAction_MinToTray   = トレイに最小化
Settings_General_CloseAction_Exit        = アプリケーションを終了

Settings_General_Notifications_Title  = 通知
Settings_General_Notifications_Desc   = アップロード完了時に画面右下にポップアップを表示します。
Settings_General_ShowCompletionToasts = アップロード完了時にポップアップ通知を表示

Settings_General_ConfirmationPrompts_Title = 確認プロンプト
Settings_General_ConfirmationPrompts_Desc  = チェックを入れると操作前に再度確認します。チェックを外すと、その操作の確認が省略されます。

Settings_General_Database_Title            = データベース
Settings_General_Database_Desc             = CSUploader はアップロード履歴 (パッケージ、ファイル、URL) をローカルの SQLite データベースに保存します。「アップロード」または「履歴」タブから項目を削除しても非表示になるだけで、行はデータベースに残ります。「クリア」をクリックすると、両方のタブで非表示になっている行を完全に削除します。
Settings_General_Database_BtnClear         = クリア
Settings_General_Database_ConfirmTitle     = データベースをクリア
Settings_General_Database_ConfirmMessage   = 両方のタブで非表示になっているアップロード記録を完全に削除しますか？\n\n進行中および表示中のアップロードには影響しません。この操作は取り消せません。
Settings_General_Database_Status_Cleared_Format    = データベースからファイル {0} 件、パッケージ {1} 件をクリアしました。
Settings_General_Database_Status_NothingToClear    = クリアできる非表示の行はありません。
Settings_General_Database_BtnClearLogs              = Clear logs
Settings_General_Database_ConfirmClearLogsTitle     = Clear log history
Settings_General_Database_ConfirmClearLogsMessage   = Permanently delete all log entries from the database?\n\nThis affects only the persisted history loaded on startup. The Logs tab keeps showing this session's entries until you close the app. This cannot be undone.
Settings_General_Database_LogsCleared_Format        = Cleared {0} log entr(ies) from the database.
Settings_General_Database_LogsNothingToClear        = No log entries to clear.
```

---

## Toast notifications

```
Toast_FileCompleted_Title    = アップロード完了
Toast_FileCompleted_Body     = {0}
Toast_PackageCompleted_Title = パッケージのアップロード完了
Toast_PackageCompleted_Body  = {1} 件中 {0} 件のファイルをアップロード — {2}
```

---

## 設定 — アップロード

```
Settings_Upload_Mgmt_Title         = アップロード管理
Settings_Upload_Mgmt_Desc          = 接続上限、優先度など、アップロードコントローラーの詳細を設定します。
Settings_Upload_MaxConcurrent      = 同時アップロード最大数
Settings_Upload_MaxPerHoster       = ファイルホスターごとの同時アップロード最大数
Settings_Upload_RemoveFinished     = 完了したアップロードを削除
Settings_Upload_IfFileExists       = ファイルが既に存在する場合
Settings_Upload_MaxCpuJobs         = 同時 CPU ジョブ最大数
Settings_Upload_SpeedLimit         = 速度制限 (KB/s)

Settings_Upload_RemoveFinished_Never              = しない
Settings_Upload_RemoveFinished_Immediately        = すぐに
Settings_Upload_RemoveFinished_AtStartup          = 起動時に
Settings_Upload_RemoveFinished_WhenPackageReady   = パッケージ完了時に

Settings_Upload_IfExists_Ask                      = ファイルごとに確認
Settings_Upload_IfExists_Skip                     = スキップ
Settings_Upload_IfExists_Overwrite                = 上書き
Settings_Upload_IfExists_Rename                   = 名前変更

Settings_Upload_Autostart_Title                   = アップロードの自動開始
Settings_Upload_Autostart_Desc                    = CSUploader が保留中のアップロードをユーザー操作なしで開始するかどうか、またそのタイミングを選択します。
Settings_Upload_Autostart                         = アプリ起動時にアップロードを自動開始

Settings_Upload_Autostart_Always                  = 常に
Settings_Upload_Autostart_OnlyIfRunning           = 前回終了時にアップロードが実行中だった場合のみ
Settings_Upload_Autostart_Never                   = しない
```

---

## 設定 — 接続 (プロキシマネージャー)

```
Settings_Conn_Title                = 接続マネージャー
Settings_Conn_Desc                 = インターネットへのアクセスにプロキシが必要な場合は、ここで設定します。複数のプロキシは新しいアップロードごとにローテーションされます。有効なプロキシがない場合の既定動作は直接接続です。
Settings_Conn_UseProxies           = アップロードでプロキシを使用
Settings_Conn_UseProxiesTip        = ローテーションのマスタースイッチです。オフの場合、グリッドにプロキシがあっても直接接続されます — プロキシを追加してテストしたいが、まだ実際の使用にはコミットしたくないときに便利です。
Settings_Conn_AutoDisable          = 失敗したプロキシのチェックを自動的に外す
Settings_Conn_AutoDisableTip       = オンにすると、手動テストやアップロードに失敗したプロキシのチェックが外され、ローテーションでスキップされます。ステータスアイコンはどちらの場合でも更新されます。

Settings_Conn_Col_On               = 有効
Settings_Conn_Col_Priority         = 優先度
Settings_Conn_Col_Type             = 種類
Settings_Conn_Col_Host             = ホスト / IP
Settings_Conn_Col_Port             = ポート
Settings_Conn_Col_User             = ユーザー
Settings_Conn_Col_Password         = パスワード
Settings_Conn_Col_Test             = テスト
Settings_Conn_Col_Status           = ステータス

Settings_Conn_PriorityUpTip        = 上へ移動 (優先度を上げる)
Settings_Conn_PriorityDownTip      = 下へ移動 (優先度を下げる)

Settings_Conn_Context_Test         = テスト
Settings_Conn_Context_Remove       = 削除
Settings_Conn_Context_Remove_Gesture = Del

Settings_Conn_Btn_Import           = インポート
Settings_Conn_Btn_Import_FromText  = テキストからインポート…
Settings_Conn_Btn_Import_FromFile  = ファイルからインポート…
Settings_Conn_Btn_Export           = エクスポート
Settings_Conn_Btn_Export_AllToText = すべてのプロキシをテキストにエクスポート…
Settings_Conn_Btn_Export_AllToFile = すべてのプロキシをファイルにエクスポート…
Settings_Conn_Btn_Export_OkToText  = テスト成功のプロキシをテキストにエクスポート…
Settings_Conn_Btn_Export_OkToFile  = テスト成功のプロキシをファイルにエクスポート…
Settings_Conn_Btn_Save             = 保存
Settings_Conn_Btn_Add              = 追加
Settings_Conn_Btn_Remove           = 削除
Settings_Conn_Btn_RemoveSelected   = 選択項目を削除
Settings_Conn_Btn_RemoveFailed     = 失敗したものを削除
Settings_Conn_Btn_TestAll          = すべてテスト
Settings_Conn_Btn_TestAllTip       = リスト内のすべてのプロキシの接続性をテストします
Settings_Conn_Btn_Details          = 詳細

# Proxy import/export dialogs (ProxyTextDialog ctor args, ConnectionManagerViewModel)
Settings_Conn_ImportProxies_FileDialogTitle = プロキシをインポート
Settings_Conn_ImportProxies_FileFilter      = プロキシリスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*
Settings_Conn_ImportProxies_DialogTitle     = プロキシをインポート
Settings_Conn_ImportProxies_DialogDesc      = プロキシ行を貼り付けてください (1 行に 1 つ)。形式: scheme://[user:pass@]host[:port] — ポートはスキームに応じて 80/443/1080 が既定値になります。
Settings_Conn_ExportAll_DialogTitle         = すべてのプロキシをエクスポート
Settings_Conn_ExportOk_DialogTitle          = テスト成功のプロキシをエクスポート
Settings_Conn_ExportAll_Desc_Format         = プロキシ {0} 個:                                  # {0} = count of all proxies
Settings_Conn_ExportOk_Desc_Format          = 直近のテストに成功したプロキシ {0} 個:      # {0} = count of OK proxies

# Proxy remove confirmations (ConnectionManagerViewModel)
Settings_Conn_RemoveProxy_Title             = プロキシを削除
Settings_Conn_RemoveProxy_One_Format        = プロキシ「{0}:{1}」を削除しますか?                        # {0} = host, {1} = port
Settings_Conn_RemoveProxy_Many_Format       = プロキシを {0} 個削除しますか?                            # {0} = count
Settings_Conn_RemoveFailedProxy_Title       = 失敗したプロキシを削除
Settings_Conn_RemoveFailedProxy_One_Format  = 失敗したプロキシ「{0}:{1}」を削除しますか?             # {0} = host, {1} = port
Settings_Conn_RemoveFailedProxy_Many_Format = 失敗したプロキシを {0} 個削除しますか?                     # {0} = count

# Proxy test/save status strings (ConnectionManagerViewModel / ProxySettingItem)
Settings_Conn_Status_Queued                 = 待機中…
Settings_Conn_Status_Testing                = テスト中…
Settings_Conn_Status_OkLive                 = OK (ライブ)
Settings_Conn_Status_OkLatencyIp_Format     = OK {0}ms ({1})                                 # {0} = ms, {1} = detected IP
Settings_Conn_Status_OkLatencyUnknown_Format = OK {0}ms (予期しない応答)                # {0} = ms
Settings_Conn_Status_Failed_Format          = 失敗: {0}                                    # {0} = error first line / message
Settings_Conn_Status_Saved                  = 保存しました
Settings_Conn_Status_SaveFailed_Format      = 保存に失敗しました: {0}                               # {0} = error message
Settings_Conn_Status_ImportedNeedsSave_Format = プロキシを {0} 個インポートしました — 保存をクリックして反映してください  # {0} = proxy count
Settings_Conn_Status_ExportedToFile_Format  = プロキシ {0} 個を {1} にエクスポートしました                   # {0} = count, {1} = file name
```

---

## 設定 — アカウント

```
Settings_Accounts_Title            = アカウントマネージャー
Settings_Accounts_Desc             = プレミアム/ゴールド/プラチナアカウントを入力・管理します。

Settings_Accounts_Col_Enabled      = ✓                                                       # check-mark glyph header — leave as glyph or localize as e.g. "On"
Settings_Accounts_Col_Hoster       = ホスター
Settings_Accounts_Col_Status       = ステータス
Settings_Accounts_Col_Username     = ユーザー名
Settings_Accounts_Col_Password     = パスワード
Settings_Accounts_Col_Type         = 種類

Settings_Accounts_Context_Edit     = アカウントを編集…
Settings_Accounts_Context_Refresh  = チェック / 更新
Settings_Accounts_Context_Enable   = 有効化
Settings_Accounts_Context_Disable  = 無効化
Settings_Accounts_Context_Delete   = 削除

Settings_Accounts_Btn_Add          = 追加
Settings_Accounts_Btn_Remove       = 削除
Settings_Accounts_Btn_Refresh      = 更新

# Account remove / validation
Settings_Accounts_Remove_Title             = アカウントを削除
Settings_Accounts_Remove_Message_Format    = {1} のアカウント「{0}」を削除しますか?                  # {0} = username, {1} = file hoster name

Settings_Accounts_Validation_FillHosterUser = ファイルホスター、ユーザー名、パスワードを入力してください。
Settings_Accounts_Check_DialogTitle         = アカウントチェック
Settings_Accounts_Check_FailedAddAnyway_Format = アカウントチェックに失敗しました: {0}\n\nそれでも追加しますか?    # {0} = error message
Settings_Accounts_Check_CouldNotVerifyAddAnyway_Format = アカウントを検証できませんでした: {0}\n\nそれでも追加しますか?   # {0} = error message

# CheckAccountStatus inline status messages
Settings_Accounts_Status_Verifying          = 認証情報を検証中…
Settings_Accounts_Status_Checking           = アカウントをチェック中…
Settings_Accounts_Status_NoAccountsToRefresh = 更新するアカウントがありません。
Settings_Accounts_Status_Verified_Format    = 検証済み: {0}                                  # {0} = result.Message
Settings_Accounts_Status_Warning_Format     = 警告: {0}                                   # {0} = result.Message
Settings_Accounts_Status_Valid_Format       = 有効: {0}                                     # {0} = result.Message
Settings_Accounts_Status_ValidExclaim_Format = 有効です! {0}                                    # {0} = result.Message  (separate from above — different exclamation)
Settings_Accounts_Status_Failed_Format      = 失敗: {0}                                    # {0} = result.Message
Settings_Accounts_Status_CheckError_Format  = チェックエラー: {0}                               # {0} = exception.Message
Settings_Accounts_Status_Error_Format       = エラー: {0}                                     # {0} = exception.Message
Settings_Accounts_Status_AccountAdded_Format = {0} のアカウントを追加しました!                        # {0} = file hoster name
Settings_Accounts_Status_NoImpl_Format      = {0} の実装がありません。チェックできません。       # {0} = file hoster name
Settings_Accounts_Status_NoImplWillSave_Format = {0} の実装がありません。アカウントは検証なしで保存されます。   # {0} = file hoster name
Settings_Accounts_Status_CheckingProgress_Format = {0}@{1} をチェック中… ({2}/{3})             # {0} = username, {1} = hoster, {2} = current, {3} = total
Settings_Accounts_Status_CheckingShort      = チェック中…
Settings_Accounts_Status_NoImpl             = 実装なし
Settings_Accounts_Status_RefreshSummary_Format = {0} 個のアカウントを更新しました。{1} 個が更新されました。        # {0} = checked count, {1} = updated count
Settings_Accounts_Status_AccountDisabled_Format = アカウント「{0}」を無効化しました。                    # {0} = username
Settings_Accounts_Status_AccountEnabled_Format  = アカウント「{0}」を有効化しました。                     # {0} = username

# AccountCheckResult fallback strings (SettingsViewModel)
Settings_Accounts_DefaultStatus_OK        = OK
Settings_Accounts_DefaultStatus_Failed    = 失敗

# Password column placeholder
Settings_Accounts_PasswordMask            = ******
```

---

## ログタブ

```
Logs_AutoScroll                    = 自動スクロール
Logs_BtnClear                      = Clear
Logs_Tab_Status                    = ステータス
Logs_Tab_Http                      = HTTP
Logs_Tab_Errors                    = エラー
Logs_Tab_UI                        = UI

Logs_Col_DateTime                  = 日時
Logs_Col_Filename                  = ファイル名
Logs_Col_Function                  = 関数
Logs_Col_Line                      = 行
Logs_Col_Message                   = メッセージ
Logs_Col_Thread                    = スレッド

# Status messages logged to the Status tab (UploadedViewModel) — these surface to the
# user via the Logs tab so they're worth localising.
Logs_Status_NoUrlsClipboardCleared = 選択範囲に URL がありません。クリップボードをクリアしました
Logs_Status_CopiedUrls_Format      = URL を {0} 件クリップボードにコピーしました                          # {0} = url count
Logs_Status_HiddenFiles_Format     = 履歴タブから {0} 個のファイルを非表示にしました                   # {0} = file count
Logs_Status_ExportedPackages_Format = パッケージ {0} 個を {1} にエクスポートしました                         # {0} = pkg count, {1} = file path
```

---

## アップロードウィザード

```
Wizard_Title                       = アップロードウィザード

Wizard_Step_Directory              = 1. ディレクトリ
Wizard_Step_Files                  = 2. ファイル
Wizard_Step_FileHosters            = 3. ファイルホスター
Wizard_Step_Start                  = 4. 開始

Wizard_Step0_Title                 = アップロードディレクトリを選択
Wizard_Step0_Desc                  = アップロードしたいファイルを含むディレクトリを選択してください。
Wizard_Step0_Browse                = 参照
Wizard_Step0_BrowseDialogTitle     = アップロードディレクトリを選択                                 # used when calling BrowseFolder

Wizard_Step1_Title                 = ファイルを選択
Wizard_Step1_PackageTitleLabel     = パッケージタイトル:
Wizard_Step1_FilterLabel           = フィルター:
Wizard_Step1_BtnAll                = すべて
Wizard_Step1_BtnNone               = なし
Wizard_Step1_Col_File              = ファイル
Wizard_Step1_Col_Size              = サイズ
Wizard_Step1_SelectedLabel         = 選択中:
Wizard_Step1_FilesUnit             = 個のファイル

Wizard_Step2_Title                 = ファイルホスターを選択
Wizard_Step2_Desc                  = アップロード先のファイルホスターを選択し、アカウントを指定してください。
Wizard_Step2_Col_Use               = 使用
Wizard_Step2_Col_FileHoster        = ファイルホスター
Wizard_Step2_Col_Account           = アカウント
Wizard_Step2_AccountAnonymous      = (匿名)
Wizard_Step2_AccountSelect         = (アカウントを選択)
Wizard_Step2_AddAccountLink        = アカウントを追加…
Wizard_Step2_AccountRequiredTooltip = このホスターはアカウントが必要です。「アカウントを追加…」をクリックして追加してください。

Wizard_Step3_Title                 = 開始タイミング
Wizard_Step3_Desc                  = アップロードを開始するタイミングを選択してください。
Wizard_Step3_Mode_Immediately      = ウィザードを閉じた直後に開始
Wizard_Step3_Mode_Later            = キューに追加して後で開始 (手動開始)
Wizard_Step3_Mode_Scheduled        = 指定した日時にスケジュール
Wizard_Step3_TimeFormatHint        = (HH:mm)

Wizard_Btn_Back                    = 戻る
Wizard_Btn_Cancel                  = キャンセル
Wizard_Btn_Next                    = 次へ
Wizard_Btn_Add                     = 追加

# Validation errors (UploadWizardViewModel.ShowError)
Wizard_Validation_PickValidDir     = 有効なディレクトリを選択してください。
Wizard_Validation_PickFile         = ファイルを 1 つ以上選択してください。
Wizard_Validation_PickHoster       = ファイルホスターを 1 つ以上選択してください。
Wizard_Error_Format                = エラー: {0}                                              # {0} = exception.Message
```

---

## 確認プロンプト (設定 → 一般のリストラベル)

これらは `ConfirmationKeys.All` のユーザー向けラベル — 設定の「確認プロンプト」セクションに
表示される文字列です。安定 ID (`remove-upload-package-or-file` など) は英語のままです。

```
Confirm_RemoveUploadPackageOrFile  = アップロードタブからパッケージまたはファイルを削除
Confirm_RemoveUploadedEntry        = 履歴タブからエントリを削除
Confirm_RemoveFileHosterAccount    = ファイルホスターアカウントを削除
Confirm_RemoveProxy                = 接続マネージャーからプロキシを削除
Confirm_ResetColumns               = アップロード / 履歴タブの列を既定値にリセット
```

---

## ダイアログウィンドウ

### バージョン情報

```
About_WindowTitle                  = CSUploader について
About_AppName                      = CSUploader
About_Version_Format               = バージョン {0}                                             # {0} = assembly version, e.g. "1.2.3"
About_Description                  = 複数のホスティングサービスに対応する強力なファイルアップロードマネージャーです。ハッシュ計算、キュー管理、リアルタイムの進捗追跡などの機能を備えています。
About_Field_Framework              = フレームワーク:
About_Field_Framework_Value        = .NET 10.0 (WPF)
About_Field_Database               = データベース:
About_Field_Database_Value         = SQLite via EF Core 10
About_Field_License                = ライセンス:
About_Field_License_Value          = MIT
About_Field_Source                 = ソース:
About_OK                           = OK
```

### CloseAction ダイアログ

```
CloseAction_WindowTitle            = CSUploader を閉じる
CloseAction_Heading                = 閉じるボタンの動作をどのようにしますか?
CloseAction_Subheading             = いずれかを選択してください — 後で 設定 → 一般 から変更できます。
CloseAction_Remember               = この選択を記憶する
CloseAction_BtnMinimize            = トレイに最小化
CloseAction_BtnExit                = 終了
CloseAction_BtnCancel              = キャンセル
```

### 確認ダイアログ

```
Confirmation_WindowTitle           = 確認
Confirmation_DontAskAgain          = この操作については今後確認しない
Confirmation_BtnYes                = はい
Confirmation_BtnNo                 = いいえ
```

### EditAccount ダイアログ

```
EditAccount_WindowTitle            = アカウントを編集
EditAccount_AddTitle               = アカウントを追加                                             # used by SettingsViewModel.AddAccountDialog
EditAccount_FileHosterLabel        = ファイルホスター:
EditAccount_UsernameLabel          = ユーザー名:
EditAccount_PasswordLabel          = パスワード:
EditAccount_AccountEnabled         = アカウントを有効化
EditAccount_BtnSave                = 保存
EditAccount_BtnCancel              = キャンセル
EditAccount_Validation_RequireUsernameAndPassword = ユーザー名とパスワードの両方を入力してください。
```

### HttpDetails ウィンドウ

```
HttpDetails_WindowTitle            = HTTP トランザクションの詳細
HttpDetails_Tab_Request            = リクエスト
HttpDetails_Tab_Response           = レスポンス
HttpDetails_Tab_FullDump           = 完全ダンプ
HttpDetails_SubTab_Headers         = ヘッダー
HttpDetails_SubTab_BodyRaw         = ボディ (Raw)
HttpDetails_SubTab_BodyJson        = ボディ (JSON)
HttpDetails_SubTab_Hex             = Hex

# Header strip
HttpDetails_Timing_Format          = 開始: {0}  |  所要時間: {1}ms  |  サイズ: {2} バイト    # {0} = HH:mm:ss.fff, {1} = ms, {2} = byte count
HttpDetails_Proxy_Format           = プロキシ: {0}                                              # {0} = proxy display string
HttpDetails_NoData                 = (データなし)
HttpDetails_NoBody                 = (ボディなし)
# Section dividers used in the Full Dump (these are framed in box-drawing chars; the
# label words are the only translatable parts).
HttpDetails_FullDump_Request       = リクエスト
HttpDetails_FullDump_Response      = レスポンス
```

### LogDetails ウィンドウ

```
LogDetails_WindowTitle             = ログの詳細
LogDetails_Field_DateTime          = 日時:
LogDetails_Field_ThreadId          = スレッド ID:
LogDetails_Field_Filename          = ファイル名:
LogDetails_Field_Function          = 関数:
LogDetails_Field_Line              = 行:
LogDetails_Tab_Text                = テキスト
LogDetails_Tab_Html                = HTML
LogDetails_Btn_Close               = 閉じる
```

### Progress / UpdateProgress ウィンドウ

```
Progress_WindowTitle               = お待ちください…
Progress_DefaultLabel              = 読み込み中…
Progress_LabelSuffix               = お待ちください…                                          # appended to the caller-supplied label on a new line
Progress_BtnCancel                 = キャンセル
Progress_BtnCancelling             = キャンセル中…

UpdateProgress_WindowTitle         = CSUploader を更新中
UpdateProgress_StatusInitial       = 準備中…
UpdateProgress_StatusDownloading_Format = 更新 v{0} をダウンロード中…                           # {0} = available semver
UpdateProgress_StatusRestarting    = 再起動中…
UpdateProgress_StatusFailed_Format = 更新に失敗しました: {0}                                      # {0} = exception.Message
UpdateProgress_PercentInitial      = 0%
```

### ProxyText ダイアログ

```
ProxyText_WindowTitle              = プロキシ                                                 # XAML default — overridden via ctor with Import/Export titles
ProxyText_BtnImport                = インポート
ProxyText_BtnCopy                  = コピー
ProxyText_BtnCancel                = キャンセル
ProxyText_BtnClose                 = 閉じる                                                   # replaces Cancel in read-only export mode
```

### SpeedLimit ダイアログ

```
SpeedLimit_WindowTitle             = 速度制限
SpeedLimit_Heading                 = 速度制限を設定
SpeedLimit_Subheading              = パッケージごとのオーバーライドです。空欄にすると全体設定が使用されます。
SpeedLimit_Unit                    = KB/s
SpeedLimit_BtnClear                = クリア
SpeedLimit_BtnCancel               = キャンセル
SpeedLimit_BtnOk                   = OK
SpeedLimit_Validation_Title        = 値が無効です
SpeedLimit_Validation_Message      = 正の整数 (KB/s) を入力するか、空欄のままでクリアしてください。
```

---

## ステータス / インラインメッセージ

非ダイアログのインライン UI に表示される短いステータス文字列です。一部は上記の所属セクションに
既に記載済みのため、ここではそれ以外と、ダイアログサービスの既定タイトルをまとめています。

```
Dialog_DefaultErrorTitle           = エラー
Dialog_DefaultConfirmTitle         = 確認
Dialog_DefaultBrowseFolderTitle    = フォルダーを選択
Dialog_GenericErrorTitle           = エラー                                                   # ProgressWindow exception fallback

# File-picker filters (Microsoft.Win32 OpenFileDialog / SaveFileDialog)
Picker_Filter_Json                 = JSON ファイル (*.json)|*.json|すべてのファイル (*.*)|*.*
Picker_Filter_ProxyLists           = プロキシリスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*
```

アップロードグリッドの速度制限列で `SpeedLimitConverter` が生成する速度制限の表示文字列:

```
SpeedLimit_Display_Mbps_Format     = {0} MB/s                                                # {0} = numeric value (formatted "0.##")
SpeedLimit_Display_Kbps_Format     = {0} KB/s                                                # {0} = integer KB/s
```

ゼロのときのアップロード概要 UploadSpeed 既定値 (`UploadsViewModel.UploadSpeed`):

```
Uploads_Overview_UploadSpeed_Zero  = 0 B/s
```

---

## トレイアイコンメニュー

`TrayIconManager` の文字列です。ツールチップテキスト「CSUploader」はブランド名のため、
ローカライズ時もそのまま維持します。

```
Tray_Tooltip                       = CSUploader                                              # brand — do not translate
Tray_Menu_Show                     = CSUploader を表示                                         # "Show " + brand; localise the verb only
Tray_Menu_Exit                     = 終了
Tray_Balloon_Title                 = CSUploader                                              # brand — do not translate
Tray_Balloon_Body                  = トレイで実行中です。アイコンをクリックするとウィンドウが復元され、右クリックで終了できます。
```

---

## 注記 / 曖昧なケース

レビュー対象としてフラグが付けられた項目:

- **`Settings_General_UseMockServer`** — ホスターリクエストを `localhost:8080` へリダイレクト
  する開発者/QA フラグに紐づくチェックボックスです。厳密には「Developer」グループは一般タブに
  公開されているので、エンドユーザーが目にする *可能性* があります。`# review` マーク付き。
  チームは通常通りローカライズするか、ビルドフラグの裏に隠すか、英語のまま残すかを判断できます。

- **`Settings_Accounts_Col_Enabled`** — 列ヘッダーは Unicode のチェックマーク文字 `✓` 自体です。
  一部のロケールでは「On」「啟用」のような短い単語のほうが好まれます。インベントリではそのまま
  残していますが、翻訳前に UX 判断を仰ぐ価値があります。

- **`Settings_Accounts_DefaultStatus_OK`** / **`_Failed`** — これらの文字列は
  `AccountCheckResult.Message ?? "OK"` / `?? "Failed"` のフォールバックから来ます。実際の結果
  メッセージは通常ホスタークライアント (例: RapidgatorClient) から渡され、別の文字列群
  (`"Failed to login"`、`"Failed to create folder"` など) になります。これらは現在
  `UploadFinishedCallback` とステータス列を介してそのままユーザーに渡されています。ログ/診断と
  ユーザー向けの境界に位置するため、リソース化するか開発者専用として扱うかをチームに判断委ねます。

- **`Wizard_Error_Format` (「Error: {0}」)** と、ダイアログ汎用の
  **`Dialog_DefaultErrorTitle` (「Error」)** — ダイアログタイトルが 1 単語のままでも、
  インラインの「Error: …」プレフィックスを翻訳者が自然に表現できるよう、別キーとして保持しています。

- 末尾コロン付きの短いラベル (`Package(s):`、`ETA:`) のいくつかは、コロン付きとコロンなしの両形式
  で重複しています。これは一部の翻訳ではコロンを用語自体に付けたい場合があるため (CJK の組版規則
  が異なる) です。すべてのロケールが同じ慣習を選んだ場合は、後で重複を解消します。

- `Common_Save` は、設定 → 接続の保存、アカウント編集の保存、確認/編集ダイアログの保存ボタンで
  再利用されます。3 つとも現在は同じ英語テキストでレンダリングされ、機能上分割する理由はありませんが、
  翻訳者から要望があった場合に備え、セクション別キー (`Settings_Conn_Btn_Save`、
  `EditAccount_BtnSave`) は既にリストに入っています。

- `Tray_Tooltip` / `Tray_Balloon_Title` はブランド名のみなので翻訳しないでください。

- `About_AppName`、`About_Field_Framework_Value`、`About_Field_Database_Value`、
  `About_Field_License_Value` はほぼすべて固有名詞/ブランド文字列です。マークアップ拡張パスで
  漏れないよう完全性のために含めましたが、翻訳者はそのまま残してください。

- アップロード済みタブの `Header="✓"` グリフや、ツールバーボタンの JD2 風上下シェブロン
  (`▲`、`▼`、`+`、`−`、`✕`、`▶`、`▼`) は純粋なグリフであり、ローカライズしません。

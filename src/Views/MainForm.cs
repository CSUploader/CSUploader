// <copyright file="MainForm.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using BrightIdeasSoftware;
using CSUploader.Controls.Models;
using CSUploader.Dal;
using CSUploader.Lib;

namespace CSUploader.Views
{
    /// <summary>
    /// Main form.
    /// </summary>
    /// <seealso cref="Form" />
    public partial class MainForm : Form
    {
        private readonly HashSet<TabPage> tabPageControlsLoaded = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="MainForm"/> class.
        /// </summary>
        public MainForm()
        {
            // Create controls
            InitializeComponent();

            // Add callbacks
            Load += MainForm_Load;

            tcMain.SelectedIndexChanged += TcMain_SelectedIndexChanged;
        }

        private object TextBoxLogLock { get; } = new object();

        private Dictionary<LogType, Tuple<FastObjectListView, List<LogListViewModel>>> LogObjectListViews { get; set; } = new();

        private Database Database => _db ?? throw new NullReferenceException(nameof(_db));
        private Database? _db = null;

        private async void MainForm_Load(object? sender, EventArgs e)
        {
            LogObjectListViews = new Dictionary<LogType, Tuple<FastObjectListView, List<LogListViewModel>>>
            {
                { LogType.Status, CreateLogObjectListView("Status") },
                { LogType.Http, CreateLogObjectListView("HTTP") },
                { LogType.Error, CreateLogObjectListView("Errors") },
                { LogType.UI, CreateLogObjectListView("UI") }
            };

            // Set callbacks first (so we can get logs)
            Logger.OnLogOutput += Log_OnLogOutput;

            // Load stuff
            _db = await ProgressForm.ExecuteAsync(this, "Initializing database...", false, (form, cancellationToken) =>
            {
                // Initialize database
                try
                {
                    return Task.FromResult<Database?>(FirstRun.InitializeDatabase());
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return Task.FromResult(null as Database);
                }
            });

            if (_db == null)
            {
                GUIHelper.Error(this, "Failed to load database.");
                Close();
                return;
            }

            Logger.Log(this, LogType.Status, $"Database initialized.");

            // Load settings
            await ProgressForm.ExecuteAsync(this, "Loading settings...", false, async (form, cancellationToken) =>
            {
                await LoadSettingsAsync(cancellationToken);
            });

            // Load previous uploads
            await ProgressForm.ExecuteAsync(this, "Loading previous uploads...", false, async (form, cancellationToken) =>
            {
                await LoadUploadedAsync(cancellationToken);
            });

            Logger.Log(this, LogType.Status, "Settings loaded.");

            if (!DidTabPageControlLoad(tpUploads))
            {
                MainForm_UploadsTabPage_Load(sender, e);
            }

            foreach (KeyValuePair<LogType, Tuple<FastObjectListView, List<LogListViewModel>>> objectListViewKeyValuePair in LogObjectListViews)
            {
                ObjectListView objectListView = objectListViewKeyValuePair.Value.Item1;

                int remainingWidth = objectListView.Width - 25;  // remove scrollbar
                remainingWidth = SetColumnWidth(objectListView, objectListView.AllColumns[0], 115, remainingWidth);
                remainingWidth = SetColumnWidth(objectListView, objectListView.AllColumns[1], 200, remainingWidth);
                remainingWidth = SetColumnWidth(objectListView, objectListView.AllColumns[2], 250, remainingWidth);
                remainingWidth = SetColumnWidth(objectListView, objectListView.AllColumns[3], 74, remainingWidth);
                remainingWidth = SetColumnWidth(objectListView, objectListView.AllColumns[5], 62, remainingWidth);
                SetColumnWidth(objectListView, objectListView.AllColumns[4], remainingWidth);
            }

            Logger.Log(this, LogType.Status, "MainForm loaded.");

            TcMain_SelectedIndexChanged(tcMain, e);
        }

        private void ObjectListViewLog_MouseDoubleClick(object? sender, MouseEventArgs e)
        {
            if (sender is not ObjectListView objectListView)
            {
                return;
            }

            if (objectListView.SelectedObject != null)
            {
                if (objectListView.SelectedObject is not LogListViewModel listViewLogItem)
                {
                    return;
                }

                // Don't block UI (don't show it as dialogue)
                LogDetailsForm logDetailsForm = new(listViewLogItem);
                logDetailsForm.Show();
            }
        }

        private void TcMain_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (sender is not TabControl tabControl)
            {
                return;
            }

            if (tabControl.SelectedTab == tpUpload)
            {
                if (!DidTabPageControlLoad(tpUpload))
                {
                    MainForm_UploadTabPage_Load(sender, e);
                }

                MainForm_UploadTabPage_Focus(sender, e);
            }
            else if (tabControl.SelectedTab == tpUploads)
            {
                if (!DidTabPageControlLoad(tpUploads))
                {
                    MainForm_UploadsTabPage_Load(sender, e);
                }

                MainForm_UploadsTabPage_Focus(sender, e);
            }
            else if (tabControl.SelectedTab == tpSettings)
            {
                if (!DidTabPageControlLoad(tpSettings))
                {
                    MainForm_SettingsTabPage_Load(sender, e);
                }

                MainForm_SettingsTabPage_Focus(sender, e);
            }
        }

        private void Log_OnLogOutput(object? sender, LogEvent e)
        {
            if (InvokeRequired)
            {
                Invoke((MethodInvoker)delegate { Log_OnLogOutput(sender, e); });
                return;
            }

            // Write the output.
            lock (TextBoxLogLock)
            {
                LogListViewModel listViewLogItem = new()
                {
                    LogType = e.LogType,
                    DateTime = e.DateTime,
                    Filename = e.Filename,
                    Function = e.Function,
                    LineNumber = e.LineNumber,
                    Message = e.Message,
                    ThreadId = e.ThreadId
                };
                AddLogListViewModel(listViewLogItem);
            }
        }

        private void AddLogListViewModel(LogListViewModel logListViewModel)
        {
            // Check listview to add item to
            if (!LogObjectListViews.TryGetValue(logListViewModel.LogType, out Tuple<FastObjectListView, List<LogListViewModel>>? kvp))
            {
                string? logTypeStr = Enum.GetName(typeof(LogType), logListViewModel.LogType);
                MessageBox.Show($"No list view found for log type `{logTypeStr}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Add item to listview
            kvp.Item2.Add(logListViewModel);
            kvp.Item1.SetObjects(kvp.Item2);

            if (cbLogAutoScroll.Checked)
            {
                // Auto scroll to last added entry
                kvp.Item1.EnsureModelVisible(logListViewModel);
            }
        }

        private static int SetColumnWidth(ObjectListView objectListView, OLVColumn column, int width, int? maxWidth = null)
        {
            objectListView.AllColumns.First(c => ReferenceEquals(c, column)).Width = width;
            return maxWidth.HasValue ? maxWidth.Value - width : width;
        }

        private bool DidTabPageControlLoad(TabPage tabPage)
        {
            return !tabPageControlsLoaded.Add(tabPage);
        }

        private Tuple<FastObjectListView, List<LogListViewModel>> CreateLogObjectListView(string name)
        {
            TabPage logTabPage = new(name);

            FastObjectListView fastObjectListView = new()
            {
                Name = $"objectListViewLog{name}",
                BackColor = Color.FromArgb(244, 252, 254),
                GridLines = true,
                Dock = DockStyle.Fill,
                FullRowSelect = true,
                LabelWrap = false,
                Location = new Point(3, 3),
                MultiSelect = false,
                Size = new Size(1255, 229),
                View = View.Details,
                HeaderFormatStyle = tlvHeaderFormatStyle,
                UseAlternatingBackColors = true,
                AlternateRowBackColor = Color.FromArgb(239, 246, 248),
                ShowGroups = false
            };

            fastObjectListView.AllColumns.AddRange(new[]
            {
                new OLVColumn
                {
                    Name = $"columnHeaderLog{name}DateTime",
                    Text = "Date/time",
                    Width = 74,
                    AspectGetter = (object rowObject) => ((LogListViewModel)rowObject).DateTime.ToString("yyyy/MM/dd HH:mm:ss")
                },
                new OLVColumn
                {
                    Name = $"columnHeaderLog{name}FileName",
                    Text = "File name",
                    Width = 117,
                    AspectName = nameof(LogListViewModel.Filename)
                },
                new OLVColumn
                {
                    Name = $"columnHeaderLog{name}Function",
                    Text = "Function",
                    Width = 97,
                    AspectName = nameof(LogListViewModel.Function)
                },
                new OLVColumn
                {
                    Name = $"columnHeaderLog{name}LineNumber",
                    Text = "Line number",
                    Width = 77,
                    AspectName = nameof(LogListViewModel.LineNumber)
                },
                new OLVColumn
                {
                    Name = $"columnHeaderLog{name}Message",
                    Text = "Message",
                    Width = 666,
                    AspectGetter = (object rowObject) =>
                    {
                        if (rowObject is not LogListViewModel m || string.IsNullOrEmpty(m.Message))
                        {
                            return null;
                        }

                        string message = m.Message;
                        if (message.Contains(Environment.NewLine))
                        {
                            string[] messages = message.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                            if (messages.Any())
                            {
                                message = messages.First();
                            }

                            message = $"{message} (Double click log message to see details)";
                        }
                        else if (message.Length > 200)
                        {
                            message = message[..200];
                        }

                        return message;
                    }
                },
                new OLVColumn
                {
                    Name = $"columnHeaderLog{name}ThreadId",
                    Text = "Thread ID",
                    Width = 66,
                    AspectName = nameof(LogListViewModel.ThreadId)
                }
            });

            fastObjectListView.MouseDoubleClick += ObjectListViewLog_MouseDoubleClick;

            fastObjectListView.RebuildColumns();

            List<LogListViewModel> models = new();
            fastObjectListView.SetObjects(models);

            logTabPage.Controls.Add(fastObjectListView);
            tcFilesLogs.TabPages.Add(logTabPage);

            return new Tuple<FastObjectListView, List<LogListViewModel>>(fastObjectListView, models);
        }

        private void BtnUploads_MouseEnter(object sender, EventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            button.FlatStyle = FlatStyle.Popup;
        }

        private void BtnUploads_MouseLeave(object sender, EventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            button.FlatStyle = FlatStyle.Flat;
        }
    }
}

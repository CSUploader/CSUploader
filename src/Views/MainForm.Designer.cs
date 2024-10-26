
namespace CSUploader.Views
{
    partial class MainForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            BrightIdeasSoftware.HeaderStateStyle headerStateStyle1 = new BrightIdeasSoftware.HeaderStateStyle();
            BrightIdeasSoftware.HeaderStateStyle headerStateStyle2 = new BrightIdeasSoftware.HeaderStateStyle();
            BrightIdeasSoftware.HeaderStateStyle headerStateStyle3 = new BrightIdeasSoftware.HeaderStateStyle();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.olvUploadsUploadMode = new BrightIdeasSoftware.OLVColumn();
            this.tlvHeaderFormatStyle = new BrightIdeasSoftware.HeaderFormatStyle();
            this.ilFileHosters = new System.Windows.Forms.ImageList(this.components);
            this.tmrUploadsTabPageRefresh = new System.Windows.Forms.Timer(this.components);
            this.cmsUploadsPackageFile = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.tsmiUploadsRetry = new System.Windows.Forms.ToolStripMenuItem();
            this.tsmiUploadsStop = new System.Windows.Forms.ToolStripMenuItem();
            this.ilActions = new System.Windows.Forms.ImageList(this.components);
            this.ilPackage = new System.Windows.Forms.ImageList(this.components);
            this.ilSpeed = new System.Windows.Forms.ImageList(this.components);
            this.ilStatus = new System.Windows.Forms.ImageList(this.components);
            this.tcMain = new System.Windows.Forms.TabControl();
            this.tpUpload = new System.Windows.Forms.TabPage();
            this.gbUploadCompression = new System.Windows.Forms.GroupBox();
            this.rbUploadCompressionRar = new System.Windows.Forms.RadioButton();
            this.rbUploadCompression7z = new System.Windows.Forms.RadioButton();
            this.gbUploadCompressionRar = new System.Windows.Forms.GroupBox();
            this.gbUploadCompression7z = new System.Windows.Forms.GroupBox();
            this.gbUpload7zOutputDirectory = new System.Windows.Forms.GroupBox();
            this.btnUpload7zOutputBrowse = new System.Windows.Forms.Button();
            this.tbUpload7zOutputDirectory = new System.Windows.Forms.TextBox();
            this.label14 = new System.Windows.Forms.Label();
            this.gbUpload7zSettings = new System.Windows.Forms.GroupBox();
            this.gbUpload7zEncryption = new System.Windows.Forms.GroupBox();
            this.tbUpload7zPassword2 = new System.Windows.Forms.TextBox();
            this.label12 = new System.Windows.Forms.Label();
            this.tbUpload7zPassword = new System.Windows.Forms.TextBox();
            this.label11 = new System.Windows.Forms.Label();
            this.cbUpload7zSplitVolumeBytes = new System.Windows.Forms.ComboBox();
            this.label10 = new System.Windows.Forms.Label();
            this.lblUpload7zCPU = new System.Windows.Forms.Label();
            this.cbUpload7zNumberCPUThreads = new System.Windows.Forms.ComboBox();
            this.label9 = new System.Windows.Forms.Label();
            this.label8 = new System.Windows.Forms.Label();
            this.cbUpload7zSolidBlockSize = new System.Windows.Forms.ComboBox();
            this.cbUpload7zWordSize = new System.Windows.Forms.ComboBox();
            this.label7 = new System.Windows.Forms.Label();
            this.cbUpload7zDictionarySize = new System.Windows.Forms.ComboBox();
            this.label6 = new System.Windows.Forms.Label();
            this.label5 = new System.Windows.Forms.Label();
            this.cbUpload7zCompressionMethod = new System.Windows.Forms.ComboBox();
            this.cbUpload7zCompressionLevel = new System.Windows.Forms.ComboBox();
            this.label4 = new System.Windows.Forms.Label();
            this.gbUploadFileHosters = new System.Windows.Forms.GroupBox();
            this.fhlvUploadFileHosters = new CSUploader.Controls.FileHostersListView();
            this.btnUploadUpload = new System.Windows.Forms.Button();
            this.gbUploadInput = new System.Windows.Forms.GroupBox();
            this.label20 = new System.Windows.Forms.Label();
            this.tbIploadInputPackageNamingResult = new System.Windows.Forms.TextBox();
            this.label19 = new System.Windows.Forms.Label();
            this.label18 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.label17 = new System.Windows.Forms.Label();
            this.tbUploadInputPackageNamingExpression = new System.Windows.Forms.TextBox();
            this.label16 = new System.Windows.Forms.Label();
            this.tbUploadInputDirectoryPattern = new System.Windows.Forms.TextBox();
            this.cbUploadInputEnableCompression = new System.Windows.Forms.CheckBox();
            this.btnUploadInputBrowse = new System.Windows.Forms.Button();
            this.tbUploadInputDirectory = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.tpUploads = new System.Windows.Forms.TabPage();
            this.btnUploadsStart = new System.Windows.Forms.Button();
            this.btnUploadsStop = new System.Windows.Forms.Button();
            this.btnUploadsPause = new System.Windows.Forms.Button();
            this.tlvUploads = new BrightIdeasSoftware.TreeListView();
            this.olvUploadsName = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsSize = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsHoster = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsConnection = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsGateway = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsStatus = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsAddedDate = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsFinishedDate = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsDuration = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsSpeed = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsETA = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsBytesLoaded = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsBytesRemaining = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsProgress = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsSaveFrom = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsCompressionPassword = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsFileCount = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsError = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsFileUrl = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadsEnabledDisabled = new BrightIdeasSoftware.OLVColumn();
            this.groupBox4 = new System.Windows.Forms.GroupBox();
            this.tpUploaded = new System.Windows.Forms.TabPage();
            this.tlvUploaded = new BrightIdeasSoftware.TreeListView();
            this.olvUploadedName = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadedHoster = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadedSize = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadedAddedDate = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadedFinishedDate = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadedDuration = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadedCompressionPassword = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadedFileCount = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadedError = new BrightIdeasSoftware.OLVColumn();
            this.olvUploadedFileUrl = new BrightIdeasSoftware.OLVColumn();
            this.tpSettings = new System.Windows.Forms.TabPage();
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.cbSettingSpeedLimitEnabled = new System.Windows.Forms.CheckBox();
            this.label15 = new System.Windows.Forms.Label();
            this.nupSettingsSpeedLimit = new System.Windows.Forms.NumericUpDown();
            this.label13 = new System.Windows.Forms.Label();
            this.nupSettingsMaxConcurrentUploadJobs = new System.Windows.Forms.NumericUpDown();
            this.nupSettingsMaxConcurrentCPUJobs = new System.Windows.Forms.NumericUpDown();
            this.label3 = new System.Windows.Forms.Label();
            this.btnSettingsSave = new System.Windows.Forms.Button();
            this.tpLogs = new System.Windows.Forms.TabPage();
            this.cbLogAutoScroll = new System.Windows.Forms.CheckBox();
            this.tcFilesLogs = new System.Windows.Forms.TabControl();
            this.ilFileTypes = new System.Windows.Forms.ImageList(this.components);
            this.cmsUploadsPackageFile.SuspendLayout();
            this.tcMain.SuspendLayout();
            this.tpUpload.SuspendLayout();
            this.gbUploadCompression.SuspendLayout();
            this.gbUploadCompression7z.SuspendLayout();
            this.gbUpload7zOutputDirectory.SuspendLayout();
            this.gbUpload7zSettings.SuspendLayout();
            this.gbUpload7zEncryption.SuspendLayout();
            this.gbUploadFileHosters.SuspendLayout();
            this.gbUploadInput.SuspendLayout();
            this.tpUploads.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.tlvUploads)).BeginInit();
            this.tpUploaded.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.tlvUploaded)).BeginInit();
            this.tpSettings.SuspendLayout();
            this.groupBox1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nupSettingsSpeedLimit)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.nupSettingsMaxConcurrentUploadJobs)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.nupSettingsMaxConcurrentCPUJobs)).BeginInit();
            this.tpLogs.SuspendLayout();
            this.SuspendLayout();
            // 
            // olvUploadsUploadMode
            // 
            this.olvUploadsUploadMode.DisplayIndex = 7;
            this.olvUploadsUploadMode.IsVisible = false;
            this.olvUploadsUploadMode.Text = "Upload Mode";
            // 
            // tlvHeaderFormatStyle
            // 
            this.tlvHeaderFormatStyle.Hot = headerStateStyle1;
            headerStateStyle2.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(237)))), ((int)(((byte)(243)))), ((int)(((byte)(246)))));
            this.tlvHeaderFormatStyle.Normal = headerStateStyle2;
            this.tlvHeaderFormatStyle.Pressed = headerStateStyle3;
            // 
            // ilFileHosters
            // 
            this.ilFileHosters.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;
            this.ilFileHosters.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("ilFileHosters.ImageStream")));
            this.ilFileHosters.TransparentColor = System.Drawing.Color.Transparent;
            this.ilFileHosters.Images.SetKeyName(0, "filehoster_rapidgator.png");
            // 
            // cmsUploadsPackageFile
            // 
            this.cmsUploadsPackageFile.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.tsmiUploadsRetry,
            this.tsmiUploadsStop});
            this.cmsUploadsPackageFile.Name = "cmsUploadsPackageFile";
            this.cmsUploadsPackageFile.Size = new System.Drawing.Size(102, 48);
            this.cmsUploadsPackageFile.Text = "Package File";
            // 
            // tsmiUploadsRetry
            // 
            this.tsmiUploadsRetry.Image = ((System.Drawing.Image)(resources.GetObject("tsmiUploadsRetry.Image")));
            this.tsmiUploadsRetry.Name = "tsmiUploadsRetry";
            this.tsmiUploadsRetry.Size = new System.Drawing.Size(101, 22);
            this.tsmiUploadsRetry.Text = "Retry";
            // 
            // tsmiUploadsStop
            // 
            this.tsmiUploadsStop.Image = ((System.Drawing.Image)(resources.GetObject("tsmiUploadsStop.Image")));
            this.tsmiUploadsStop.Name = "tsmiUploadsStop";
            this.tsmiUploadsStop.Size = new System.Drawing.Size(101, 22);
            this.tsmiUploadsStop.Text = "Stop";
            // 
            // ilActions
            // 
            this.ilActions.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;
            this.ilActions.ImageSize = new System.Drawing.Size(16, 16);
            this.ilActions.TransparentColor = System.Drawing.Color.Transparent;
            // 
            // ilPackage
            // 
            this.ilPackage.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;
            this.ilPackage.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("ilPackage.ImageStream")));
            this.ilPackage.TransparentColor = System.Drawing.Color.Transparent;
            this.ilPackage.Images.SetKeyName(0, "package_closed.png");
            this.ilPackage.Images.SetKeyName(1, "package_compressing.png");
            this.ilPackage.Images.SetKeyName(2, "package_open.png");
            // 
            // ilSpeed
            // 
            this.ilSpeed.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;
            this.ilSpeed.ImageSize = new System.Drawing.Size(16, 16);
            this.ilSpeed.TransparentColor = System.Drawing.Color.Transparent;
            // 
            // ilStatus
            // 
            this.ilStatus.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;
            this.ilStatus.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("ilStatus.ImageStream")));
            this.ilStatus.TransparentColor = System.Drawing.Color.Transparent;
            this.ilStatus.Images.SetKeyName(0, "status_compressing.png");
            this.ilStatus.Images.SetKeyName(1, "status_failed.png");
            this.ilStatus.Images.SetKeyName(2, "status_hashing.png");
            this.ilStatus.Images.SetKeyName(3, "status_ok.png");
            this.ilStatus.Images.SetKeyName(4, "status_queued.png");
            this.ilStatus.Images.SetKeyName(5, "status_running.png");
            this.ilStatus.Images.SetKeyName(6, "status_success.png");
            this.ilStatus.Images.SetKeyName(7, "status_uploading.png");
            this.ilStatus.Images.SetKeyName(8, "status_warning.png");
            this.ilStatus.Images.SetKeyName(9, "status_cancelled.png");
            // 
            // tcMain
            // 
            this.tcMain.Controls.Add(this.tpUpload);
            this.tcMain.Controls.Add(this.tpUploads);
            this.tcMain.Controls.Add(this.tpUploaded);
            this.tcMain.Controls.Add(this.tpSettings);
            this.tcMain.Controls.Add(this.tpLogs);
            this.tcMain.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tcMain.Location = new System.Drawing.Point(0, 0);
            this.tcMain.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tcMain.Name = "tcMain";
            this.tcMain.SelectedIndex = 0;
            this.tcMain.Size = new System.Drawing.Size(1653, 1220);
            this.tcMain.TabIndex = 2;
            // 
            // tpUpload
            // 
            this.tpUpload.BackColor = System.Drawing.SystemColors.Control;
            this.tpUpload.Controls.Add(this.gbUploadCompression);
            this.tpUpload.Controls.Add(this.gbUploadFileHosters);
            this.tpUpload.Controls.Add(this.btnUploadUpload);
            this.tpUpload.Controls.Add(this.gbUploadInput);
            this.tpUpload.Location = new System.Drawing.Point(4, 24);
            this.tpUpload.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tpUpload.Name = "tpUpload";
            this.tpUpload.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tpUpload.Size = new System.Drawing.Size(1645, 1192);
            this.tpUpload.TabIndex = 0;
            this.tpUpload.Text = "Upload";
            // 
            // gbUploadCompression
            // 
            this.gbUploadCompression.Controls.Add(this.rbUploadCompressionRar);
            this.gbUploadCompression.Controls.Add(this.rbUploadCompression7z);
            this.gbUploadCompression.Controls.Add(this.gbUploadCompressionRar);
            this.gbUploadCompression.Controls.Add(this.gbUploadCompression7z);
            this.gbUploadCompression.Location = new System.Drawing.Point(9, 157);
            this.gbUploadCompression.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadCompression.Name = "gbUploadCompression";
            this.gbUploadCompression.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadCompression.Size = new System.Drawing.Size(1625, 442);
            this.gbUploadCompression.TabIndex = 17;
            this.gbUploadCompression.TabStop = false;
            this.gbUploadCompression.Text = "Compression";
            // 
            // rbUploadCompressionRar
            // 
            this.rbUploadCompressionRar.AutoSize = true;
            this.rbUploadCompressionRar.Enabled = false;
            this.rbUploadCompressionRar.Location = new System.Drawing.Point(10, 22);
            this.rbUploadCompressionRar.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.rbUploadCompressionRar.Name = "rbUploadCompressionRar";
            this.rbUploadCompressionRar.Size = new System.Drawing.Size(110, 19);
            this.rbUploadCompressionRar.TabIndex = 4;
            this.rbUploadCompressionRar.Text = "WinRAR (TODO)";
            this.rbUploadCompressionRar.UseVisualStyleBackColor = true;
            // 
            // rbUploadCompression7z
            // 
            this.rbUploadCompression7z.AutoSize = true;
            this.rbUploadCompression7z.Checked = true;
            this.rbUploadCompression7z.Location = new System.Drawing.Point(760, 22);
            this.rbUploadCompression7z.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.rbUploadCompression7z.Name = "rbUploadCompression7z";
            this.rbUploadCompression7z.Size = new System.Drawing.Size(53, 19);
            this.rbUploadCompression7z.TabIndex = 3;
            this.rbUploadCompression7z.TabStop = true;
            this.rbUploadCompression7z.Text = "7-Zip";
            this.rbUploadCompression7z.UseVisualStyleBackColor = true;
            // 
            // gbUploadCompressionRar
            // 
            this.gbUploadCompressionRar.Enabled = false;
            this.gbUploadCompressionRar.Location = new System.Drawing.Point(7, 48);
            this.gbUploadCompressionRar.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadCompressionRar.Name = "gbUploadCompressionRar";
            this.gbUploadCompressionRar.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadCompressionRar.Size = new System.Drawing.Size(746, 384);
            this.gbUploadCompressionRar.TabIndex = 2;
            this.gbUploadCompressionRar.TabStop = false;
            this.gbUploadCompressionRar.Text = "WinRAR";
            // 
            // gbUploadCompression7z
            // 
            this.gbUploadCompression7z.Controls.Add(this.gbUpload7zOutputDirectory);
            this.gbUploadCompression7z.Controls.Add(this.gbUpload7zSettings);
            this.gbUploadCompression7z.Location = new System.Drawing.Point(760, 48);
            this.gbUploadCompression7z.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadCompression7z.Name = "gbUploadCompression7z";
            this.gbUploadCompression7z.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadCompression7z.Size = new System.Drawing.Size(859, 384);
            this.gbUploadCompression7z.TabIndex = 1;
            this.gbUploadCompression7z.TabStop = false;
            this.gbUploadCompression7z.Text = "7-Zip";
            // 
            // gbUpload7zOutputDirectory
            // 
            this.gbUpload7zOutputDirectory.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.gbUpload7zOutputDirectory.Controls.Add(this.btnUpload7zOutputBrowse);
            this.gbUpload7zOutputDirectory.Controls.Add(this.tbUpload7zOutputDirectory);
            this.gbUpload7zOutputDirectory.Controls.Add(this.label14);
            this.gbUpload7zOutputDirectory.Location = new System.Drawing.Point(7, 22);
            this.gbUpload7zOutputDirectory.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUpload7zOutputDirectory.Name = "gbUpload7zOutputDirectory";
            this.gbUpload7zOutputDirectory.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUpload7zOutputDirectory.Size = new System.Drawing.Size(845, 60);
            this.gbUpload7zOutputDirectory.TabIndex = 6;
            this.gbUpload7zOutputDirectory.TabStop = false;
            this.gbUpload7zOutputDirectory.Text = "Output Directory";
            // 
            // btnUpload7zOutputBrowse
            // 
            this.btnUpload7zOutputBrowse.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnUpload7zOutputBrowse.Location = new System.Drawing.Point(750, 20);
            this.btnUpload7zOutputBrowse.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnUpload7zOutputBrowse.Name = "btnUpload7zOutputBrowse";
            this.btnUpload7zOutputBrowse.Size = new System.Drawing.Size(88, 29);
            this.btnUpload7zOutputBrowse.TabIndex = 2;
            this.btnUpload7zOutputBrowse.Text = "Browse";
            this.btnUpload7zOutputBrowse.UseVisualStyleBackColor = true;
            // 
            // tbUpload7zOutputDirectory
            // 
            this.tbUpload7zOutputDirectory.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tbUpload7zOutputDirectory.Location = new System.Drawing.Point(75, 23);
            this.tbUpload7zOutputDirectory.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tbUpload7zOutputDirectory.Name = "tbUpload7zOutputDirectory";
            this.tbUpload7zOutputDirectory.Size = new System.Drawing.Size(666, 23);
            this.tbUpload7zOutputDirectory.TabIndex = 1;
            this.tbUpload7zOutputDirectory.Text = "G:\\anime\\output";
            // 
            // label14
            // 
            this.label14.AutoSize = true;
            this.label14.Location = new System.Drawing.Point(7, 27);
            this.label14.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label14.Name = "label14";
            this.label14.Size = new System.Drawing.Size(55, 15);
            this.label14.TabIndex = 0;
            this.label14.Text = "Directory";
            // 
            // gbUpload7zSettings
            // 
            this.gbUpload7zSettings.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.gbUpload7zSettings.Controls.Add(this.gbUpload7zEncryption);
            this.gbUpload7zSettings.Controls.Add(this.cbUpload7zSplitVolumeBytes);
            this.gbUpload7zSettings.Controls.Add(this.label10);
            this.gbUpload7zSettings.Controls.Add(this.lblUpload7zCPU);
            this.gbUpload7zSettings.Controls.Add(this.cbUpload7zNumberCPUThreads);
            this.gbUpload7zSettings.Controls.Add(this.label9);
            this.gbUpload7zSettings.Controls.Add(this.label8);
            this.gbUpload7zSettings.Controls.Add(this.cbUpload7zSolidBlockSize);
            this.gbUpload7zSettings.Controls.Add(this.cbUpload7zWordSize);
            this.gbUpload7zSettings.Controls.Add(this.label7);
            this.gbUpload7zSettings.Controls.Add(this.cbUpload7zDictionarySize);
            this.gbUpload7zSettings.Controls.Add(this.label6);
            this.gbUpload7zSettings.Controls.Add(this.label5);
            this.gbUpload7zSettings.Controls.Add(this.cbUpload7zCompressionMethod);
            this.gbUpload7zSettings.Controls.Add(this.cbUpload7zCompressionLevel);
            this.gbUpload7zSettings.Controls.Add(this.label4);
            this.gbUpload7zSettings.Location = new System.Drawing.Point(7, 89);
            this.gbUpload7zSettings.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUpload7zSettings.Name = "gbUpload7zSettings";
            this.gbUpload7zSettings.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUpload7zSettings.Size = new System.Drawing.Size(845, 284);
            this.gbUpload7zSettings.TabIndex = 2;
            this.gbUpload7zSettings.TabStop = false;
            this.gbUpload7zSettings.Text = "7zip";
            // 
            // gbUpload7zEncryption
            // 
            this.gbUpload7zEncryption.Controls.Add(this.tbUpload7zPassword2);
            this.gbUpload7zEncryption.Controls.Add(this.label12);
            this.gbUpload7zEncryption.Controls.Add(this.tbUpload7zPassword);
            this.gbUpload7zEncryption.Controls.Add(this.label11);
            this.gbUpload7zEncryption.Location = new System.Drawing.Point(310, 23);
            this.gbUpload7zEncryption.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUpload7zEncryption.Name = "gbUpload7zEncryption";
            this.gbUpload7zEncryption.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUpload7zEncryption.Size = new System.Drawing.Size(328, 147);
            this.gbUpload7zEncryption.TabIndex = 15;
            this.gbUpload7zEncryption.TabStop = false;
            this.gbUpload7zEncryption.Text = "Encryption";
            // 
            // tbUpload7zPassword2
            // 
            this.tbUpload7zPassword2.Location = new System.Drawing.Point(7, 105);
            this.tbUpload7zPassword2.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tbUpload7zPassword2.Name = "tbUpload7zPassword2";
            this.tbUpload7zPassword2.Size = new System.Drawing.Size(313, 23);
            this.tbUpload7zPassword2.TabIndex = 3;
            // 
            // label12
            // 
            this.label12.AutoSize = true;
            this.label12.Location = new System.Drawing.Point(7, 87);
            this.label12.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label12.Name = "label12";
            this.label12.Size = new System.Drawing.Size(103, 15);
            this.label12.TabIndex = 2;
            this.label12.Text = "Reenter password:";
            // 
            // tbUpload7zPassword
            // 
            this.tbUpload7zPassword.Location = new System.Drawing.Point(7, 45);
            this.tbUpload7zPassword.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tbUpload7zPassword.Name = "tbUpload7zPassword";
            this.tbUpload7zPassword.Size = new System.Drawing.Size(313, 23);
            this.tbUpload7zPassword.TabIndex = 1;
            // 
            // label11
            // 
            this.label11.AutoSize = true;
            this.label11.Location = new System.Drawing.Point(7, 27);
            this.label11.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label11.Name = "label11";
            this.label11.Size = new System.Drawing.Size(90, 15);
            this.label11.TabIndex = 0;
            this.label11.Text = "Enter password:";
            // 
            // cbUpload7zSplitVolumeBytes
            // 
            this.cbUpload7zSplitVolumeBytes.FormattingEnabled = true;
            this.cbUpload7zSplitVolumeBytes.Location = new System.Drawing.Point(9, 240);
            this.cbUpload7zSplitVolumeBytes.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbUpload7zSplitVolumeBytes.Name = "cbUpload7zSplitVolumeBytes";
            this.cbUpload7zSplitVolumeBytes.Size = new System.Drawing.Size(293, 23);
            this.cbUpload7zSplitVolumeBytes.TabIndex = 14;
            // 
            // label10
            // 
            this.label10.AutoSize = true;
            this.label10.Location = new System.Drawing.Point(7, 222);
            this.label10.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label10.Name = "label10";
            this.label10.Size = new System.Drawing.Size(121, 15);
            this.label10.TabIndex = 13;
            this.label10.Text = "Split to volume, bytes";
            // 
            // lblUpload7zCPU
            // 
            this.lblUpload7zCPU.Location = new System.Drawing.Point(258, 189);
            this.lblUpload7zCPU.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblUpload7zCPU.Name = "lblUpload7zCPU";
            this.lblUpload7zCPU.Size = new System.Drawing.Size(46, 15);
            this.lblUpload7zCPU.TabIndex = 12;
            this.lblUpload7zCPU.Text = "/ ??";
            this.lblUpload7zCPU.TextAlign = System.Drawing.ContentAlignment.TopRight;
            // 
            // cbUpload7zNumberCPUThreads
            // 
            this.cbUpload7zNumberCPUThreads.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbUpload7zNumberCPUThreads.FormattingEnabled = true;
            this.cbUpload7zNumberCPUThreads.Location = new System.Drawing.Point(162, 185);
            this.cbUpload7zNumberCPUThreads.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbUpload7zNumberCPUThreads.Name = "cbUpload7zNumberCPUThreads";
            this.cbUpload7zNumberCPUThreads.Size = new System.Drawing.Size(88, 23);
            this.cbUpload7zNumberCPUThreads.TabIndex = 11;
            // 
            // label9
            // 
            this.label9.AutoSize = true;
            this.label9.Location = new System.Drawing.Point(7, 189);
            this.label9.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label9.Name = "label9";
            this.label9.Size = new System.Drawing.Size(133, 15);
            this.label9.TabIndex = 10;
            this.label9.Text = "Number of CPU threads";
            // 
            // label8
            // 
            this.label8.AutoSize = true;
            this.label8.Location = new System.Drawing.Point(7, 156);
            this.label8.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label8.Name = "label8";
            this.label8.Size = new System.Drawing.Size(87, 15);
            this.label8.TabIndex = 9;
            this.label8.Text = "Solid Block size";
            // 
            // cbUpload7zSolidBlockSize
            // 
            this.cbUpload7zSolidBlockSize.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbUpload7zSolidBlockSize.FormattingEnabled = true;
            this.cbUpload7zSolidBlockSize.Items.AddRange(new object[] {
            "",
            ""});
            this.cbUpload7zSolidBlockSize.Location = new System.Drawing.Point(162, 152);
            this.cbUpload7zSolidBlockSize.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbUpload7zSolidBlockSize.Name = "cbUpload7zSolidBlockSize";
            this.cbUpload7zSolidBlockSize.Size = new System.Drawing.Size(140, 23);
            this.cbUpload7zSolidBlockSize.TabIndex = 8;
            // 
            // cbUpload7zWordSize
            // 
            this.cbUpload7zWordSize.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbUpload7zWordSize.FormattingEnabled = true;
            this.cbUpload7zWordSize.Items.AddRange(new object[] {
            "",
            ""});
            this.cbUpload7zWordSize.Location = new System.Drawing.Point(162, 120);
            this.cbUpload7zWordSize.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbUpload7zWordSize.Name = "cbUpload7zWordSize";
            this.cbUpload7zWordSize.Size = new System.Drawing.Size(140, 23);
            this.cbUpload7zWordSize.TabIndex = 7;
            // 
            // label7
            // 
            this.label7.AutoSize = true;
            this.label7.Location = new System.Drawing.Point(7, 123);
            this.label7.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(58, 15);
            this.label7.TabIndex = 6;
            this.label7.Text = "Word size";
            // 
            // cbUpload7zDictionarySize
            // 
            this.cbUpload7zDictionarySize.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbUpload7zDictionarySize.FormattingEnabled = true;
            this.cbUpload7zDictionarySize.Items.AddRange(new object[] {
            "",
            ""});
            this.cbUpload7zDictionarySize.Location = new System.Drawing.Point(162, 88);
            this.cbUpload7zDictionarySize.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbUpload7zDictionarySize.Name = "cbUpload7zDictionarySize";
            this.cbUpload7zDictionarySize.Size = new System.Drawing.Size(140, 23);
            this.cbUpload7zDictionarySize.TabIndex = 5;
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(7, 91);
            this.label6.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(83, 15);
            this.label6.TabIndex = 4;
            this.label6.Text = "Dictionary size";
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(7, 59);
            this.label5.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(122, 15);
            this.label5.TabIndex = 3;
            this.label5.Text = "Compression method";
            // 
            // cbUpload7zCompressionMethod
            // 
            this.cbUpload7zCompressionMethod.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbUpload7zCompressionMethod.FormattingEnabled = true;
            this.cbUpload7zCompressionMethod.Items.AddRange(new object[] {
            "",
            ""});
            this.cbUpload7zCompressionMethod.Location = new System.Drawing.Point(162, 55);
            this.cbUpload7zCompressionMethod.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbUpload7zCompressionMethod.Name = "cbUpload7zCompressionMethod";
            this.cbUpload7zCompressionMethod.Size = new System.Drawing.Size(140, 23);
            this.cbUpload7zCompressionMethod.TabIndex = 2;
            // 
            // cbUpload7zCompressionLevel
            // 
            this.cbUpload7zCompressionLevel.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbUpload7zCompressionLevel.FormattingEnabled = true;
            this.cbUpload7zCompressionLevel.Location = new System.Drawing.Point(162, 23);
            this.cbUpload7zCompressionLevel.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbUpload7zCompressionLevel.Name = "cbUpload7zCompressionLevel";
            this.cbUpload7zCompressionLevel.Size = new System.Drawing.Size(140, 23);
            this.cbUpload7zCompressionLevel.TabIndex = 1;
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(7, 27);
            this.label4.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(104, 15);
            this.label4.TabIndex = 0;
            this.label4.Text = "Compression level";
            // 
            // gbUploadFileHosters
            // 
            this.gbUploadFileHosters.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.gbUploadFileHosters.Controls.Add(this.fhlvUploadFileHosters);
            this.gbUploadFileHosters.Location = new System.Drawing.Point(9, 606);
            this.gbUploadFileHosters.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadFileHosters.Name = "gbUploadFileHosters";
            this.gbUploadFileHosters.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadFileHosters.Size = new System.Drawing.Size(1630, 543);
            this.gbUploadFileHosters.TabIndex = 16;
            this.gbUploadFileHosters.TabStop = false;
            this.gbUploadFileHosters.Text = "File Hosters";
            // 
            // fhlvUploadFileHosters
            // 
            this.fhlvUploadFileHosters.Dock = System.Windows.Forms.DockStyle.Fill;
            this.fhlvUploadFileHosters.FileHostersImageList = null;
            this.fhlvUploadFileHosters.Location = new System.Drawing.Point(4, 19);
            this.fhlvUploadFileHosters.Margin = new System.Windows.Forms.Padding(5, 3, 5, 3);
            this.fhlvUploadFileHosters.Name = "fhlvUploadFileHosters";
            this.fhlvUploadFileHosters.Size = new System.Drawing.Size(1622, 521);
            this.fhlvUploadFileHosters.TabIndex = 0;
            // 
            // btnUploadUpload
            // 
            this.btnUploadUpload.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnUploadUpload.Location = new System.Drawing.Point(1547, 1156);
            this.btnUploadUpload.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnUploadUpload.Name = "btnUploadUpload";
            this.btnUploadUpload.Size = new System.Drawing.Size(88, 27);
            this.btnUploadUpload.TabIndex = 3;
            this.btnUploadUpload.Text = "Upload";
            this.btnUploadUpload.UseVisualStyleBackColor = true;
            // 
            // gbUploadInput
            // 
            this.gbUploadInput.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.gbUploadInput.Controls.Add(this.label20);
            this.gbUploadInput.Controls.Add(this.tbIploadInputPackageNamingResult);
            this.gbUploadInput.Controls.Add(this.label19);
            this.gbUploadInput.Controls.Add(this.label18);
            this.gbUploadInput.Controls.Add(this.label2);
            this.gbUploadInput.Controls.Add(this.label17);
            this.gbUploadInput.Controls.Add(this.tbUploadInputPackageNamingExpression);
            this.gbUploadInput.Controls.Add(this.label16);
            this.gbUploadInput.Controls.Add(this.tbUploadInputDirectoryPattern);
            this.gbUploadInput.Controls.Add(this.cbUploadInputEnableCompression);
            this.gbUploadInput.Controls.Add(this.btnUploadInputBrowse);
            this.gbUploadInput.Controls.Add(this.tbUploadInputDirectory);
            this.gbUploadInput.Controls.Add(this.label1);
            this.gbUploadInput.Location = new System.Drawing.Point(9, 7);
            this.gbUploadInput.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadInput.Name = "gbUploadInput";
            this.gbUploadInput.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.gbUploadInput.Size = new System.Drawing.Size(1625, 143);
            this.gbUploadInput.TabIndex = 0;
            this.gbUploadInput.TabStop = false;
            this.gbUploadInput.Text = "Input";
            // 
            // label20
            // 
            this.label20.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label20.AutoSize = true;
            this.label20.Location = new System.Drawing.Point(957, 110);
            this.label20.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label20.Name = "label20";
            this.label20.Size = new System.Drawing.Size(285, 15);
            this.label20.TabIndex = 16;
            this.label20.Text = "Result of the above expression on the input directory";
            // 
            // tbIploadInputPackageNamingResult
            // 
            this.tbIploadInputPackageNamingResult.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tbIploadInputPackageNamingResult.BackColor = System.Drawing.SystemColors.Window;
            this.tbIploadInputPackageNamingResult.Font = new System.Drawing.Font("Lucida Console", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.tbIploadInputPackageNamingResult.Location = new System.Drawing.Point(415, 108);
            this.tbIploadInputPackageNamingResult.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tbIploadInputPackageNamingResult.Name = "tbIploadInputPackageNamingResult";
            this.tbIploadInputPackageNamingResult.ReadOnly = true;
            this.tbIploadInputPackageNamingResult.Size = new System.Drawing.Size(534, 18);
            this.tbIploadInputPackageNamingResult.TabIndex = 15;
            // 
            // label19
            // 
            this.label19.AutoSize = true;
            this.label19.Location = new System.Drawing.Point(245, 110);
            this.label19.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label19.Name = "label19";
            this.label19.Size = new System.Drawing.Size(127, 15);
            this.label19.TabIndex = 14;
            this.label19.Text = "Package naming result";
            // 
            // label18
            // 
            this.label18.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.label18.AutoSize = true;
            this.label18.Location = new System.Drawing.Point(957, 82);
            this.label18.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label18.Name = "label18";
            this.label18.Size = new System.Drawing.Size(520, 15);
            this.label18.TabIndex = 13;
            this.label18.Text = "Pattern for creating the file names if compression is enabled; use capture groups" +
    " to get the values\r\n";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(316, 55);
            this.label2.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(366, 15);
            this.label2.TabIndex = 12;
            this.label2.Text = "Pattern for matching directories to upload within the input directory";
            // 
            // label17
            // 
            this.label17.AutoSize = true;
            this.label17.Location = new System.Drawing.Point(245, 82);
            this.label17.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label17.Name = "label17";
            this.label17.Size = new System.Drawing.Size(154, 15);
            this.label17.TabIndex = 11;
            this.label17.Text = "Package naming expression";
            // 
            // tbUploadInputPackageNamingExpression
            // 
            this.tbUploadInputPackageNamingExpression.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tbUploadInputPackageNamingExpression.Font = new System.Drawing.Font("Lucida Console", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.tbUploadInputPackageNamingExpression.Location = new System.Drawing.Point(415, 81);
            this.tbUploadInputPackageNamingExpression.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tbUploadInputPackageNamingExpression.Name = "tbUploadInputPackageNamingExpression";
            this.tbUploadInputPackageNamingExpression.Size = new System.Drawing.Size(534, 18);
            this.tbUploadInputPackageNamingExpression.TabIndex = 10;
            this.tbUploadInputPackageNamingExpression.Text = "{0}";
            // 
            // label16
            // 
            this.label16.AutoSize = true;
            this.label16.Location = new System.Drawing.Point(7, 55);
            this.label16.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label16.Name = "label16";
            this.label16.Size = new System.Drawing.Size(45, 15);
            this.label16.TabIndex = 9;
            this.label16.Text = "Pattern";
            // 
            // tbUploadInputDirectoryPattern
            // 
            this.tbUploadInputDirectoryPattern.Font = new System.Drawing.Font("Lucida Console", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.tbUploadInputDirectoryPattern.Location = new System.Drawing.Point(75, 53);
            this.tbUploadInputDirectoryPattern.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tbUploadInputDirectoryPattern.Name = "tbUploadInputDirectoryPattern";
            this.tbUploadInputDirectoryPattern.Size = new System.Drawing.Size(234, 18);
            this.tbUploadInputDirectoryPattern.TabIndex = 8;
            this.tbUploadInputDirectoryPattern.Text = "(.+)";
            // 
            // cbUploadInputEnableCompression
            // 
            this.cbUploadInputEnableCompression.AutoSize = true;
            this.cbUploadInputEnableCompression.Checked = true;
            this.cbUploadInputEnableCompression.CheckState = System.Windows.Forms.CheckState.Checked;
            this.cbUploadInputEnableCompression.Location = new System.Drawing.Point(10, 81);
            this.cbUploadInputEnableCompression.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbUploadInputEnableCompression.Name = "cbUploadInputEnableCompression";
            this.cbUploadInputEnableCompression.Size = new System.Drawing.Size(197, 19);
            this.cbUploadInputEnableCompression.TabIndex = 6;
            this.cbUploadInputEnableCompression.Text = "Compress files before uploading";
            this.cbUploadInputEnableCompression.UseVisualStyleBackColor = true;
            // 
            // btnUploadInputBrowse
            // 
            this.btnUploadInputBrowse.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnUploadInputBrowse.Location = new System.Drawing.Point(1531, 20);
            this.btnUploadInputBrowse.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnUploadInputBrowse.Name = "btnUploadInputBrowse";
            this.btnUploadInputBrowse.Size = new System.Drawing.Size(88, 29);
            this.btnUploadInputBrowse.TabIndex = 2;
            this.btnUploadInputBrowse.Text = "Browse";
            this.btnUploadInputBrowse.UseVisualStyleBackColor = true;
            // 
            // tbUploadInputDirectory
            // 
            this.tbUploadInputDirectory.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tbUploadInputDirectory.Location = new System.Drawing.Point(75, 23);
            this.tbUploadInputDirectory.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tbUploadInputDirectory.Name = "tbUploadInputDirectory";
            this.tbUploadInputDirectory.Size = new System.Drawing.Size(1446, 23);
            this.tbUploadInputDirectory.TabIndex = 1;
            this.tbUploadInputDirectory.Text = "G:\\anime\\input";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(7, 27);
            this.label1.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(55, 15);
            this.label1.TabIndex = 0;
            this.label1.Text = "Directory";
            // 
            // tpUploads
            // 
            this.tpUploads.BackColor = System.Drawing.SystemColors.Control;
            this.tpUploads.Controls.Add(this.btnUploadsStart);
            this.tpUploads.Controls.Add(this.btnUploadsStop);
            this.tpUploads.Controls.Add(this.btnUploadsPause);
            this.tpUploads.Controls.Add(this.tlvUploads);
            this.tpUploads.Controls.Add(this.groupBox4);
            this.tpUploads.Location = new System.Drawing.Point(4, 24);
            this.tpUploads.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tpUploads.Name = "tpUploads";
            this.tpUploads.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tpUploads.Size = new System.Drawing.Size(1645, 1192);
            this.tpUploads.TabIndex = 1;
            this.tpUploads.Text = "Uploads";
            // 
            // btnUploadsStart
            // 
            this.btnUploadsStart.BackColor = System.Drawing.SystemColors.Control;
            this.btnUploadsStart.BackgroundImage = ((System.Drawing.Image)(resources.GetObject("btnUploadsStart.BackgroundImage")));
            this.btnUploadsStart.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.btnUploadsStart.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(192)))), ((int)(((byte)(255)))), ((int)(((byte)(255)))));
            this.btnUploadsStart.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnUploadsStart.ForeColor = System.Drawing.SystemColors.Control;
            this.btnUploadsStart.Location = new System.Drawing.Point(9, 7);
            this.btnUploadsStart.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnUploadsStart.Name = "btnUploadsStart";
            this.btnUploadsStart.Size = new System.Drawing.Size(33, 32);
            this.btnUploadsStart.TabIndex = 3;
            this.btnUploadsStart.UseVisualStyleBackColor = true;
            this.btnUploadsStart.MouseEnter += new System.EventHandler(this.BtnUploads_MouseEnter);
            this.btnUploadsStart.MouseLeave += new System.EventHandler(this.BtnUploads_MouseLeave);
            // 
            // btnUploadsStop
            // 
            this.btnUploadsStop.BackColor = System.Drawing.SystemColors.Control;
            this.btnUploadsStop.BackgroundImage = ((System.Drawing.Image)(resources.GetObject("btnUploadsStop.BackgroundImage")));
            this.btnUploadsStop.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.btnUploadsStop.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(192)))), ((int)(((byte)(255)))), ((int)(((byte)(255)))));
            this.btnUploadsStop.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnUploadsStop.ForeColor = System.Drawing.SystemColors.Control;
            this.btnUploadsStop.Location = new System.Drawing.Point(89, 9);
            this.btnUploadsStop.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnUploadsStop.Name = "btnUploadsStop";
            this.btnUploadsStop.Size = new System.Drawing.Size(30, 30);
            this.btnUploadsStop.TabIndex = 5;
            this.btnUploadsStop.UseVisualStyleBackColor = true;
            this.btnUploadsStop.MouseEnter += new System.EventHandler(this.BtnUploads_MouseEnter);
            this.btnUploadsStop.MouseLeave += new System.EventHandler(this.BtnUploads_MouseLeave);
            // 
            // btnUploadsPause
            // 
            this.btnUploadsPause.BackColor = System.Drawing.SystemColors.Control;
            this.btnUploadsPause.BackgroundImage = ((System.Drawing.Image)(resources.GetObject("btnUploadsPause.BackgroundImage")));
            this.btnUploadsPause.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.btnUploadsPause.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(192)))), ((int)(((byte)(255)))), ((int)(((byte)(255)))));
            this.btnUploadsPause.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnUploadsPause.ForeColor = System.Drawing.SystemColors.Control;
            this.btnUploadsPause.Location = new System.Drawing.Point(49, 7);
            this.btnUploadsPause.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnUploadsPause.Name = "btnUploadsPause";
            this.btnUploadsPause.Size = new System.Drawing.Size(33, 32);
            this.btnUploadsPause.TabIndex = 4;
            this.btnUploadsPause.UseVisualStyleBackColor = true;
            this.btnUploadsPause.MouseEnter += new System.EventHandler(this.BtnUploads_MouseEnter);
            this.btnUploadsPause.MouseLeave += new System.EventHandler(this.BtnUploads_MouseLeave);
            // 
            // tlvUploads
            // 
            this.tlvUploads.AllColumns.Add(this.olvUploadsName);
            this.tlvUploads.AllColumns.Add(this.olvUploadsSize);
            this.tlvUploads.AllColumns.Add(this.olvUploadsHoster);
            this.tlvUploads.AllColumns.Add(this.olvUploadsConnection);
            this.tlvUploads.AllColumns.Add(this.olvUploadsGateway);
            this.tlvUploads.AllColumns.Add(this.olvUploadsUploadMode);
            this.tlvUploads.AllColumns.Add(this.olvUploadsStatus);
            this.tlvUploads.AllColumns.Add(this.olvUploadsAddedDate);
            this.tlvUploads.AllColumns.Add(this.olvUploadsFinishedDate);
            this.tlvUploads.AllColumns.Add(this.olvUploadsDuration);
            this.tlvUploads.AllColumns.Add(this.olvUploadsSpeed);
            this.tlvUploads.AllColumns.Add(this.olvUploadsETA);
            this.tlvUploads.AllColumns.Add(this.olvUploadsBytesLoaded);
            this.tlvUploads.AllColumns.Add(this.olvUploadsBytesRemaining);
            this.tlvUploads.AllColumns.Add(this.olvUploadsProgress);
            this.tlvUploads.AllColumns.Add(this.olvUploadsSaveFrom);
            this.tlvUploads.AllColumns.Add(this.olvUploadsCompressionPassword);
            this.tlvUploads.AllColumns.Add(this.olvUploadsFileCount);
            this.tlvUploads.AllColumns.Add(this.olvUploadsError);
            this.tlvUploads.AllColumns.Add(this.olvUploadsFileUrl);
            this.tlvUploads.AllColumns.Add(this.olvUploadsEnabledDisabled);
            this.tlvUploads.AllowColumnReorder = true;
            this.tlvUploads.AlternateRowBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(238)))), ((int)(((byte)(246)))), ((int)(((byte)(248)))));
            this.tlvUploads.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tlvUploads.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(244)))), ((int)(((byte)(252)))), ((int)(((byte)(254)))));
            this.tlvUploads.CellEditUseWholeCell = false;
            this.tlvUploads.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.olvUploadsName,
            this.olvUploadsSize,
            this.olvUploadsHoster,
            this.olvUploadsConnection,
            this.olvUploadsGateway,
            this.olvUploadsStatus,
            this.olvUploadsAddedDate,
            this.olvUploadsFinishedDate,
            this.olvUploadsDuration,
            this.olvUploadsSpeed,
            this.olvUploadsETA,
            this.olvUploadsBytesLoaded,
            this.olvUploadsBytesRemaining,
            this.olvUploadsProgress,
            this.olvUploadsSaveFrom,
            this.olvUploadsCompressionPassword,
            this.olvUploadsFileCount,
            this.olvUploadsError,
            this.olvUploadsFileUrl});
            this.tlvUploads.FullRowSelect = true;
            this.tlvUploads.GridLines = true;
            this.tlvUploads.HeaderFormatStyle = this.tlvHeaderFormatStyle;
            this.tlvUploads.LargeImageList = this.ilFileHosters;
            this.tlvUploads.Location = new System.Drawing.Point(0, 81);
            this.tlvUploads.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tlvUploads.Name = "tlvUploads";
            this.tlvUploads.SelectedBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(200)))), ((int)(((byte)(224)))), ((int)(((byte)(236)))));
            this.tlvUploads.SelectedForeColor = System.Drawing.Color.Black;
            this.tlvUploads.ShowGroups = false;
            this.tlvUploads.ShowImagesOnSubItems = true;
            this.tlvUploads.Size = new System.Drawing.Size(1643, 1108);
            this.tlvUploads.SmallImageList = this.ilFileHosters;
            this.tlvUploads.TabIndex = 1;
            this.tlvUploads.UseAlternatingBackColors = true;
            this.tlvUploads.UseCompatibleStateImageBehavior = false;
            this.tlvUploads.View = System.Windows.Forms.View.Details;
            this.tlvUploads.VirtualMode = true;
            // 
            // olvUploadsName
            // 
            this.olvUploadsName.AspectName = "";
            this.olvUploadsName.HeaderTextAlign = System.Windows.Forms.HorizontalAlignment.Left;
            this.olvUploadsName.IsEditable = false;
            this.olvUploadsName.Text = "Name";
            this.olvUploadsName.Width = 193;
            // 
            // olvUploadsSize
            // 
            this.olvUploadsSize.Text = "Size";
            this.olvUploadsSize.Width = 73;
            // 
            // olvUploadsHoster
            // 
            this.olvUploadsHoster.IsEditable = false;
            this.olvUploadsHoster.Text = "Hoster";
            // 
            // olvUploadsConnection
            // 
            this.olvUploadsConnection.Text = "Connection";
            this.olvUploadsConnection.Width = 73;
            // 
            // olvUploadsGateway
            // 
            this.olvUploadsGateway.Text = "Gateway";
            this.olvUploadsGateway.Width = 146;
            // 
            // olvUploadsStatus
            // 
            this.olvUploadsStatus.IsEditable = false;
            this.olvUploadsStatus.Text = "Status";
            this.olvUploadsStatus.Width = 79;
            // 
            // olvUploadsAddedDate
            // 
            this.olvUploadsAddedDate.Text = "Added Date";
            // 
            // olvUploadsFinishedDate
            // 
            this.olvUploadsFinishedDate.Text = "Finished Date";
            // 
            // olvUploadsDuration
            // 
            this.olvUploadsDuration.Text = "Duration";
            // 
            // olvUploadsSpeed
            // 
            this.olvUploadsSpeed.Text = "Speed";
            this.olvUploadsSpeed.Width = 73;
            // 
            // olvUploadsETA
            // 
            this.olvUploadsETA.Text = "ETA";
            this.olvUploadsETA.Width = 71;
            // 
            // olvUploadsBytesLoaded
            // 
            this.olvUploadsBytesLoaded.Text = "Bytes Loaded";
            this.olvUploadsBytesLoaded.Width = 112;
            // 
            // olvUploadsBytesRemaining
            // 
            this.olvUploadsBytesRemaining.Text = "Bytes Remaining";
            // 
            // olvUploadsProgress
            // 
            this.olvUploadsProgress.Text = "Progress";
            this.olvUploadsProgress.Width = 109;
            // 
            // olvUploadsSaveFrom
            // 
            this.olvUploadsSaveFrom.Text = "Save from";
            this.olvUploadsSaveFrom.Width = 112;
            // 
            // olvUploadsCompressionPassword
            // 
            this.olvUploadsCompressionPassword.Text = "Compression Password";
            // 
            // olvUploadsFileCount
            // 
            this.olvUploadsFileCount.Text = "File Count";
            // 
            // olvUploadsError
            // 
            this.olvUploadsError.Text = "Error";
            // 
            // olvUploadsFileUrl
            // 
            this.olvUploadsFileUrl.Text = "File URL";
            // 
            // olvUploadsEnabledDisabled
            // 
            this.olvUploadsEnabledDisabled.CheckBoxes = true;
            this.olvUploadsEnabledDisabled.DisplayIndex = 19;
            this.olvUploadsEnabledDisabled.IsVisible = false;
            this.olvUploadsEnabledDisabled.Text = "Enabled / Disabled";
            // 
            // groupBox4
            // 
            this.groupBox4.Location = new System.Drawing.Point(400, 3);
            this.groupBox4.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.groupBox4.Name = "groupBox4";
            this.groupBox4.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.groupBox4.Size = new System.Drawing.Size(1234, 70);
            this.groupBox4.TabIndex = 0;
            this.groupBox4.TabStop = false;
            this.groupBox4.Text = "Statistics";
            // 
            // tpUploaded
            // 
            this.tpUploaded.Controls.Add(this.tlvUploaded);
            this.tpUploaded.Location = new System.Drawing.Point(4, 24);
            this.tpUploaded.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tpUploaded.Name = "tpUploaded";
            this.tpUploaded.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tpUploaded.Size = new System.Drawing.Size(1645, 1192);
            this.tpUploaded.TabIndex = 4;
            this.tpUploaded.Text = "Uploaded";
            this.tpUploaded.UseVisualStyleBackColor = true;
            // 
            // tlvUploaded
            // 
            this.tlvUploaded.AllColumns.Add(this.olvUploadedName);
            this.tlvUploaded.AllColumns.Add(this.olvUploadedHoster);
            this.tlvUploaded.AllColumns.Add(this.olvUploadedSize);
            this.tlvUploaded.AllColumns.Add(this.olvUploadedAddedDate);
            this.tlvUploaded.AllColumns.Add(this.olvUploadedFinishedDate);
            this.tlvUploaded.AllColumns.Add(this.olvUploadedDuration);
            this.tlvUploaded.AllColumns.Add(this.olvUploadedCompressionPassword);
            this.tlvUploaded.AllColumns.Add(this.olvUploadedFileCount);
            this.tlvUploaded.AllColumns.Add(this.olvUploadedError);
            this.tlvUploaded.AllColumns.Add(this.olvUploadedFileUrl);
            this.tlvUploaded.AllowColumnReorder = true;
            this.tlvUploaded.AlternateRowBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(205)))), ((int)(((byte)(232)))), ((int)(((byte)(240)))));
            this.tlvUploaded.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tlvUploaded.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(244)))), ((int)(((byte)(252)))), ((int)(((byte)(254)))));
            this.tlvUploaded.CellEditUseWholeCell = false;
            this.tlvUploaded.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.olvUploadedName,
            this.olvUploadedHoster,
            this.olvUploadedSize,
            this.olvUploadedAddedDate,
            this.olvUploadedFinishedDate,
            this.olvUploadedDuration,
            this.olvUploadedCompressionPassword,
            this.olvUploadedFileCount,
            this.olvUploadedError,
            this.olvUploadedFileUrl});
            this.tlvUploaded.FullRowSelect = true;
            this.tlvUploaded.GridLines = true;
            this.tlvUploaded.HeaderFormatStyle = this.tlvHeaderFormatStyle;
            this.tlvUploaded.LargeImageList = this.ilFileHosters;
            this.tlvUploaded.Location = new System.Drawing.Point(0, 0);
            this.tlvUploaded.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tlvUploaded.Name = "tlvUploaded";
            this.tlvUploaded.SelectedBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(200)))), ((int)(((byte)(224)))), ((int)(((byte)(236)))));
            this.tlvUploaded.SelectedForeColor = System.Drawing.Color.Black;
            this.tlvUploaded.ShowGroups = false;
            this.tlvUploaded.ShowImagesOnSubItems = true;
            this.tlvUploaded.Size = new System.Drawing.Size(1643, 1189);
            this.tlvUploaded.SmallImageList = this.ilFileHosters;
            this.tlvUploaded.TabIndex = 2;
            this.tlvUploaded.UseAlternatingBackColors = true;
            this.tlvUploaded.UseCompatibleStateImageBehavior = false;
            this.tlvUploaded.View = System.Windows.Forms.View.Details;
            this.tlvUploaded.VirtualMode = true;
            // 
            // olvUploadedName
            // 
            this.olvUploadedName.AspectName = "";
            this.olvUploadedName.HeaderTextAlign = System.Windows.Forms.HorizontalAlignment.Left;
            this.olvUploadedName.IsEditable = false;
            this.olvUploadedName.Text = "Name";
            this.olvUploadedName.Width = 193;
            // 
            // olvUploadedHoster
            // 
            this.olvUploadedHoster.IsEditable = false;
            this.olvUploadedHoster.Text = "Hoster";
            // 
            // olvUploadedSize
            // 
            this.olvUploadedSize.Text = "Size";
            this.olvUploadedSize.Width = 73;
            // 
            // olvUploadedAddedDate
            // 
            this.olvUploadedAddedDate.Text = "Added Date";
            // 
            // olvUploadedFinishedDate
            // 
            this.olvUploadedFinishedDate.Text = "Finished Date";
            // 
            // olvUploadedDuration
            // 
            this.olvUploadedDuration.Text = "Duration";
            // 
            // olvUploadedCompressionPassword
            // 
            this.olvUploadedCompressionPassword.Text = "Compression Password";
            // 
            // olvUploadedFileCount
            // 
            this.olvUploadedFileCount.Text = "File Count";
            // 
            // olvUploadedError
            // 
            this.olvUploadedError.Text = "Error";
            // 
            // olvUploadedFileUrl
            // 
            this.olvUploadedFileUrl.Text = "File URL";
            // 
            // tpSettings
            // 
            this.tpSettings.BackColor = System.Drawing.SystemColors.Control;
            this.tpSettings.Controls.Add(this.groupBox1);
            this.tpSettings.Controls.Add(this.btnSettingsSave);
            this.tpSettings.Location = new System.Drawing.Point(4, 24);
            this.tpSettings.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tpSettings.Name = "tpSettings";
            this.tpSettings.Size = new System.Drawing.Size(1645, 1192);
            this.tpSettings.TabIndex = 2;
            this.tpSettings.Text = "Settings";
            // 
            // groupBox1
            // 
            this.groupBox1.Controls.Add(this.cbSettingSpeedLimitEnabled);
            this.groupBox1.Controls.Add(this.label15);
            this.groupBox1.Controls.Add(this.nupSettingsSpeedLimit);
            this.groupBox1.Controls.Add(this.label13);
            this.groupBox1.Controls.Add(this.nupSettingsMaxConcurrentUploadJobs);
            this.groupBox1.Controls.Add(this.nupSettingsMaxConcurrentCPUJobs);
            this.groupBox1.Controls.Add(this.label3);
            this.groupBox1.Location = new System.Drawing.Point(9, 3);
            this.groupBox1.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.groupBox1.Size = new System.Drawing.Size(562, 481);
            this.groupBox1.TabIndex = 5;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "Uploads";
            // 
            // cbSettingSpeedLimitEnabled
            // 
            this.cbSettingSpeedLimitEnabled.AutoSize = true;
            this.cbSettingSpeedLimitEnabled.Location = new System.Drawing.Point(377, 88);
            this.cbSettingSpeedLimitEnabled.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbSettingSpeedLimitEnabled.Name = "cbSettingSpeedLimitEnabled";
            this.cbSettingSpeedLimitEnabled.Size = new System.Drawing.Size(122, 19);
            this.cbSettingSpeedLimitEnabled.TabIndex = 6;
            this.cbSettingSpeedLimitEnabled.Text = "Enable speed limit";
            this.cbSettingSpeedLimitEnabled.UseVisualStyleBackColor = true;
            // 
            // label15
            // 
            this.label15.AutoSize = true;
            this.label15.Location = new System.Drawing.Point(7, 89);
            this.label15.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label15.Name = "label15";
            this.label15.Size = new System.Drawing.Size(140, 15);
            this.label15.TabIndex = 5;
            this.label15.Text = "Speed limit (in bytes/sec)";
            // 
            // nupSettingsSpeedLimit
            // 
            this.nupSettingsSpeedLimit.Enabled = false;
            this.nupSettingsSpeedLimit.Location = new System.Drawing.Point(281, 87);
            this.nupSettingsSpeedLimit.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.nupSettingsSpeedLimit.Maximum = new decimal(new int[] {
            1310720,
            0,
            0,
            0});
            this.nupSettingsSpeedLimit.Minimum = new decimal(new int[] {
            20,
            0,
            0,
            0});
            this.nupSettingsSpeedLimit.Name = "nupSettingsSpeedLimit";
            this.nupSettingsSpeedLimit.Size = new System.Drawing.Size(89, 23);
            this.nupSettingsSpeedLimit.TabIndex = 4;
            this.nupSettingsSpeedLimit.Value = new decimal(new int[] {
            1310720,
            0,
            0,
            0});
            // 
            // label13
            // 
            this.label13.AutoSize = true;
            this.label13.Location = new System.Drawing.Point(7, 59);
            this.label13.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label13.Name = "label13";
            this.label13.Size = new System.Drawing.Size(190, 15);
            this.label13.TabIndex = 3;
            this.label13.Text = "Max uploads running concurrently";
            // 
            // nupSettingsMaxConcurrentUploadJobs
            // 
            this.nupSettingsMaxConcurrentUploadJobs.Location = new System.Drawing.Point(281, 57);
            this.nupSettingsMaxConcurrentUploadJobs.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.nupSettingsMaxConcurrentUploadJobs.Maximum = new decimal(new int[] {
            1000,
            0,
            0,
            0});
            this.nupSettingsMaxConcurrentUploadJobs.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.nupSettingsMaxConcurrentUploadJobs.Name = "nupSettingsMaxConcurrentUploadJobs";
            this.nupSettingsMaxConcurrentUploadJobs.Size = new System.Drawing.Size(89, 23);
            this.nupSettingsMaxConcurrentUploadJobs.TabIndex = 2;
            this.nupSettingsMaxConcurrentUploadJobs.Value = new decimal(new int[] {
            1,
            0,
            0,
            0});
            // 
            // nupSettingsMaxConcurrentCPUJobs
            // 
            this.nupSettingsMaxConcurrentCPUJobs.Location = new System.Drawing.Point(281, 27);
            this.nupSettingsMaxConcurrentCPUJobs.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.nupSettingsMaxConcurrentCPUJobs.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.nupSettingsMaxConcurrentCPUJobs.Name = "nupSettingsMaxConcurrentCPUJobs";
            this.nupSettingsMaxConcurrentCPUJobs.Size = new System.Drawing.Size(89, 23);
            this.nupSettingsMaxConcurrentCPUJobs.TabIndex = 1;
            this.nupSettingsMaxConcurrentCPUJobs.Value = new decimal(new int[] {
            1,
            0,
            0,
            0});
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(7, 29);
            this.label3.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(246, 15);
            this.label3.TabIndex = 0;
            this.label3.Text = "Max CPU intensive jobs running concurrently";
            // 
            // btnSettingsSave
            // 
            this.btnSettingsSave.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnSettingsSave.Location = new System.Drawing.Point(1547, 1157);
            this.btnSettingsSave.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.btnSettingsSave.Name = "btnSettingsSave";
            this.btnSettingsSave.Size = new System.Drawing.Size(88, 29);
            this.btnSettingsSave.TabIndex = 4;
            this.btnSettingsSave.Text = "Save";
            this.btnSettingsSave.UseVisualStyleBackColor = true;
            // 
            // tpLogs
            // 
            this.tpLogs.Controls.Add(this.cbLogAutoScroll);
            this.tpLogs.Controls.Add(this.tcFilesLogs);
            this.tpLogs.Location = new System.Drawing.Point(4, 24);
            this.tpLogs.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tpLogs.Name = "tpLogs";
            this.tpLogs.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tpLogs.Size = new System.Drawing.Size(1645, 1192);
            this.tpLogs.TabIndex = 3;
            this.tpLogs.Text = "Logs";
            this.tpLogs.UseVisualStyleBackColor = true;
            // 
            // cbLogAutoScroll
            // 
            this.cbLogAutoScroll.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.cbLogAutoScroll.AutoSize = true;
            this.cbLogAutoScroll.Location = new System.Drawing.Point(1548, 1161);
            this.cbLogAutoScroll.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.cbLogAutoScroll.Name = "cbLogAutoScroll";
            this.cbLogAutoScroll.Size = new System.Drawing.Size(83, 19);
            this.cbLogAutoScroll.TabIndex = 3;
            this.cbLogAutoScroll.Text = "Auto scroll";
            this.cbLogAutoScroll.UseVisualStyleBackColor = true;
            // 
            // tcFilesLogs
            // 
            this.tcFilesLogs.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tcFilesLogs.Location = new System.Drawing.Point(4, 3);
            this.tcFilesLogs.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.tcFilesLogs.Name = "tcFilesLogs";
            this.tcFilesLogs.SelectedIndex = 0;
            this.tcFilesLogs.Size = new System.Drawing.Size(1637, 1150);
            this.tcFilesLogs.TabIndex = 2;
            // 
            // ilFileTypes
            // 
            this.ilFileTypes.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;
            this.ilFileTypes.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("ilFileTypes.ImageStream")));
            this.ilFileTypes.TransparentColor = System.Drawing.Color.Transparent;
            this.ilFileTypes.Images.SetKeyName(0, "filetype_7z.png");
            this.ilFileTypes.Images.SetKeyName(1, "filetype_rar.png");
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1653, 1220);
            this.Controls.Add(this.tcMain);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "CSUploader";
            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            this.cmsUploadsPackageFile.ResumeLayout(false);
            this.tcMain.ResumeLayout(false);
            this.tpUpload.ResumeLayout(false);
            this.gbUploadCompression.ResumeLayout(false);
            this.gbUploadCompression.PerformLayout();
            this.gbUploadCompression7z.ResumeLayout(false);
            this.gbUpload7zOutputDirectory.ResumeLayout(false);
            this.gbUpload7zOutputDirectory.PerformLayout();
            this.gbUpload7zSettings.ResumeLayout(false);
            this.gbUpload7zSettings.PerformLayout();
            this.gbUpload7zEncryption.ResumeLayout(false);
            this.gbUpload7zEncryption.PerformLayout();
            this.gbUploadFileHosters.ResumeLayout(false);
            this.gbUploadInput.ResumeLayout(false);
            this.gbUploadInput.PerformLayout();
            this.tpUploads.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.tlvUploads)).EndInit();
            this.tpUploaded.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.tlvUploaded)).EndInit();
            this.tpSettings.ResumeLayout(false);
            this.groupBox1.ResumeLayout(false);
            this.groupBox1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nupSettingsSpeedLimit)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.nupSettingsMaxConcurrentUploadJobs)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.nupSettingsMaxConcurrentCPUJobs)).EndInit();
            this.tpLogs.ResumeLayout(false);
            this.tpLogs.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion
        private System.Windows.Forms.Timer tmrUploadsTabPageRefresh;
        private BrightIdeasSoftware.OLVColumn olvUploadsUploadMode;
        private BrightIdeasSoftware.HeaderFormatStyle tlvHeaderFormatStyle;
        private System.Windows.Forms.ImageList ilFileHosters;
        public System.Windows.Forms.ContextMenuStrip cmsUploadsPackageFile;
        public System.Windows.Forms.ToolStripMenuItem tsmiUploadsRetry;
        private System.Windows.Forms.ToolStripMenuItem tsmiUploadsStop;
        private System.Windows.Forms.ImageList ilActions;
        private System.Windows.Forms.ImageList ilPackage;
        private System.Windows.Forms.ImageList ilSpeed;
        private System.Windows.Forms.ImageList ilStatus;
        private System.Windows.Forms.TabControl tcMain;
        private System.Windows.Forms.TabPage tpUpload;
        private System.Windows.Forms.GroupBox gbUploadCompression;
        private System.Windows.Forms.RadioButton rbUploadCompressionRar;
        private System.Windows.Forms.RadioButton rbUploadCompression7z;
        private System.Windows.Forms.GroupBox gbUploadCompressionRar;
        private System.Windows.Forms.GroupBox gbUploadCompression7z;
        private System.Windows.Forms.GroupBox gbUpload7zOutputDirectory;
        private System.Windows.Forms.Button btnUpload7zOutputBrowse;
        private System.Windows.Forms.TextBox tbUpload7zOutputDirectory;
        private System.Windows.Forms.Label label14;
        private System.Windows.Forms.GroupBox gbUpload7zSettings;
        private System.Windows.Forms.GroupBox gbUpload7zEncryption;
        private System.Windows.Forms.TextBox tbUpload7zPassword2;
        private System.Windows.Forms.Label label12;
        private System.Windows.Forms.TextBox tbUpload7zPassword;
        private System.Windows.Forms.Label label11;
        private System.Windows.Forms.ComboBox cbUpload7zSplitVolumeBytes;
        private System.Windows.Forms.Label label10;
        private System.Windows.Forms.Label lblUpload7zCPU;
        private System.Windows.Forms.ComboBox cbUpload7zNumberCPUThreads;
        private System.Windows.Forms.Label label9;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.ComboBox cbUpload7zSolidBlockSize;
        private System.Windows.Forms.ComboBox cbUpload7zWordSize;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.ComboBox cbUpload7zDictionarySize;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.ComboBox cbUpload7zCompressionMethod;
        private System.Windows.Forms.ComboBox cbUpload7zCompressionLevel;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.GroupBox gbUploadFileHosters;
        private Controls.FileHostersListView fhlvUploadFileHosters;
        private System.Windows.Forms.Button btnUploadUpload;
        private System.Windows.Forms.GroupBox gbUploadInput;
        private System.Windows.Forms.CheckBox cbUploadInputEnableCompression;
        private System.Windows.Forms.Button btnUploadInputBrowse;
        private System.Windows.Forms.TextBox tbUploadInputDirectory;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.TabPage tpUploads;
        private BrightIdeasSoftware.TreeListView tlvUploads;
        private BrightIdeasSoftware.OLVColumn olvUploadsName;
        private BrightIdeasSoftware.OLVColumn olvUploadsSize;
        private BrightIdeasSoftware.OLVColumn olvUploadsHoster;
        private BrightIdeasSoftware.OLVColumn olvUploadsConnection;
        private BrightIdeasSoftware.OLVColumn olvUploadsGateway;
        private BrightIdeasSoftware.OLVColumn olvUploadsStatus;
        private BrightIdeasSoftware.OLVColumn olvUploadsSpeed;
        private BrightIdeasSoftware.OLVColumn olvUploadsETA;
        private BrightIdeasSoftware.OLVColumn olvUploadsBytesLoaded;
        private BrightIdeasSoftware.OLVColumn olvUploadsProgress;
        private BrightIdeasSoftware.OLVColumn olvUploadsSaveFrom;
        private BrightIdeasSoftware.OLVColumn olvUploadsError;
        private BrightIdeasSoftware.OLVColumn olvUploadsFileUrl;
        private System.Windows.Forms.GroupBox groupBox4;
        private System.Windows.Forms.TabPage tpSettings;
        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.CheckBox cbSettingSpeedLimitEnabled;
        private System.Windows.Forms.Label label15;
        private System.Windows.Forms.NumericUpDown nupSettingsSpeedLimit;
        private System.Windows.Forms.Label label13;
        private System.Windows.Forms.NumericUpDown nupSettingsMaxConcurrentUploadJobs;
        private System.Windows.Forms.NumericUpDown nupSettingsMaxConcurrentCPUJobs;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Button btnSettingsSave;
        private System.Windows.Forms.TabPage tpLogs;
        private System.Windows.Forms.TabControl tcFilesLogs;
        private System.Windows.Forms.CheckBox cbLogAutoScroll;
        private System.Windows.Forms.Button btnUploadsStart;
        private System.Windows.Forms.Button btnUploadsPause;
        private System.Windows.Forms.Button btnUploadsStop;
        private System.Windows.Forms.ImageList ilFileTypes;
        private System.Windows.Forms.Label label16;
        private System.Windows.Forms.TextBox tbUploadInputDirectoryPattern;
        private System.Windows.Forms.Label label17;
        private System.Windows.Forms.TextBox tbUploadInputPackageNamingExpression;
        private System.Windows.Forms.Label label18;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TextBox tbIploadInputPackageNamingResult;
        private System.Windows.Forms.Label label19;
        private System.Windows.Forms.Label label20;
        private System.Windows.Forms.TabPage tpUploaded;
        private BrightIdeasSoftware.TreeListView tlvUploaded;
        private BrightIdeasSoftware.OLVColumn olvUploadedName;
        private BrightIdeasSoftware.OLVColumn olvUploadedSize;
        private BrightIdeasSoftware.OLVColumn olvUploadedHoster;
        private BrightIdeasSoftware.OLVColumn olvUploadedError;
        private BrightIdeasSoftware.OLVColumn olvUploadedFileUrl;
        private BrightIdeasSoftware.OLVColumn olvUploadedAddedDate;
        private BrightIdeasSoftware.OLVColumn olvUploadedFinishedDate;
        private BrightIdeasSoftware.OLVColumn olvUploadedDuration;
        private BrightIdeasSoftware.OLVColumn olvUploadedCompressionPassword;
        private BrightIdeasSoftware.OLVColumn olvUploadedFileCount;
        private BrightIdeasSoftware.OLVColumn olvUploadsAddedDate;
        private BrightIdeasSoftware.OLVColumn olvUploadsFinishedDate;
        private BrightIdeasSoftware.OLVColumn olvUploadsDuration;
        private BrightIdeasSoftware.OLVColumn olvUploadsCompressionPassword;
        private BrightIdeasSoftware.OLVColumn olvUploadsFileCount;
        private BrightIdeasSoftware.OLVColumn olvUploadsBytesRemaining;
        private BrightIdeasSoftware.OLVColumn olvUploadsEnabledDisabled;
    }
}


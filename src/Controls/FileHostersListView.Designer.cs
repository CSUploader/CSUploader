
namespace CSUploader.Controls
{
    partial class FileHostersListView
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

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.olvFileHosters = new BrightIdeasSoftware.ObjectListView();
            this.olvUse = ((BrightIdeasSoftware.OLVColumn)(new BrightIdeasSoftware.OLVColumn()));
            this.olvFileHoster = ((BrightIdeasSoftware.OLVColumn)(new BrightIdeasSoftware.OLVColumn()));
            this.olvFileHosterLogin = ((BrightIdeasSoftware.OLVColumn)(new BrightIdeasSoftware.OLVColumn()));
            this.olvFileHosterLayer = ((BrightIdeasSoftware.OLVColumn)(new BrightIdeasSoftware.OLVColumn()));
            ((System.ComponentModel.ISupportInitialize)(this.olvFileHosters)).BeginInit();
            this.SuspendLayout();
            // 
            // olvFileHosters
            // 
            this.olvFileHosters.AllColumns.Add(this.olvUse);
            this.olvFileHosters.AllColumns.Add(this.olvFileHoster);
            this.olvFileHosters.AllColumns.Add(this.olvFileHosterLogin);
            this.olvFileHosters.AllColumns.Add(this.olvFileHosterLayer);
            this.olvFileHosters.AlternateRowBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(239)))), ((int)(((byte)(246)))), ((int)(((byte)(248)))));
            this.olvFileHosters.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(244)))), ((int)(((byte)(252)))), ((int)(((byte)(254)))));
            this.olvFileHosters.CellEditActivation = BrightIdeasSoftware.ObjectListView.CellEditActivateMode.SingleClickAlways;
            this.olvFileHosters.CellEditUseWholeCell = false;
            this.olvFileHosters.CheckBoxes = true;
            this.olvFileHosters.CheckedAspectName = "";
            this.olvFileHosters.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.olvUse,
            this.olvFileHoster,
            this.olvFileHosterLogin,
            this.olvFileHosterLayer});
            this.olvFileHosters.Cursor = System.Windows.Forms.Cursors.Default;
            this.olvFileHosters.Dock = System.Windows.Forms.DockStyle.Fill;
            this.olvFileHosters.FullRowSelect = true;
            this.olvFileHosters.GridLines = true;
            this.olvFileHosters.HideSelection = false;
            this.olvFileHosters.LabelWrap = false;
            this.olvFileHosters.Location = new System.Drawing.Point(0, 0);
            this.olvFileHosters.MultiSelect = false;
            this.olvFileHosters.Name = "olvFileHosters";
            this.olvFileHosters.SelectColumnsOnRightClick = false;
            this.olvFileHosters.SelectColumnsOnRightClickBehaviour = BrightIdeasSoftware.ObjectListView.ColumnSelectBehaviour.None;
            this.olvFileHosters.ShowGroups = false;
            this.olvFileHosters.Size = new System.Drawing.Size(985, 746);
            this.olvFileHosters.TabIndex = 1;
            this.olvFileHosters.UseAlternatingBackColors = true;
            this.olvFileHosters.UseCompatibleStateImageBehavior = false;
            this.olvFileHosters.View = System.Windows.Forms.View.Details;
            // 
            // olvUse
            // 
            this.olvUse.IsEditable = false;
            this.olvUse.Text = "Use";
            this.olvUse.Width = 39;
            // 
            // olvFileHoster
            // 
            this.olvFileHoster.IsEditable = false;
            this.olvFileHoster.Text = "File Hoster";
            this.olvFileHoster.Width = 155;
            // 
            // olvFileHosterLogin
            // 
            this.olvFileHosterLogin.Text = "Account";
            this.olvFileHosterLogin.Width = 122;
            // 
            // olvFileHosterLayer
            // 
            this.olvFileHosterLayer.Text = "Protocol";
            // 
            // FileHostersListView
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.olvFileHosters);
            this.Name = "FileHostersListView";
            this.Size = new System.Drawing.Size(985, 746);
            ((System.ComponentModel.ISupportInitialize)(this.olvFileHosters)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion

        private BrightIdeasSoftware.ObjectListView olvFileHosters;
        private BrightIdeasSoftware.OLVColumn olvUse;
        private BrightIdeasSoftware.OLVColumn olvFileHoster;
        private BrightIdeasSoftware.OLVColumn olvFileHosterLogin;
        private BrightIdeasSoftware.OLVColumn olvFileHosterLayer;
    }
}

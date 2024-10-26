namespace CSUploader.Views
{
    partial class LogDetailsForm
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
            this.label1 = new System.Windows.Forms.Label();
            this.textBoxDateTime = new System.Windows.Forms.TextBox();
            this.buttonClose = new System.Windows.Forms.Button();
            this.label2 = new System.Windows.Forms.Label();
            this.textBoxFilename = new System.Windows.Forms.TextBox();
            this.label3 = new System.Windows.Forms.Label();
            this.textBoxFunction = new System.Windows.Forms.TextBox();
            this.textBoxLineNumber = new System.Windows.Forms.TextBox();
            this.label4 = new System.Windows.Forms.Label();
            this.textBoxThreadId = new System.Windows.Forms.TextBox();
            this.label5 = new System.Windows.Forms.Label();
            this.label6 = new System.Windows.Forms.Label();
            this.tabControlMessage = new System.Windows.Forms.TabControl();
            this.tagPageText = new System.Windows.Forms.TabPage();
            this.textBoxMessage = new System.Windows.Forms.TextBox();
            this.tabPageHtml = new System.Windows.Forms.TabPage();
            this.webBrowserMessage = new System.Windows.Forms.WebBrowser();
            this.tabControlMessage.SuspendLayout();
            this.tagPageText.SuspendLayout();
            this.tabPageHtml.SuspendLayout();
            this.SuspendLayout();
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(12, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(57, 12);
            this.label1.TabIndex = 0;
            this.label1.Text = "Date/time";
            // 
            // textBoxDateTime
            // 
            this.textBoxDateTime.BackColor = System.Drawing.Color.White;
            this.textBoxDateTime.Location = new System.Drawing.Point(108, 6);
            this.textBoxDateTime.Name = "textBoxDateTime";
            this.textBoxDateTime.ReadOnly = true;
            this.textBoxDateTime.Size = new System.Drawing.Size(161, 19);
            this.textBoxDateTime.TabIndex = 1;
            // 
            // buttonClose
            // 
            this.buttonClose.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.buttonClose.Location = new System.Drawing.Point(713, 415);
            this.buttonClose.Name = "buttonClose";
            this.buttonClose.Size = new System.Drawing.Size(75, 23);
            this.buttonClose.TabIndex = 2;
            this.buttonClose.Text = "Close";
            this.buttonClose.UseVisualStyleBackColor = true;
            this.buttonClose.Click += new System.EventHandler(this.buttonClose_Click);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(12, 34);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(51, 12);
            this.label2.TabIndex = 3;
            this.label2.Text = "Filename";
            // 
            // textBoxFilename
            // 
            this.textBoxFilename.BackColor = System.Drawing.Color.White;
            this.textBoxFilename.Location = new System.Drawing.Point(108, 31);
            this.textBoxFilename.Name = "textBoxFilename";
            this.textBoxFilename.ReadOnly = true;
            this.textBoxFilename.Size = new System.Drawing.Size(292, 19);
            this.textBoxFilename.TabIndex = 2;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(12, 59);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(49, 12);
            this.label3.TabIndex = 5;
            this.label3.Text = "Function";
            // 
            // textBoxFunction
            // 
            this.textBoxFunction.BackColor = System.Drawing.Color.White;
            this.textBoxFunction.Location = new System.Drawing.Point(108, 56);
            this.textBoxFunction.Name = "textBoxFunction";
            this.textBoxFunction.ReadOnly = true;
            this.textBoxFunction.Size = new System.Drawing.Size(292, 19);
            this.textBoxFunction.TabIndex = 3;
            // 
            // textBoxLineNumber
            // 
            this.textBoxLineNumber.BackColor = System.Drawing.Color.White;
            this.textBoxLineNumber.Location = new System.Drawing.Point(108, 81);
            this.textBoxLineNumber.Name = "textBoxLineNumber";
            this.textBoxLineNumber.ReadOnly = true;
            this.textBoxLineNumber.Size = new System.Drawing.Size(100, 19);
            this.textBoxLineNumber.TabIndex = 4;
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(12, 84);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(67, 12);
            this.label4.TabIndex = 8;
            this.label4.Text = "Line number";
            // 
            // textBoxThreadId
            // 
            this.textBoxThreadId.BackColor = System.Drawing.Color.White;
            this.textBoxThreadId.Location = new System.Drawing.Point(108, 106);
            this.textBoxThreadId.Name = "textBoxThreadId";
            this.textBoxThreadId.ReadOnly = true;
            this.textBoxThreadId.Size = new System.Drawing.Size(100, 19);
            this.textBoxThreadId.TabIndex = 5;
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(12, 109);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(55, 12);
            this.label5.TabIndex = 10;
            this.label5.Text = "Thread ID";
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(12, 147);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(50, 12);
            this.label6.TabIndex = 12;
            this.label6.Text = "Message";
            // 
            // tabControlMessage
            // 
            this.tabControlMessage.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tabControlMessage.Controls.Add(this.tagPageText);
            this.tabControlMessage.Controls.Add(this.tabPageHtml);
            this.tabControlMessage.Location = new System.Drawing.Point(14, 162);
            this.tabControlMessage.Name = "tabControlMessage";
            this.tabControlMessage.SelectedIndex = 0;
            this.tabControlMessage.Size = new System.Drawing.Size(774, 247);
            this.tabControlMessage.TabIndex = 13;
            // 
            // tagPageText
            // 
            this.tagPageText.Controls.Add(this.textBoxMessage);
            this.tagPageText.Location = new System.Drawing.Point(4, 22);
            this.tagPageText.Name = "tagPageText";
            this.tagPageText.Padding = new System.Windows.Forms.Padding(3);
            this.tagPageText.Size = new System.Drawing.Size(766, 221);
            this.tagPageText.TabIndex = 0;
            this.tagPageText.Text = "Text";
            this.tagPageText.UseVisualStyleBackColor = true;
            // 
            // textBoxMessage
            // 
            this.textBoxMessage.BackColor = System.Drawing.Color.White;
            this.textBoxMessage.Dock = System.Windows.Forms.DockStyle.Fill;
            this.textBoxMessage.Location = new System.Drawing.Point(3, 3);
            this.textBoxMessage.Multiline = true;
            this.textBoxMessage.Name = "textBoxMessage";
            this.textBoxMessage.ReadOnly = true;
            this.textBoxMessage.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.textBoxMessage.Size = new System.Drawing.Size(760, 215);
            this.textBoxMessage.TabIndex = 7;
            // 
            // tabPageHtml
            // 
            this.tabPageHtml.Controls.Add(this.webBrowserMessage);
            this.tabPageHtml.Location = new System.Drawing.Point(4, 22);
            this.tabPageHtml.Name = "tabPageHtml";
            this.tabPageHtml.Padding = new System.Windows.Forms.Padding(3);
            this.tabPageHtml.Size = new System.Drawing.Size(766, 221);
            this.tabPageHtml.TabIndex = 1;
            this.tabPageHtml.Text = "HTML";
            this.tabPageHtml.UseVisualStyleBackColor = true;
            // 
            // webBrowserMessage
            // 
            this.webBrowserMessage.Dock = System.Windows.Forms.DockStyle.Fill;
            this.webBrowserMessage.Location = new System.Drawing.Point(3, 3);
            this.webBrowserMessage.MinimumSize = new System.Drawing.Size(20, 20);
            this.webBrowserMessage.Name = "webBrowserMessage";
            this.webBrowserMessage.ScriptErrorsSuppressed = true;
            this.webBrowserMessage.Size = new System.Drawing.Size(760, 215);
            this.webBrowserMessage.TabIndex = 0;
            // 
            // LogDetailsForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.tabControlMessage);
            this.Controls.Add(this.label6);
            this.Controls.Add(this.label5);
            this.Controls.Add(this.textBoxThreadId);
            this.Controls.Add(this.label4);
            this.Controls.Add(this.textBoxLineNumber);
            this.Controls.Add(this.textBoxFunction);
            this.Controls.Add(this.label3);
            this.Controls.Add(this.textBoxFilename);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.buttonClose);
            this.Controls.Add(this.textBoxDateTime);
            this.Controls.Add(this.label1);
            this.Name = "LogDetailsForm";
            this.Text = "LogDetails";
            this.Load += new System.EventHandler(this.LogDetailsForm_Load);
            this.tabControlMessage.ResumeLayout(false);
            this.tagPageText.ResumeLayout(false);
            this.tagPageText.PerformLayout();
            this.tabPageHtml.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.TextBox textBoxDateTime;
        private System.Windows.Forms.Button buttonClose;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TextBox textBoxFilename;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.TextBox textBoxFunction;
        private System.Windows.Forms.TextBox textBoxLineNumber;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.TextBox textBoxThreadId;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.TabControl tabControlMessage;
        private System.Windows.Forms.TabPage tagPageText;
        private System.Windows.Forms.TextBox textBoxMessage;
        private System.Windows.Forms.TabPage tabPageHtml;
        private System.Windows.Forms.WebBrowser webBrowserMessage;
    }
}
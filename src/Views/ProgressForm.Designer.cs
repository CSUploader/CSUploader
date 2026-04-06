namespace CSUploader.Views;

partial class ProgressForm
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
        this.components = new System.ComponentModel.Container();
        this.timer1 = new System.Windows.Forms.Timer(this.components);
        this.TextLabel = new System.Windows.Forms.Label();
        this.ProgressBar = new System.Windows.Forms.ProgressBar();
        this.CancelActionButton = new System.Windows.Forms.Button();
        this.SuspendLayout();
        // 
        // TextLabel
        // 
        this.TextLabel.Anchor = System.Windows.Forms.AnchorStyles.None;
        this.TextLabel.Location = new System.Drawing.Point(12, 10);
        this.TextLabel.Name = "TextLabel";
        this.TextLabel.Size = new System.Drawing.Size(129, 54);
        this.TextLabel.TabIndex = 13;
        this.TextLabel.Text = "label1";
        this.TextLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        // 
        // ProgressBar
        // 
        this.ProgressBar.Anchor = System.Windows.Forms.AnchorStyles.None;
        this.ProgressBar.Location = new System.Drawing.Point(27, 73);
        this.ProgressBar.Name = "ProgressBar";
        this.ProgressBar.Size = new System.Drawing.Size(105, 22);
        this.ProgressBar.Style = System.Windows.Forms.ProgressBarStyle.Continuous;
        this.ProgressBar.TabIndex = 11;
        // 
        // CancelActionButton
        // 
        this.CancelActionButton.Anchor = System.Windows.Forms.AnchorStyles.None;
        this.CancelActionButton.Location = new System.Drawing.Point(43, 101);
        this.CancelActionButton.Name = "CancelActionButton";
        this.CancelActionButton.Size = new System.Drawing.Size(75, 25);
        this.CancelActionButton.TabIndex = 12;
        this.CancelActionButton.TabStop = false;
        this.CancelActionButton.Text = "Cancel";
        this.CancelActionButton.UseVisualStyleBackColor = true;
        this.CancelActionButton.Click += new System.EventHandler(this.CancelActionButton_Click);
        // 
        // ProgressForm
        // 
        this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
        this.ClientSize = new System.Drawing.Size(145, 129);
        this.ControlBox = false;
        this.Controls.Add(this.TextLabel);
        this.Controls.Add(this.ProgressBar);
        this.Controls.Add(this.CancelActionButton);
        this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Fixed3D;
        this.Name = "ProgressForm";
        this.ShowIcon = false;
        this.ShowInTaskbar = false;
        this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        this.TopMost = true;
        this.Load += new System.EventHandler(this.ProgressForm_Load);
        this.ResumeLayout(false);

    }

    #endregion

    private System.Windows.Forms.Timer timer1;
    private System.Windows.Forms.Label TextLabel;
    private System.Windows.Forms.ProgressBar ProgressBar;
    private System.Windows.Forms.Button CancelActionButton;
}

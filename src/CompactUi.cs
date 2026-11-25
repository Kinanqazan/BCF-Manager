using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FreedomIssues
{
    internal sealed partial class MainForm
    {
        private readonly ToolTip tips = new ToolTip();
        private readonly ImageList thumbnails = new ImageList { ImageSize = new Size(96, 72), ColorDepth = ColorDepth.Depth32Bit };
        // ImageList defers copying images until its native handle is created.
        // Retain source bitmaps until the image list releases them.
        private readonly List<Bitmap> thumbnailSources = new List<Bitmap>();
        internal event EventHandler BackRequested;
        private CheckBox pinControl;

        internal void ApplyPinState()
        {
            var window = Parent != null ? Parent.FindForm() : this;
            if (window != null && pinControl != null) window.TopMost = pinControl.Checked;
        }

        internal static Button IconButton(string glyph, string label, ToolTip tooltip, bool primary)
        {
            var button = new Button();
            StyleButton(button, glyph, 32, primary);
            button.Font = new Font("Segoe MDL2 Assets", 11);
            button.AccessibleName = label;
            tooltip.SetToolTip(button, label);
            return button;
        }

        private void BuildCompactLayout(string projectName)
        {
            Text = "BCF Manager – " + projectName;
            Font = new Font("Segoe UI", 9);
            ClientSize = new Size(490, Math.Min(900, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 70));
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            MinimumSize = new Size(340, 360);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 251);
            TopMost = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(layout);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            var back = IconButton("\uE72B", "Zurück zu Projekten", tips, false);
            back.Click += delegate { if (BackRequested != null) BackRequested(this, EventArgs.Empty); else Close(); };
            heading.Controls.Add(back, 0, 0);
            heading.Controls.Add(new Label { Text = projectName, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 14), AutoEllipsis = true, ForeColor = Color.FromArgb(24, 40, 63) }, 1, 0);
            layout.Controls.Add(heading, 0, 0);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var add = IconButton("\uE710", "Neues Issue", tips, true);
            ConfigureIcon(edit, "\uE70F", "Issue bearbeiten");
            ConfigureIcon(deleteIssue, "\uE74D", "Issue löschen");
            ConfigureIcon(goToView, "\uE890", "Zur Ansicht (experimentell)");
            tips.SetToolTip(goToView, "Kamera vorbereiten und in Freedom mit OK bestätigen");
            var import = new Button();
            var pin = new CheckBox { Appearance = Appearance.Button, Text = "\uE718", Font = new Font("Segoe MDL2 Assets", 11), Checked = true, FlatStyle = FlatStyle.Flat, Size = new Size(32, 30), TextAlign = ContentAlignment.MiddleCenter, AccessibleName = "Immer im Vordergrund", Margin = new Padding(0) };
            tips.SetToolTip(pin, "Immer im Vordergrund");
            pinControl = pin;
            Action updatePin = delegate {
                ApplyPinState();
                pin.BackColor = pin.Checked ? Color.FromArgb(38, 94, 179) : Color.White;
                pin.ForeColor = pin.Checked ? Color.White : Color.FromArgb(30, 43, 58);
                pin.FlatAppearance.BorderColor = pin.Checked ? Color.FromArgb(38, 94, 179) : Color.FromArgb(210, 219, 229);
            };
            pin.UseVisualStyleBackColor = false;
            pin.CheckedChanged += delegate { updatePin(); };
            updatePin();
            add.Click += delegate { AddIssue(); };
            edit.Click += delegate { EditIssue(); };
            deleteIssue.Click += delegate { DeleteIssue(); };
            goToView.Click += delegate { GoToView(); };
            import.Click += delegate { ImportBcf(); };
            heading.Controls.Add(pin, 2, 0);
            toolbar.Controls.AddRange(new Control[] { add, edit, deleteIssue, goToView });
            layout.Controls.Add(toolbar, 0, 1);
            list.Dock = DockStyle.Fill;
            list.Margin = Padding.Empty;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Color.White;
            list.View = View.Details;
            list.HeaderStyle = ColumnHeaderStyle.None;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.SmallImageList = thumbnails;
            list.Columns.Add("Issues", 370);
            list.OwnerDraw = true;
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = true; };
            list.DrawItem += delegate { };
            list.DrawSubItem += DrawIssueRow;
            list.SizeChanged += delegate { ResizeIssueColumn(); };
            list.SelectedIndexChanged += delegate { ShowSelected(); };
            list.DoubleClick += delegate { EditIssue(); };
            layout.Controls.Add(list, 0, 2);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
            count.Dock = DockStyle.Fill;
            count.TextAlign = ContentAlignment.MiddleLeft;
            StyleButton(pdf, "PDF", 69, false);
            StyleButton(bcf, "BCF", 69, false);
            StyleButton(import, "BCF", 69, false);
            import.Image = MakeExportIcon(true);
            import.TextImageRelation = TextImageRelation.ImageBeforeText;
            import.AccessibleName = "BCF importieren";
            tips.SetToolTip(import, "BCF importieren");
            pdf.Image = MakeExportIcon(false);
            bcf.Image = MakeExportIcon(false);
            pdf.TextImageRelation = bcf.TextImageRelation = TextImageRelation.ImageBeforeText;
            pdf.AccessibleName = "Als PDF exportieren";
            bcf.AccessibleName = "Als BCF exportieren";
            tips.SetToolTip(pdf, "Liste als PDF exportieren");
            tips.SetToolTip(bcf, "Liste als BCF exportieren");
            pdf.Click += delegate { Export(false); };
            bcf.Click += delegate { Export(true); };
            footer.Controls.Add(count, 0, 0);
            footer.Controls.Add(import, 1, 0);
            footer.Controls.Add(pdf, 3, 0);
            footer.Controls.Add(bcf, 4, 0);
            Disposed += delegate { if (import.Image != null) import.Image.Dispose(); };
            layout.Controls.Add(footer, 0, 3);
        }

        private void ConfigureIcon(Button button, string glyph, string label)
        {
            StyleButton(button, glyph, 32, false);
            button.Font = new Font("Segoe MDL2 Assets", 11);
            button.AccessibleName = label;
            tips.SetToolTip(button, label);
        }

        private static Bitmap MakeExportIcon(bool incoming)
        {
            var bitmap = new Bitmap(20, 20);
            using (var g = Graphics.FromImage(bitmap))
            using (var pen = new Pen(Color.FromArgb(45, 87, 147), 1.6f))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLines(pen, new[] { new Point(3, 12), new Point(3, 16), new Point(17, 16), new Point(17, 12) });
                g.DrawLine(pen, 10, 12, 10, 3);
                if (incoming) g.DrawLines(pen, new[] { new Point(6, 8), new Point(10, 12), new Point(14, 8) });
                else g.DrawLines(pen, new[] { new Point(6, 7), new Point(10, 3), new Point(14, 7) });
            }
            return bitmap;
        }

        private void ResizeIssueColumn()
        {
            if (list.Columns.Count == 0) return;
            int vertical = list.Items.Count * (thumbnails.ImageSize.Height + 2) > list.ClientSize.Height ? SystemInformation.VerticalScrollBarWidth : 0;
            list.Columns[0].Width = Math.Max(1, list.ClientSize.Width - vertical - 4);
        }

        private int AddThumbnail(Issue issue)
        {
            var image = new Bitmap(96, 72);
            try
            {
            using (var g = Graphics.FromImage(image))
            {
                g.Clear(Color.FromArgb(238, 242, 247));
                using (var source = ReadSnapshot(issue.Snapshot))
                {
                    if (source != null)
                    {
                        double scale = Math.Min(96.0 / source.Width, 72.0 / source.Height);
                        int width = Math.Max(1, (int)(source.Width * scale)), height = Math.Max(1, (int)(source.Height * scale));
                        g.DrawImage(source, (96 - width) / 2, (72 - height) / 2, width, height);
                    }
                    else
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using (var pen = new Pen(Color.FromArgb(151, 166, 183), 1.8f))
                        {
                            g.DrawRectangle(pen, 33, 23, 30, 25);
                            g.DrawEllipse(pen, 53, 28, 4, 4);
                            g.DrawLines(pen, new[] { new Point(35, 44), new Point(43, 35), new Point(48, 40), new Point(52, 36), new Point(61, 45) });
                        }
                    }
                }
                thumbnails.Images.Add(image);
            }
            thumbnailSources.Add(image);
            return thumbnails.Images.Count - 1;
            }
            catch { image.Dispose(); throw; }
        }

        private void ClearThumbnails()
        {
            thumbnails.Images.Clear();
            foreach (var image in thumbnailSources) image.Dispose();
            thumbnailSources.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                if (viewDialogTimer != null) viewDialogTimer.Dispose();
                thumbnails.Dispose();
                if (pdf.Image != null) pdf.Image.Dispose();
                if (bcf.Image != null) bcf.Image.Dispose();
                foreach (var image in thumbnailSources) image.Dispose();
                thumbnailSources.Clear();
                tips.Dispose();
            }
        }

        private void DrawIssueRow(object sender, DrawListViewSubItemEventArgs e)
        {
            var issue = (Issue)e.Item.Tag;
            bool selected = e.Item.Selected;
            var bounds = e.Bounds;
            using (var background = new SolidBrush(selected ? Color.FromArgb(229, 239, 253) : Color.White)) e.Graphics.FillRectangle(background, bounds);
            if (e.ItemIndex >= 0 && e.ItemIndex < thumbnails.Images.Count)
                thumbnails.Draw(e.Graphics, bounds.Left + 8, bounds.Top + 1, e.ItemIndex);
            int left = bounds.Left + 116, width = Math.Max(20, bounds.Width - 126);
            using (var bold = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, issue.Title, bold, new Rectangle(left, bounds.Top + 6, width, 22), Color.FromArgb(24, 40, 63), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            using (var small = new Font(Font.FontFamily, 8f)) {
            TextRenderer.DrawText(e.Graphics, issue.Status ?? "", small, new Rectangle(left, bounds.Top + 29, width, 19), StatusColor(issue.Status), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int commentCount = issue.Comments == null ? 0 : issue.Comments.Count;
            string subtitle = commentCount == 1 ? "1 Kommentar" : commentCount + " Kommentare";
            TextRenderer.DrawText(e.Graphics, subtitle, small, new Rectangle(left, bounds.Top + 50, width, 19), Color.SlateGray, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
            e.Graphics.DrawLine(Pens.WhiteSmoke, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        }

        internal static Color StatusColor(string value)
        {
            string name = (value ?? "").Trim().ToLowerInvariant();
            if (name == "erledigt" || name == "closed" || name == "resolved" || name == "geschlossen" || name == "gelöst") return Color.FromArgb(30, 130, 83);
            if (name == "in bearbeitung" || name == "in progress" || name == "pending") return Color.FromArgb(175, 104, 15);
            if (name == "offen" || name == "open" || name == "active" || name == "aktiv") return Color.FromArgb(42, 99, 176);
            return Color.FromArgb(93, 104, 119);
        }
    }

    internal sealed partial class IssueForm
    {
        private readonly ToolTip editorTips = new ToolTip();
        private readonly List<IssueComment> comments = new List<IssueComment>();
        private readonly RichTextBox commentHistory = new RichTextBox();
        private readonly TextBox commentDraft = new TextBox();
        private TabPage commentsPage;

        private void BuildCompactEditor(string captureHint)
        {
            comments.AddRange(original.Comments ?? new List<IssueComment>());
            Text = String.IsNullOrEmpty(original.Title) ? "Neues Issue" : "Issue bearbeiten";
            Font = new Font("Segoe UI", 9);
            StartPosition = FormStartPosition.CenterParent;
            int editorSize = Math.Min(850, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 70);
            ClientSize = new Size(editorSize, editorSize);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            MinimumSize = new Size(500, 510);
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(245, 247, 251);
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2 };
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(outer);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var detailPage = new TabPage("Details") { BackColor = BackColor, Padding = new Padding(8) };
            commentsPage = new TabPage("Kommentare") { BackColor = BackColor, Padding = new Padding(8) };
            tabs.TabPages.AddRange(new[] { detailPage, commentsPage });
            outer.Controls.Add(tabs, 0, 0);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            foreach (int height in new[] { 24, 37, 26, 120, 66 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detailPage.Controls.Add(layout);
            title.Dock = description.Dock = DockStyle.Fill;
            title.Text = original.Title ?? "";
            title.MaxLength = 160;
            description.Text = original.Description ?? "";
            description.Multiline = true;
            description.ScrollBars = ScrollBars.Vertical;
            description.MaxLength = 12000;
            title.BorderStyle = description.BorderStyle = BorderStyle.FixedSingle;
            status.DropDownStyle = ComboBoxStyle.DropDownList;
            status.DrawMode = DrawMode.OwnerDrawFixed;
            status.ItemHeight = 22;
            status.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                e.DrawBackground();
                if (e.Index >= 0)
                {
                    string value = status.Items[e.Index].ToString();
                    Color color = MainForm.StatusColor(value);
                    using (var brush = new SolidBrush(color)) e.Graphics.FillEllipse(brush, e.Bounds.Left + 5, e.Bounds.Top + (e.Bounds.Height - 8) / 2, 8, 8);
                    TextRenderer.DrawText(e.Graphics, value, e.Font, new Rectangle(e.Bounds.Left + 18, e.Bounds.Top, e.Bounds.Width - 20, e.Bounds.Height),
                        (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                }
                e.DrawFocusRectangle();
            };
            status.Items.AddRange(new object[] { "Offen", "In Bearbeitung", "Erledigt" });
            if (!String.IsNullOrEmpty(original.Status) && !status.Items.Contains(original.Status)) status.Items.Add(original.Status);
            status.SelectedItem = original.Status;
            if (status.SelectedIndex < 0) status.SelectedIndex = 0;
            layout.Controls.Add(new Label { Text = "Titel", AutoSize = true, Margin = new Padding(3, 3, 3, 0) }, 0, 0);
            layout.Controls.Add(title, 0, 1);
            layout.Controls.Add(new Label { Text = "Beschreibung", AutoSize = true, Margin = new Padding(3, 5, 3, 0) }, 0, 2);
            layout.Controls.Add(description, 0, 3);
            preview.Dock = DockStyle.Fill;
            preview.BackColor = Color.FromArgb(235, 240, 247);
            preview.SizeMode = PictureBoxSizeMode.Zoom;
            var people = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
            people.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            people.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            people.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            people.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            people.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            people.Controls.Add(new Label { Text = "Status", Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 0) }, 0, 0);
            people.Controls.Add(new Label { Text = "Erstellt von", Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 0) }, 1, 0);
            people.Controls.Add(new Label { Text = "Zugewiesen", Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 0) }, 2, 0);
            status.Dock = DockStyle.Fill;
            ConfigurePersonMenu(creator, original.Author);
            ConfigurePersonMenu(assignee, original.AssignedTo);
            people.Controls.Add(status, 0, 1);
            people.Controls.Add(creator, 1, 1);
            people.Controls.Add(assignee, 2, 1);
            layout.Controls.Add(people, 0, 4);
            layout.Controls.Add(preview, 0, 5);
            var actionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 6, 0, 0), Margin = Padding.Empty };
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var imageActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var refresh = MainForm.IconButton("\uE722", "Screenshot und Kamera aktualisieren", editorTips, false);
            var upload = MainForm.IconButton("\uE723", "Bild hochladen", editorTips, false);
            var paste = MainForm.IconButton("\uE77F", "Bild aus Zwischenablage", editorTips, false);
            var manual = MainForm.IconButton("\uE713", "Kamera manuell eingeben", editorTips, false);
            refresh.Click += delegate { RefreshScreenshot(); };
            upload.Click += delegate { UploadImage(); };
            paste.Click += delegate { PasteImage(); };
            manual.Click += delegate { EditCamera(); };
            imageActions.Controls.AddRange(new Control[] { refresh, upload, paste, manual });
            actionRow.Controls.Add(imageActions, 0, 0);
            var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var save = MainForm.IconButton("\uE74E", "Issue speichern", editorTips, true);
            var cancel = MainForm.IconButton("\uE711", "Abbrechen", editorTips, false);
            save.Click += delegate { SaveIssue(); };
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;
            // No AcceptButton: Enter must remain available for multiline comments.
            footer.Controls.AddRange(new Control[] { save, cancel });
            actionRow.Controls.Add(footer, 1, 0);
            outer.Controls.Add(actionRow, 0, 1);
            BuildComments();
            SetSnapshot(original.Snapshot, captureHint);
            if (!String.IsNullOrEmpty(captureHint) && captureHint != "Screenshot und Kamera automatisch erfasst.")
                Shown += delegate { MessageBox.Show(this, captureHint, "Kamera", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            Shown += delegate { title.Focus(); };
        }

        private void ConfigurePersonMenu(ComboBox menu, string existing)
        {
            menu.Dock = DockStyle.Fill;
            menu.DropDownStyle = ComboBoxStyle.DropDownList;
            menu.Items.Add(menu == assignee ? "Nicht zugewiesen" : "Nicht ausgewählt");
            foreach (string person in stakeholders.Where(person => !String.IsNullOrWhiteSpace(person) && !String.Equals(person, "Freedom Issues", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.CurrentCultureIgnoreCase))
                menu.Items.Add(person);
            if (String.Equals(existing, "Freedom Issues", StringComparison.OrdinalIgnoreCase)) existing = "";
            if (!String.IsNullOrWhiteSpace(existing) && !menu.Items.Cast<string>().Skip(1).Any(person => String.Equals(person, existing, StringComparison.CurrentCultureIgnoreCase))) menu.Items.Add(existing);
            var match = menu.Items.Cast<string>().Skip(1).FirstOrDefault(person => String.Equals(person, existing ?? "", StringComparison.CurrentCultureIgnoreCase));
            menu.SelectedIndex = match == null ? 0 : menu.Items.IndexOf(match);
        }

        private void BuildComments()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            commentsPage.Controls.Add(layout);
            commentHistory.Dock = DockStyle.Fill;
            commentHistory.ReadOnly = true;
            commentHistory.BackColor = Color.White;
            commentHistory.BorderStyle = BorderStyle.None;
            commentHistory.DetectUrls = false;
            layout.Controls.Add(commentHistory, 0, 0);
            layout.Controls.Add(new Label { Text = "Neuer Kommentar", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 1);
            commentDraft.Multiline = true;
            commentDraft.Dock = DockStyle.Fill;
            commentDraft.ScrollBars = ScrollBars.Vertical;
            commentDraft.MaxLength = 12000;
            layout.Controls.Add(commentDraft, 0, 2);
            var add = MainForm.IconButton("\uE710", "Kommentar hinzufügen (mit Issue speichern)", editorTips, true);
            add.Margin = new Padding(0, 5, 0, 0);
            add.Click += delegate { AddPendingComment(); };
            layout.Controls.Add(add, 0, 3);
            RefreshComments();
        }

        private bool AddPendingComment()
        {
            if (String.IsNullOrWhiteSpace(commentDraft.Text)) return true;
            if (String.IsNullOrWhiteSpace(currentUser))
            {
                MessageBox.Show(this, "Bitte zuerst im Projektmenü unter den allgemeinen Einstellungen bei ‚Ich bin‘ eine Person auswählen. Der Kommentar bleibt erhalten.", "Kommentarautor auswählen", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            comments.Add(new IssueComment { Id = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow, Author = currentUser, Text = commentDraft.Text.Trim() });
            commentDraft.Clear();
            RefreshComments();
            return true;
        }

        private void RefreshComments()
        {
            commentsPage.Text = "Kommentare (" + comments.Count + ")";
            commentHistory.Text = comments.Count == 0 ? "Noch keine Kommentare." : String.Join("\n\n────────────────────\n\n", comments.OrderBy(c => c.CreatedUtc).Select(c =>
                c.Author + " · " + c.CreatedUtc.ToLocalTime().ToString("g") + "\n" + (String.IsNullOrEmpty(c.Text) ? "[Ansicht ohne Kommentartext]" : c.Text)));
        }
    }
}

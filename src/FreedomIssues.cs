using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace FreedomIssues
{
    public sealed class Issue
    {
        public Issue() { Comments = new List<IssueComment>(); }
        public List<IssueComment> Comments { get; set; }
        public Guid Id { get; set; }
        public Guid SnapshotId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Author { get; set; }
        public string AssignedTo { get; set; }
        public byte[] Snapshot { get; set; }
        public CameraView Camera { get; set; }
    }

    public sealed class IssueComment
    {
        public Guid Id { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Author { get; set; }
        public string Text { get; set; }
        public Guid ViewpointId { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public string ModifiedAuthor { get; set; }
    }

    public sealed class CameraView
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public double TargetZ { get; set; }
        public double HorizontalFov { get; set; }
        public bool HasUpVector { get; set; }
        public double UpX { get; set; }
        public double UpY { get; set; }
        public double UpZ { get; set; }
        public bool IsOrthographic { get; set; }
        public double ViewToWorldScale { get; set; }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--self-test")
            {
                ExportSmokeTest.Run(args[1]);
                return;
            }

            if (args.Length == 2 && args[0] == "--capture-test")
            {
                NativeCapture.EnableDpiAwareness();
                IntPtr window = NativeCapture.FindFreedom();
                if (window == IntPtr.Zero) throw new InvalidOperationException("No visible Freedom window.");
                File.WriteAllBytes(args[1], NativeCapture.CaptureWindow(window));
                return;
            }

            NativeCapture.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs eventArgs)
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "last-error.txt");
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, DateTime.UtcNow.ToString("o") + Environment.NewLine + eventArgs.Exception); }
                catch { }
                MessageBox.Show(eventArgs.Exception.Message + Environment.NewLine + Environment.NewLine + "Fehlerdetails: " + path,
                    "BCF Manager – Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            };
            bool firstInstance;
            using (var mutex = new Mutex(true, @"Local\FreedomIssuesPrototype", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("Freedom Issues ist bereits geöffnet.", "Freedom Issues");
                    return;
                }

                try { Application.Run(new ProjectForm()); }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message, "Freedom Issues", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    internal sealed class IssueStore
    {
        private readonly string path;
        private readonly XmlSerializer serializer = new XmlSerializer(typeof(List<Issue>));

        public IssueStore(string path) { this.path = path; }

        public static IssueStore ForCurrentUser()
        {
            var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            var store = new IssueStore(Path.Combine(folder, "issues.xml"));
            var legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreedomIssues", "issues.xml");
            if (!File.Exists(store.path) && File.Exists(legacyPath))
                store.Save(new IssueStore(legacyPath).Load());
            return store;
        }

        public List<Issue> Load()
        {
            if (!File.Exists(path)) return new List<Issue>();
            using (var stream = File.OpenRead(path))
                return (List<Issue>)serializer.Deserialize(stream);
        }

        public void Save(List<Issue> issues)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AtomicFile.Write(path, stream => serializer.Serialize(stream, issues));
        }
    }

    public sealed class ProjectInfo
    {
        public ProjectInfo() { Stakeholders = new List<string>(); }
        public Guid Id { get; set; }
        public string Name { get; set; }
        public List<string> Stakeholders { get; set; }
    }

    internal static class CurrentUserSettings
    {
        private static string PathName { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BCF Manager", "current-user.txt"); } }
        public static string Load() { return File.Exists(PathName) ? File.ReadAllText(PathName).Trim() : ""; }
        public static void Save(string name)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName));
            AtomicFile.Write(PathName, stream => { using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1024, true)) { writer.Write(name ?? ""); writer.Flush(); } });
        }
    }

    internal sealed class ProjectForm : Form
    {
        private readonly string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private readonly string indexPath;
        private readonly string sharedPeoplePath;
        private readonly XmlSerializer serializer = new XmlSerializer(typeof(List<ProjectInfo>));
        private readonly ListBox projectList = new ListBox();
        private Control projectPage;
        private List<ProjectInfo> projects;

        public ProjectForm()
        {
            indexPath = Path.Combine(root, "projects.xml");
            sharedPeoplePath = Path.Combine(root, "stakeholders.xml");
            Directory.CreateDirectory(root);
            if (File.Exists(indexPath))
            {
                using (var stream = File.OpenRead(indexPath)) projects = (List<ProjectInfo>)serializer.Deserialize(stream);
            }
            else
            {
                projects = new List<ProjectInfo>();
                string existing = Path.Combine(root, "issues.xml");
                if (File.Exists(existing))
                {
                    var project = new ProjectInfo { Id = Guid.NewGuid(), Name = "Bestehende Issues" };
                    new IssueStore(ProjectPath(project)).Save(new IssueStore(existing).Load());
                    projects.Add(project);
                }
                SaveProjects(projects);
            }

            Text = "BCF Manager – Projekte";
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(490, Math.Min(900, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 70));
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            MinimumSize = new Size(340, 360);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 251);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            Controls.Add(layout);
            projectPage = layout;
            layout.Controls.Add(new Label { Text = "BCF Manager", Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 18), ForeColor = Color.FromArgb(24, 40, 63) }, 0, 0);
            layout.Controls.Add(new Label { Text = "Projekte", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(80, 101, 117) }, 0, 1);
            projectList.Dock = DockStyle.Fill;
            projectList.BorderStyle = BorderStyle.None;
            projectList.IntegralHeight = false;
            projectList.DrawMode = DrawMode.OwnerDrawFixed;
            projectList.ItemHeight = 64;
            projectList.BackColor = Color.White;
            projectList.DrawItem += DrawProject;
            projectList.DisplayMember = "Name";
            projectList.DoubleClick += delegate { OpenProject(); };
            layout.Controls.Add(projectList, 0, 2);
            var actionBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 10, 0, 0) };
            actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var create = new Button();
            var tooltips = new ToolTip();
            var rename = MainForm.IconButton("\uE70F", "Projekteinstellungen und Beteiligte", tooltips, false);
            var delete = MainForm.IconButton("\uE74D", "Projekt löschen", tooltips, false);
            MainForm.StyleButton(create, "+  Neues Projekt", 130, true);
            create.Click += delegate { CreateProject(); };
            var settings = MainForm.IconButton("\uE713", "Allgemeine Einstellungen und Benutzer", tooltips, false);
            rename.Click += delegate { RenameProject(false); };
            settings.Click += delegate { RenameProject(true); };
            delete.Click += delegate { DeleteProject(); };
            actions.Controls.AddRange(new Control[] { create, rename, delete });
            actionBar.Controls.Add(actions, 0, 0);
            actionBar.Controls.Add(settings, 1, 0);
            FormClosed += delegate { tooltips.Dispose(); };
            layout.Controls.Add(actionBar, 0, 3);
            RefreshProjects(null);
        }

        private void DrawProject(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= projects.Count) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var background = new SolidBrush(selected ? Color.FromArgb(235, 242, 253) : Color.White)) e.Graphics.FillRectangle(background, e.Bounds);
            Rectangle badge = new Rectangle(e.Bounds.Left + 14, e.Bounds.Top + (e.Bounds.Height - 40) / 2, 40, 40);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Color.FromArgb(232, 241, 252))) e.Graphics.FillEllipse(brush, badge);
            using (var pen = new Pen(Color.FromArgb(38, 94, 179), 1.8f))
            {
                int x = badge.Left + 10, y = badge.Top + 11;
                e.Graphics.DrawLines(pen, new[] { new Point(x, y + 4), new Point(x + 6, y + 4), new Point(x + 8, y + 7), new Point(x + 20, y + 7), new Point(x + 20, y + 18), new Point(x, y + 18), new Point(x, y + 4) });
            }
            TextRenderer.DrawText(e.Graphics, projects[e.Index].Name, Font, new Rectangle(e.Bounds.Left + 70, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 90), e.Bounds.Height), Color.FromArgb(24, 40, 63), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            using (var line = new Pen(Color.FromArgb(235, 239, 245))) e.Graphics.DrawLine(line, e.Bounds.Left + 70, e.Bounds.Bottom - 1, e.Bounds.Right - 15, e.Bounds.Bottom - 1);
            e.DrawFocusRectangle();
        }

        private string ProjectPath(ProjectInfo project)
        {
            return Path.Combine(root, "projects", project.Id.ToString("D"), "issues.xml");
        }

        private void SaveProjects(List<ProjectInfo> list)
        {
            AtomicFile.Write(indexPath, stream => serializer.Serialize(stream, list));
        }

        private List<string> SharedPeople()
        {
            var people = new List<string>();
            if (File.Exists(sharedPeoplePath))
                using (var stream = File.OpenRead(sharedPeoplePath)) people = (List<string>)new XmlSerializer(typeof(List<string>)).Deserialize(stream);
            if (!people.Any(person => String.Equals(person, "BIM Koordinator", StringComparison.CurrentCultureIgnoreCase))) people.Insert(0, "BIM Koordinator");
            return people;
        }

        private void RefreshProjects(ProjectInfo selected)
        {
            projectList.DataSource = null;
            projectList.DataSource = projects;
            projectList.DisplayMember = "Name";
            if (selected != null) projectList.SelectedItem = selected;
        }

        private void CreateProject()
        {
            using (var dialog = new Form { Text = "Neues Projekt", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(430, 190), MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog, Font = Font, AutoScaleMode = AutoScaleMode.Font })
            {
                var name = new TextBox { Left = 20, Top = 42, Width = 390, MaxLength = 120 };
                var save = new Button { Text = "Erstellen", Left = 20, Top = 95, Width = 140, Height = 36, DialogResult = DialogResult.OK };
                dialog.Controls.Add(new Label { Text = "Projektname", Left = 20, Top = 15, AutoSize = true });
                dialog.Controls.Add(name);
                dialog.Controls.Add(save);
                dialog.AcceptButton = save;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string value = name.Text.Trim();
                if (value.Length == 0) { MessageBox.Show(this, "Bitte einen Projektnamen eingeben."); return; }
                if (projects.Any(p => String.Equals(p.Name, value, StringComparison.CurrentCultureIgnoreCase))) { MessageBox.Show(this, "Ein Projekt mit diesem Namen existiert bereits."); return; }
                var project = new ProjectInfo { Id = Guid.NewGuid(), Name = value };
                var updated = new List<ProjectInfo>(projects) { project };
                try { new IssueStore(ProjectPath(project)).Save(new List<Issue>()); SaveProjects(updated); projects = updated; RefreshProjects(project); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Projekt konnte nicht erstellt werden"); }
            }
        }

        private void OpenProject()
        {
            var project = projectList.SelectedItem as ProjectInfo;
            if (project == null) return;
            try
            {
                var people = SharedPeople().Concat(project.Stakeholders ?? new List<string>()).Where(person => !String.IsNullOrWhiteSpace(person)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                var form = new MainForm(new IssueStore(ProjectPath(project)), project.Name, people, CurrentUserSettings.Load());
                form.TopLevel = false;
                form.FormBorderStyle = FormBorderStyle.None;
                form.Dock = DockStyle.Fill;
                form.BackRequested += delegate {
                    Controls.Remove(form);
                    form.Dispose();
                    Text = "BCF Manager – Projekte";
                    projectPage.Visible = true;
                    projectPage.BringToFront();
                };
                Controls.Add(form);
                Text = "BCF Manager – " + project.Name;
                projectPage.Visible = false;
                form.Show();
                form.BringToFront();
                form.ApplyPinState();
            }
            catch (Exception error) { MessageBox.Show(error.Message, "Projekt konnte nicht geöffnet werden"); }
        }

        private void RenameProject(bool shared)
        {
            var project = shared ? null : projectList.SelectedItem as ProjectInfo;
            if (!shared && project == null) return;
            using (var dialog = new Form { Text = shared ? "Allgemeine Einstellungen" : "Projekteinstellungen", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(460, 400), MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog, Font = Font, AutoScaleMode = AutoScaleMode.Font })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                var name = new TextBox { Dock = DockStyle.Fill, MaxLength = 120, Text = shared ? "" : project.Name };
                var people = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
                var commonPeople = SharedPeople();
                var commonNames = new HashSet<string>(commonPeople, StringComparer.CurrentCultureIgnoreCase);
                var originalLocal = new HashSet<string>(shared ? new List<string>() : (project.Stakeholders ?? new List<string>()), StringComparer.CurrentCultureIgnoreCase);
                foreach (var person in shared ? commonPeople : commonPeople.Concat(project.Stakeholders ?? new List<string>()).Distinct(StringComparer.CurrentCultureIgnoreCase)) people.Items.Add(person);
                if (!shared)
                {
                    people.DrawMode = DrawMode.OwnerDrawFixed;
                    people.ItemHeight = 29;
                    people.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                        e.DrawBackground();
                        if (e.Index < 0) return;
                        string person = (string)people.Items[e.Index];
                        bool isShared = commonNames.Contains(person);
                        bool selected = (e.State & DrawItemState.Selected) != 0;
                        var textColor = selected ? SystemColors.HighlightText : Color.FromArgb(30, 45, 62);
                        string tag = isShared ? "Allgemein · geschützt" : "Projekt";
                        TextRenderer.DrawText(e.Graphics, person, e.Font, new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, Math.Max(10, e.Bounds.Width - 155), e.Bounds.Height), textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(e.Graphics, tag, e.Font, new Rectangle(e.Bounds.Right - 148, e.Bounds.Top, 140, e.Bounds.Height), selected ? textColor : (isShared ? Color.FromArgb(75, 105, 140) : Color.FromArgb(35, 130, 103)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                        e.DrawFocusRectangle();
                    };
                }
                var currentUser = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                Action updateUsers = delegate {
                    string chosen = currentUser.SelectedIndex > 0 ? (string)currentUser.SelectedItem : "";
                    currentUser.Items.Clear();
                    currentUser.Items.Add("Nicht ausgewählt");
                    foreach (string item in people.Items) currentUser.Items.Add(item);
                    int index = currentUser.Items.Cast<string>().ToList().FindIndex(1, person => String.Equals(person, chosen, StringComparison.CurrentCultureIgnoreCase));
                    currentUser.SelectedIndex = index < 0 ? 0 : index;
                };
                if (shared) { updateUsers(); string existingUser = CurrentUserSettings.Load(); int selected = currentUser.Items.Cast<string>().ToList().FindIndex(1, person => String.Equals(person, existingUser, StringComparison.CurrentCultureIgnoreCase)); currentUser.SelectedIndex = selected < 0 ? 0 : selected; }
                var entry = new TextBox { Dock = DockStyle.Fill, MaxLength = 200 };
                var addPerson = new Button { Text = "+", Width = 38, Height = 30, AccessibleName = "Person hinzufügen" };
                var removePerson = new Button { Text = "−", Width = 38, Height = 30, AccessibleName = "Ausgewählte Person entfernen" };
                Action add = delegate {
                    string person = entry.Text.Trim();
                    if (person.Length == 0) return;
                    if (!people.Items.Cast<string>().Any(item => String.Equals(item, person, StringComparison.CurrentCultureIgnoreCase))) people.Items.Add(person);
                    if (shared) updateUsers();
                    entry.Clear(); entry.Focus();
                };
                addPerson.Click += delegate { add(); };
                entry.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { add(); e.Handled = true; e.SuppressKeyPress = true; } };
                removePerson.Click += delegate {
                    if (people.SelectedIndex < 0) return;
                    if (shared && String.Equals(people.SelectedItem.ToString(), "BIM Koordinator", StringComparison.CurrentCultureIgnoreCase)) { MessageBox.Show(dialog, "BIM Koordinator ist die Standardzuweisung und bleibt verfügbar."); return; }
                    if (!shared && commonNames.Contains(people.SelectedItem.ToString())) { MessageBox.Show(dialog, "Diese Person gilt für alle Projekte und kann nur in den allgemeinen Einstellungen geändert werden.", "Allgemeine Person", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    people.Items.RemoveAt(people.SelectedIndex);
                    if (shared) updateUsers();
                };
                var personEntry = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
                personEntry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                personEntry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
                personEntry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
                personEntry.Controls.Add(entry, 0, 0); personEntry.Controls.Add(addPerson, 1, 0); personEntry.Controls.Add(removePerson, 2, 0);
                var save = new Button { Text = "Speichern", Width = 140, Height = 34, DialogResult = DialogResult.OK };
                if (!shared) { layout.Controls.Add(new Label { Text = "Projektname", Dock = DockStyle.Fill }, 0, 0); layout.Controls.Add(name, 0, 1); }
                else { layout.Controls.Add(new Label { Text = "Ich bin", Dock = DockStyle.Fill }, 0, 0); layout.Controls.Add(currentUser, 0, 1); }
                layout.Controls.Add(new Label { Text = shared ? "Personen für alle Projekte" : "Personen · Allgemein oder Projekt", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 2);
                layout.Controls.Add(personEntry, 0, 3);
                layout.Controls.Add(people, 0, 4);
                layout.Controls.Add(save, 0, 5);
                dialog.Controls.Add(layout);
                dialog.AcceptButton = save;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (shared)
                {
                    if (!String.IsNullOrWhiteSpace(entry.Text)) add();
                    var sharedList = people.Items.Cast<string>().ToList();
                    string selectedUser = currentUser.SelectedIndex <= 0 ? "" : (string)currentUser.SelectedItem;
                    try { AtomicFile.Write(sharedPeoplePath, stream => new XmlSerializer(typeof(List<string>)).Serialize(stream, sharedList)); CurrentUserSettings.Save(selectedUser); }
                    catch (Exception error) { MessageBox.Show(this, error.Message, "Speichern fehlgeschlagen"); }
                    return;
                }
                string value = name.Text.Trim();
                if (value.Length == 0) { MessageBox.Show(this, "Bitte einen Projektnamen eingeben."); return; }
                if (projects.Any(p => p.Id != project.Id && String.Equals(p.Name, value, StringComparison.CurrentCultureIgnoreCase))) { MessageBox.Show(this, "Ein Projekt mit diesem Namen existiert bereits."); return; }
                string previousName = project.Name;
                var previousPeople = project.Stakeholders;
                project.Name = value;
                if (!String.IsNullOrWhiteSpace(entry.Text)) add();
                project.Stakeholders = people.Items.Cast<string>().Where(person => !commonNames.Contains(person) || originalLocal.Contains(person)).ToList();
                try { SaveProjects(projects); RefreshProjects(project); }
                catch (Exception error) { project.Name = previousName; project.Stakeholders = previousPeople; MessageBox.Show(this, error.Message, "Speichern fehlgeschlagen"); }
            }
        }

        private void DeleteProject()
        {
            var project = projectList.SelectedItem as ProjectInfo;
            if (project == null) return;
            if (MessageBox.Show(this, "Projekt „" + project.Name + "“ und alle enthaltenen Issues löschen?\n\nEine lokale Sicherung bleibt unter data\\Papierkorb erhalten.", "Projekt löschen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            string folder = Path.GetDirectoryName(ProjectPath(project));
            string trash = Path.Combine(root, "Papierkorb", project.Id.ToString("D") + "-" + Guid.NewGuid().ToString("N"));
            var updated = projects.Where(p => p.Id != project.Id).ToList();
            bool moved = false;
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(trash));
                    Directory.Move(folder, trash);
                    moved = true;
                }
                try { SaveProjects(updated); }
                catch { if (moved) Directory.Move(trash, folder); throw; }
                projects = updated;
                RefreshProjects(null);
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Löschen fehlgeschlagen"); }
        }
    }

    internal static class AtomicFile
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string source, string destination, uint flags);

        public static void Write(string path, Action<FileStream> write)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                {
                    write(stream);
                    stream.Flush(true);
                }

                const uint replaceExisting = 1;
                const uint writeThrough = 8;
                if (!MoveFileEx(temp, path, replaceExisting | writeThrough))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    internal static class NativeCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();

        public static void EnableDpiAwareness()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }
        }

        public static IntPtr FindFreedom()
        {
            var foreground = GetForegroundWindow();
            var visible = new List<IntPtr>();
            foreach (var process in Process.GetProcessesByName("Roamer"))
            {
                using (process)
                {
                    IntPtr window = process.MainWindowHandle;
                    if (window == IntPtr.Zero || IsIconic(window)) continue;
                    string title = process.MainWindowTitle;
                    if (title.IndexOf("Freedom", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (window == foreground) return window;
                    visible.Add(window);
                }
            }

            if (visible.Count > 1)
                throw new InvalidOperationException("Mehrere Freedom-Fenster sind geöffnet. Minimiere die anderen Instanzen und versuche es erneut.");
            return visible.FirstOrDefault();
        }

        public static byte[] CaptureWindow(IntPtr window)
        {
            Rect bounds;
            if (!GetWindowRect(window, out bounds) || IsIconic(window))
                throw new InvalidOperationException("Das Freedom-Fenster ist nicht sichtbar.");

            var area = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            if (area.Width < 32 || area.Height < 32 || area.Width > 16000 || area.Height > 16000)
                throw new InvalidOperationException("Die Größe des Freedom-Fensters konnte nicht ermittelt werden.");

            // PrintWindow isolates the target window. Screen capture is the fallback for GPU viewports.
            using (var image = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
            {
                bool rendered;
                using (var graphics = Graphics.FromImage(image))
                {
                    IntPtr dc = graphics.GetHdc();
                    try { rendered = PrintWindow(window, dc, 2); }
                    finally { graphics.ReleaseHdc(dc); }
                }

                if (rendered && HasImageContent(image)) return ExtractView(image, area, window);
            }

            SetForegroundWindow(window);
            DwmFlush();
            if (GetForegroundWindow() != window)
                throw new InvalidOperationException("Freedom konnte für die Aufnahme nicht in den Vordergrund geholt werden.");

            area.Intersect(SystemInformation.VirtualScreen);
            if (area.Width < 32 || area.Height < 32)
                throw new InvalidOperationException("Das Freedom-Fenster liegt außerhalb des sichtbaren Bildschirmbereichs.");

            using (var image = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
                if (!HasImageContent(image)) throw new InvalidOperationException("Die Fensteraufnahme ist leer.");
                return ExtractView(image, area, window);
            }
        }

        private static byte[] ExtractView(Bitmap image, Rectangle windowArea, IntPtr window)
        {
            Rectangle best = Rectangle.Empty;
            long bestArea = 0;
            EnumChildWindows(window, delegate(IntPtr child, IntPtr unused)
            {
                if (!IsWindowVisible(child)) return true;
                var className = new System.Text.StringBuilder(256);
                GetClassName(child, className, className.Capacity);
                string name = className.ToString().ToLowerInvariant();
                if (!(name.Contains("render") || name.Contains("sceneview") || name.Contains("viewport") || name.Contains("opengl"))) return true;
                Rect childRect;
                if (!GetWindowRect(child, out childRect)) return true;
                var candidate = Rectangle.FromLTRB(childRect.Left, childRect.Top, childRect.Right, childRect.Bottom);
                candidate.Intersect(windowArea);
                long pixels = (long)candidate.Width * candidate.Height;
                if (candidate.Width >= windowArea.Width / 5 && candidate.Height >= windowArea.Height / 4 && pixels > bestArea && pixels < (long)windowArea.Width * windowArea.Height * 95 / 100)
                {
                    best = candidate;
                    bestArea = pixels;
                }
                return true;
            }, IntPtr.Zero);

            Rectangle crop;
            if (bestArea > 0)
                crop = new Rectangle(best.X - windowArea.X, best.Y - windowArea.Y, best.Width, best.Height);
            else
            {
                using (var selector = new ViewSelectionForm(image))
                {
                    if (selector.ShowDialog() != DialogResult.OK)
                        throw new OperationCanceledException("Modellbereich nicht ausgewählt.");
                    crop = selector.Selection;
                }
            }
            using (var result = image.Clone(crop, PixelFormat.Format24bppRgb)) return EncodeSnapshot(result);
        }

        private static bool HasImageContent(Bitmap image)
        {
            // Look inside the viewport-sized center, where a failed render is typically a solid fill.
            var colors = new HashSet<int>();
            for (int y = 2; y <= 8; y++)
                for (int x = 2; x <= 8; x++)
                    colors.Add(image.GetPixel(image.Width * x / 10, image.Height * y / 10).ToArgb());
            return colors.Count > 2;
        }

        public static byte[] EncodeSnapshot(Image image)
        {
            double scale = Math.Min(1.0, 1500.0 / Math.Max(image.Width, image.Height));
            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
            int height = Math.Max(1, (int)Math.Round(image.Height * scale));
            using (var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(resized))
            using (var stream = new MemoryStream())
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, new Rectangle(0, 0, width, height));
                resized.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }
    }

    internal sealed class ViewSelectionForm : Form
    {
        private readonly Image source;
        private readonly PictureBox picture = new PictureBox();
        private Point start;
        private Rectangle selection;
        private Rectangle display;
        public Rectangle Selection { get; private set; }

        public ViewSelectionForm(Image image)
        {
            source = image;
            Text = "Modellbereich auswählen";
            Font = new Font("Segoe UI", 10);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1100, 760);
            MinimumSize = new Size(600, 450);
            BackColor = Color.FromArgb(245, 247, 250);
            var heading = new Label { Dock = DockStyle.Top, Height = 52, Padding = new Padding(20, 14, 0, 0), Text = "Ziehe ein Rechteck um die Modellansicht und bestätige mit Übernehmen." };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(20, 8, 0, 0) };
            var accept = new Button();
            var cancel = new Button();
            MainForm.StyleButton(accept, "Übernehmen", 145, true);
            MainForm.StyleButton(cancel, "Abbrechen", 120, false);
            accept.Click += delegate
            {
                Rectangle chosen = Rectangle.Intersect(selection, display);
                if (chosen.Width < 20 || chosen.Height < 20) return;
                int x = (int)((chosen.X - display.X) * (double)source.Width / display.Width);
                int y = (int)((chosen.Y - display.Y) * (double)source.Height / display.Height);
                int right = (int)((chosen.Right - display.X) * (double)source.Width / display.Width);
                int bottom = (int)((chosen.Bottom - display.Y) * (double)source.Height / display.Height);
                Selection = Rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height), Rectangle.FromLTRB(x, y, right, bottom));
                DialogResult = DialogResult.OK;
            };
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.AddRange(new Control[] { accept, cancel });
            picture.Dock = DockStyle.Fill;
            picture.BackColor = Color.FromArgb(30, 40, 54);
            picture.Paint += delegate(object sender, PaintEventArgs e)
            {
                float scale = Math.Min((float)picture.Width / source.Width, (float)picture.Height / source.Height);
                int width = (int)(source.Width * scale), height = (int)(source.Height * scale);
                display = new Rectangle((picture.Width - width) / 2, (picture.Height - height) / 2, width, height);
                e.Graphics.DrawImage(source, display);
                if (!selection.IsEmpty) using (var pen = new Pen(Color.DeepSkyBlue, 3)) e.Graphics.DrawRectangle(pen, selection);
            };
            picture.MouseDown += delegate(object sender, MouseEventArgs e) { start = e.Location; selection = Rectangle.Empty; };
            picture.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                selection = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
                picture.Invalidate();
            };
            Controls.Add(picture);
            Controls.Add(buttons);
            Controls.Add(heading);
            CancelButton = cancel;
        }
    }

    internal sealed partial class MainForm : Form
    {
        private readonly IssueStore store;
        private readonly List<string> stakeholders;
        private readonly string currentUser;
        private List<Issue> issues;
        private readonly ListView list = new ListView();
        private readonly Button pdf = new Button();
        private readonly Button bcf = new Button();
        private readonly Button edit = new Button();
        private readonly Button deleteIssue = new Button();
        private readonly Button goToView = new Button();
        private System.Windows.Forms.Timer viewDialogTimer;
        private readonly Label count = new Label();

        public MainForm(IssueStore projectStore, string projectName, List<string> projectStakeholders = null, string selectedUser = null)
        {
            store = projectStore;
            stakeholders = projectStakeholders ?? new List<string>();
            currentUser = selectedUser ?? "";
            issues = store.Load();
            BuildCompactLayout(projectName);
            RefreshList(Guid.Empty);
        }

        internal static void StyleButton(Button button, string text, int width, bool primary)
        {
            button.Text = text;
            button.Width = width;
            button.Height = 30;
            button.Margin = new Padding(0, 0, 7, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(210, 219, 229);
            button.BackColor = primary ? Color.FromArgb(38, 94, 179) : Color.White;
            button.ForeColor = primary ? Color.White : Color.FromArgb(30, 43, 58);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
        }

        private Issue SelectedIssue()
        {
            return list.SelectedItems.Count == 0 ? null : (Issue)list.SelectedItems[0].Tag;
        }

        private void RefreshList(Guid select)
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                ClearThumbnails();
                foreach (var issue in issues)
                {
                    AddThumbnail(issue);
                    var row = new ListViewItem(issue.Title) { Tag = issue };
                    list.Items.Add(row);
                    if (issue.Id == select) row.Selected = true;
                }
            }
            finally { list.EndUpdate(); }
            ResizeIssueColumn();
            count.Text = issues.Count == 1 ? "1 Issue" : issues.Count + " Issues";
            pdf.Enabled = bcf.Enabled = issues.Count > 0;
            ShowSelected();
        }

        private void ShowSelected()
        {
            Issue issue = SelectedIssue();
            edit.Enabled = issue != null;
            deleteIssue.Enabled = issue != null;
            goToView.Enabled = issue != null && issue.Camera != null;
            list.Invalidate();
        }

        internal static Bitmap ReadSnapshot(byte[] snapshot)
        {
            if (snapshot == null || snapshot.Length == 0) return null;
            using (var stream = new MemoryStream(snapshot))
            using (var image = Image.FromStream(stream))
                return new Bitmap(image);
        }

        private void AddIssue()
        {
            byte[] snapshot = null;
            CameraView capturedCamera = null;
            string captureHint = "";
            try
            {
                IntPtr window = NativeCapture.FindFreedom();
                if (window == IntPtr.Zero)
                    captureHint = "Öffne ein sichtbares Freedom-Fenster für die automatische Aufnahme.";
                else
                {
                    var shell = FindForm() == this && Parent != null ? Parent.FindForm() : this;
                    shell.Hide();
                    try
                    {
                        snapshot = NativeCapture.CaptureWindow(window);
                        try { capturedCamera = FreedomCamera.Capture(window); captureHint = "Screenshot und Kamera automatisch erfasst."; }
                        catch (Exception error) { captureHint = "Screenshot erfasst. Kamera nicht erfasst: " + error.Message; }
                    }
                    finally { shell.Show(); shell.Activate(); }
                }
            }
            catch (Exception error) { captureHint = error.Message; }

            var draft = new Issue
            {
                Id = Guid.NewGuid(), SnapshotId = Guid.NewGuid(), Status = "Offen",
                Author = currentUser, AssignedTo = "BIM Koordinator", CreatedUtc = DateTime.UtcNow, Snapshot = snapshot, Camera = capturedCamera
            };
            OpenEditor(draft, captureHint);
        }

        private void EditIssue()
        {
            Issue issue = SelectedIssue();
            if (issue == null) return;
            OpenEditor(issue, "");
        }

        private void DeleteIssue()
        {
            var issue = SelectedIssue();
            if (issue == null) return;
            if (MessageBox.Show(this, "Issue „" + issue.Title + "“ löschen?", "Issue löschen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var updated = issues.Where(item => item.Id != issue.Id).ToList();
            try { store.Save(updated); issues = updated; RefreshList(Guid.Empty); }
            catch (Exception error) { ShowError("Löschen fehlgeschlagen", error); }
        }

        private void GoToView()
        {
            if (viewDialogTimer != null) return;
            var issue = SelectedIssue();
            if (issue == null || issue.Camera == null) return;
            try
            {
                IntPtr window = NativeCapture.FindFreedom();
                if (window == IntPtr.Zero) throw new InvalidOperationException("Öffne das passende Modell in Freedom und wähle eine Perspektivansicht.");
                var shell = Parent != null ? Parent.FindForm() : this;
                shell.Hide();
                try
                {
                    double rollDifference;
                    IntPtr dialog = FreedomCamera.PrepareNavigation(window, issue.Camera, out rollDifference);
                    viewDialogTimer = new System.Windows.Forms.Timer { Interval = 150 };
                    viewDialogTimer.Tick += delegate
                    {
                        if (FreedomCamera.IsViewDialogOpen(dialog)) return;
                        viewDialogTimer.Dispose();
                        viewDialogTimer = null;
                        shell.Show();
                        if (rollDifference > 0.1)
                            tips.Show("Die Kameradrehung im Freedom-Dialog wich um " + rollDifference.ToString("0.0", CultureInfo.CurrentCulture) + "° ab.", goToView, 0, goToView.Height, 5500);
                    };
                    viewDialogTimer.Start();
                }
                catch
                {
                    if (viewDialogTimer != null) viewDialogTimer.Dispose();
                    viewDialogTimer = null;
                    shell.Show();
                    throw;
                }
            }
            catch (Exception error) { ShowError("Ansicht konnte nicht geöffnet werden", error); }
        }

        private void OpenEditor(Issue original, string captureHint)
        {
            using (var editor = new IssueForm(original, captureHint, stakeholders, currentUser))
            {
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                var updated = new List<Issue>(issues);
                int index = updated.FindIndex(issue => issue.Id == original.Id);
                if (index < 0) updated.Add(editor.Result);
                else updated[index] = editor.Result;

                try
                {
                    store.Save(updated);
                    issues = updated;
                    RefreshList(original.Id);
                }
                catch (Exception error) { ShowError("Speichern fehlgeschlagen", error); }
            }
        }

        private void Export(bool asBcf)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = asBcf ? "Issue-Liste als BCF exportieren" : "Issue-Liste als PDF exportieren";
                dialog.Filter = asBcf ? "BCF 2.1 (*.bcfzip)|*.bcfzip" : "PDF (*.pdf)|*.pdf";
                dialog.FileName = asBcf ? "Freedom-Issues.bcfzip" : "BIM-Issue.pdf";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    AtomicFile.Write(dialog.FileName, stream =>
                    {
                        if (asBcf) BcfExport.Write(stream, issues);
                        else PdfExport.Write(stream, issues);
                    });
                    if (asBcf) MessageBox.Show(this, "Die Issue-Liste wurde exportiert.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception error) { ShowError("Export fehlgeschlagen", error); }
            }
        }

        private void ImportBcf()
        {
            using (var dialog = new OpenFileDialog { Title = "BCF-Datei importieren", Filter = "BCF-Dateien (*.bcf;*.bcfzip)|*.bcf;*.bcfzip|Alle Dateien|*.*" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var imported = BcfImport.Read(dialog.FileName);
                    var updated = new List<Issue>(issues);
                    foreach (Issue item in imported)
                    {
                        var existing = updated.FirstOrDefault(entry => entry.Id == item.Id);
                        if (existing != null)
                        {
                            // Work on a copy so cancelling/failed saves do not mutate the live list.
                            var merged = new List<IssueComment>(existing.Comments ?? new List<IssueComment>());
                            foreach (var comment in item.Comments ?? new List<IssueComment>())
                            {
                                int found = merged.FindIndex(c => c.Id == comment.Id);
                                if (found < 0) { merged.Add(comment); }
                                else if (comment.ModifiedUtc > merged[found].ModifiedUtc) merged[found] = comment;
                            }
                            updated[updated.IndexOf(existing)] = new Issue { Id = existing.Id, SnapshotId = existing.SnapshotId, Title = existing.Title, Description = existing.Description, Status = existing.Status, Author = existing.Author, AssignedTo = existing.AssignedTo, CreatedUtc = existing.CreatedUtc, Snapshot = existing.Snapshot, Camera = existing.Camera, Comments = merged };
                            continue;
                        }
                        updated.Add(item);
                    }
                    store.Save(updated);
                    issues = updated;
                    RefreshList(Guid.Empty);
                }
                catch (Exception error) { ShowError("Import fehlgeschlagen", error); }
            }
        }

        private void ShowError(string title, Exception error)
        {
            MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal sealed partial class IssueForm : Form
    {
        private readonly Issue original;
        private readonly TextBox title = new TextBox();
        private readonly TextBox description = new TextBox();
        private readonly ComboBox status = new ComboBox();
        private readonly ComboBox creator = new ComboBox();
        private readonly ComboBox assignee = new ComboBox();
        private readonly List<string> stakeholders;
        private readonly string currentUser;
        private readonly PictureBox preview = new PictureBox();
        private byte[] snapshot;
        private CameraView camera;

        public Issue Result { get; private set; }

        public IssueForm(Issue original, string captureHint, List<string> projectStakeholders = null, string selectedUser = null)
        {
            this.original = original;
            stakeholders = projectStakeholders ?? new List<string>();
            currentUser = selectedUser ?? "";
            camera = original.Camera;
            BuildCompactEditor(captureHint);
        }

        private void RefreshScreenshot()
        {
            try
            {
                IntPtr window = NativeCapture.FindFreedom();
                if (window == IntPtr.Zero) throw new InvalidOperationException("Öffne ein sichtbares Freedom-Fenster für die Aufnahme.");
                var parent = Owner;
                Hide();
                if (parent != null) parent.Hide();
                byte[] captured;
                CameraView updatedCamera = null;
                string cameraResult;
                try
                {
                    captured = NativeCapture.CaptureWindow(window);
                    try { updatedCamera = FreedomCamera.Capture(window); cameraResult = "Screenshot und Kamera automatisch erfasst."; }
                    catch (Exception error) { cameraResult = "Screenshot erfasst. Kamera nicht erfasst: " + error.Message; }
                }
                finally { if (parent != null) parent.Show(); Show(); Activate(); }
                SetSnapshot(captured, "");
                camera = updatedCamera;
                if (updatedCamera == null) MessageBox.Show(this, cameraResult, "Kamera", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Aufnahme fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void UploadImage()
        {
            using (var dialog = new OpenFileDialog { Title = "Bild auswählen", Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Alle Dateien|*.*" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var image = Image.FromFile(dialog.FileName)) SetSnapshot(NativeCapture.EncodeSnapshot(image), "");
                    camera = null;
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Bild konnte nicht geöffnet werden", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private void SetSnapshot(byte[] image, string message)
        {
            if (preview.Image != null) preview.Image.Dispose();
            preview.Image = MainForm.ReadSnapshot(image);
            snapshot = image;
        }

        private void PasteImage()
        {
            try
            {
                using (var image = Clipboard.GetImage())
                {
                    if (image == null) { MessageBox.Show(this, "Die Zwischenablage enthält kein Bild.", "Bild einfügen", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    SetSnapshot(NativeCapture.EncodeSnapshot(image), "");
                    camera = null;
                }
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Bild einfügen", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void SaveIssue()
        {
            if (String.IsNullOrWhiteSpace(title.Text))
            {
                MessageBox.Show(this, "Gib einen Titel ein.", "Titel fehlt", MessageBoxButtons.OK, MessageBoxIcon.Information);
                title.Focus();
                return;
            }

            if (!AddPendingComment()) return;
            Result = new Issue
            {
                Id = original.Id, SnapshotId = Object.ReferenceEquals(snapshot, original.Snapshot) ? original.SnapshotId : Guid.NewGuid(),
                Author = creator.SelectedIndex <= 0 ? "" : (string)creator.SelectedItem, AssignedTo = assignee.SelectedIndex <= 0 ? "" : (string)assignee.SelectedItem, CreatedUtc = original.CreatedUtc, Snapshot = snapshot, Camera = camera,
                Title = title.Text.Trim(), Description = description.Text.Trim(), Status = (string)status.SelectedItem,
                Comments = new List<IssueComment>(comments)
            };
            DialogResult = DialogResult.OK;
        }

        private void EditCamera()
        {
            using (var dialog = new CameraForm(camera))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                camera = dialog.Result;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) editorTips.Dispose();
            if (disposing && preview.Image != null) preview.Image.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class CameraForm : Form
    {
        private readonly TextBox[] fields = new TextBox[7];
        public CameraView Result { get; private set; }

        public CameraForm(CameraView existing)
        {
            Text = "Kamera für BCF";
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(510, 445);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            var info = new Label { Left = 20, Top = 14, Width = 465, Height = 75, Text = "In Navisworks: Ansichtspunkt → Aktuellen Ansichtspunkt bearbeiten. Werte in Metern und Grad eintragen. Nur Perspektive mit aufrechter Kamera (Roll 0°)." };
            Controls.Add(info);
            string[] labels = { "Position X (m)", "Position Y (m)", "Position Z (m)", "Look At X (m)", "Look At Y (m)", "Look At Z (m)", "Horizontaler Sichtwinkel (°)" };
            double[] values = existing == null ? null : new[] { existing.X, existing.Y, existing.Z, existing.TargetX, existing.TargetY, existing.TargetZ, existing.HorizontalFov };
            for (int i = 0; i < fields.Length; i++)
            {
                Controls.Add(new Label { Text = labels[i], Left = 20, Top = 90 + i * 37, Width = 230 });
                fields[i] = new TextBox { Left = 260, Top = 87 + i * 37, Width = 220, Text = values == null ? "" : values[i].ToString("G17", CultureInfo.CurrentCulture) };
                Controls.Add(fields[i]);
            }
            var save = new Button { Left = 20, Top = 365, Width = 140, Height = 36, Text = "Übernehmen" };
            var cancel = new Button { Left = 170, Top = 365, Width = 140, Height = 36, Text = "Abbrechen", DialogResult = DialogResult.Cancel };
            save.Click += delegate { SaveCamera(); };
            Controls.Add(save);
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private void SaveCamera()
        {
            var values = new double[7];
            for (int i = 0; i < fields.Length; i++)
            {
                string text = fields[i].Text.Trim();
                if ((!Double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out values[i]) && !Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) || Double.IsNaN(values[i]) || Double.IsInfinity(values[i]))
                { MessageBox.Show(this, "Bitte alle Werte als Zahlen eingeben."); return; }
            }
            double dx = values[3] - values[0], dy = values[4] - values[1], dz = values[5] - values[2];
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) < 0.000001 || values[6] < 45 || values[6] > 60)
            { MessageBox.Show(this, "Position und Look At müssen verschieden sein. BCF 2.1 erlaubt einen horizontalen Sichtwinkel von 45° bis 60°."); return; }
            Result = new CameraView { X = values[0], Y = values[1], Z = values[2], TargetX = values[3], TargetY = values[4], TargetZ = values[5], HorizontalFov = values[6] };
            DialogResult = DialogResult.OK;
        }
    }
}

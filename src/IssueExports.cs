using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FreedomIssues
{
    internal static class BcfImport
    {
        public static List<Issue> Read(string path)
        {
            var issues = new List<Issue>();
            using (var archive = ZipFile.OpenRead(path))
            {
                var versionEntry = archive.GetEntry("bcf.version");
                if (versionEntry == null) throw new InvalidDataException("Die Datei enthält keine BCF-Versionsinformation.");
                var version = LoadXml(versionEntry);
                string number = (string)version.Root.Attribute("VersionId");
                if (number != "2.1" && number != "3.0") throw new InvalidDataException("Unterstützt werden BCF 2.1 und 3.0. Gefunden: " + number);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith("/markup.bcf", StringComparison.OrdinalIgnoreCase)) continue;
                    var markup = LoadXml(entry);
                    var topic = markup.Root.Elements().FirstOrDefault(element => element.Name.LocalName == "Topic");
                    if (topic == null) throw new InvalidDataException("Ein Issue enthält kein Topic.");
                    Guid id;
                    if (!Guid.TryParse((string)topic.Attribute("Guid"), out id)) throw new InvalidDataException("Ein Issue hat keine gültige GUID.");
                    string title = Value(topic, "Title");
                    if (String.IsNullOrWhiteSpace(title)) throw new InvalidDataException("Ein Issue hat keinen Titel.");
                    DateTime created;
                    if (!DateTime.TryParse(Value(topic, "CreationDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out created))
                        throw new InvalidDataException("Ein Issue hat kein gültiges Erstellungsdatum.");
                    var viewpoint = markup.Descendants().FirstOrDefault(element => (element.Name.LocalName == "Viewpoints" || element.Name.LocalName == "ViewPoint") && element.Elements().Any(child => child.Name.LocalName == "Viewpoint" || child.Name.LocalName == "Snapshot"));
                    byte[] snapshot = null;
                    CameraView camera = null;
                    Guid snapshotId = Guid.NewGuid();
                    if (viewpoint != null)
                    {
                        Guid parsed;
                        if (Guid.TryParse((string)viewpoint.Attribute("Guid"), out parsed)) snapshotId = parsed;
                        string folder = entry.FullName.Substring(0, entry.FullName.Length - "markup.bcf".Length);
                        string filename = Value(viewpoint, "Snapshot").Replace('\\', '/');
                        if (filename.Length > 0)
                        {
                            var imageEntry = FindEntry(archive, folder, filename);
                            if (imageEntry.Length > 50 * 1024 * 1024) throw new InvalidDataException("Ein BCF-Bild ist größer als 50 MB.");
                            using (var input = imageEntry.Open())
                            using (var image = Image.FromStream(input)) snapshot = NativeCapture.EncodeSnapshot(image);
                        }
                        string viewFile = Value(viewpoint, "Viewpoint").Replace('\\', '/');
                        if (viewFile.Length > 0)
                            camera = ReadCamera(LoadXml(FindEntry(archive, folder, viewFile)), number);
                    }
                    string status = (string)topic.Attribute("TopicStatus") ?? "Open";
                    if (String.Equals(status, "Open", StringComparison.OrdinalIgnoreCase)) status = "Offen";
                    else if (String.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase)) status = "Erledigt";
                    else if (String.Equals(status, "In Progress", StringComparison.OrdinalIgnoreCase)) status = "In Bearbeitung";
                    var comments = new List<IssueComment>();
                    foreach (var element in markup.Root.Elements().Where(e => e.Name.LocalName == "Comment")
                        .Concat(markup.Root.Descendants().Where(e => e.Name.LocalName == "Comments").SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "Comment"))))
                    {
                        Guid commentId, viewId;
                        DateTime date, modified;
                        if (!Guid.TryParse((string)element.Attribute("Guid"), out commentId)) commentId = Guid.NewGuid();
                        if (!DateTime.TryParse(Value(element, "Date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date)) date = created;
                        DateTime.TryParse(Value(element, "ModifiedDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out modified);
                        var view = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Viewpoint");
                        Guid.TryParse(view == null ? null : (string)view.Attribute("Guid"), out viewId);
                        comments.Add(new IssueComment { Id = commentId, CreatedUtc = date, Author = Value(element, "Author"), Text = Value(element, "Comment"), ViewpointId = viewId, ModifiedUtc = modified, ModifiedAuthor = Value(element, "ModifiedAuthor") });
                    }
                    issues.Add(new Issue { Id = id, SnapshotId = snapshotId, Title = title, Description = Value(topic, "Description"), Status = status, CreatedUtc = created.ToUniversalTime(), Author = Value(topic, "CreationAuthor"), AssignedTo = Value(topic, "AssignedTo"), Snapshot = snapshot, Camera = camera, Comments = comments });
                }
            }
            if (issues.Count == 0) throw new InvalidDataException("Die BCF-Datei enthält keine Issues.");
            return issues;
        }

        private static ZipArchiveEntry FindEntry(ZipArchive archive, string folder, string filename)
        {
            if (filename.StartsWith("/", StringComparison.Ordinal) || filename.Split('/').Any(part => part == "..")) throw new InvalidDataException("Ungültiger Dateipfad im BCF.");
            var entry = archive.GetEntry(folder + filename);
            if (entry == null) throw new InvalidDataException("Eine BCF-Datei fehlt: " + filename);
            return entry;
        }

        private static CameraView ReadCamera(XDocument document, string version)
        {
            var camera = document.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "PerspectiveCamera" || e.Name.LocalName == "OrthogonalCamera");
            if (camera == null) return null;
            var position = camera.Elements().First(e => e.Name.LocalName == "CameraViewPoint");
            var direction = camera.Elements().First(e => e.Name.LocalName == "CameraDirection");
            var up = camera.Elements().First(e => e.Name.LocalName == "CameraUpVector");
            double x = Number(position, "X"), y = Number(position, "Y"), z = Number(position, "Z");
            bool orthographic = camera.Name.LocalName == "OrthogonalCamera";
            double fov = orthographic ? 0 : Number(camera, "FieldOfView");
            double scale = orthographic ? Number(camera, "ViewToWorldScale") : 0;
            if (version == "3.0" && !orthographic)
            {
                string aspectText = Value(camera, "AspectRatio");
                double aspect = String.IsNullOrWhiteSpace(aspectText) ? 1 : XmlConvert.ToDouble(aspectText);
                if (aspect <= 0) throw new InvalidDataException("Ungültiges Seitenverhältnis im BCF.");
                fov = 2 * Math.Atan(Math.Tan(fov * Math.PI / 360) * aspect) * 180 / Math.PI;
            }
            return new CameraView { X = x, Y = y, Z = z, TargetX = x + Number(direction, "X"), TargetY = y + Number(direction, "Y"), TargetZ = z + Number(direction, "Z"), HasUpVector = true, UpX = Number(up, "X"), UpY = Number(up, "Y"), UpZ = Number(up, "Z"), HorizontalFov = fov, IsOrthographic = orthographic, ViewToWorldScale = scale };
        }

        private static double Number(XElement parent, string name)
        {
            double value = XmlConvert.ToDouble(Value(parent, name));
            if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidDataException("Ungültige Kameradaten im BCF.");
            return value;
        }

        private static string Value(XElement parent, string name)
        {
            var element = parent.Elements().FirstOrDefault(child => child.Name.LocalName == name);
            return element == null ? "" : element.Value;
        }

        private static XDocument LoadXml(ZipArchiveEntry entry)
        {
            if (entry.Length > 10 * 1024 * 1024) throw new InvalidDataException("Eine BCF-XML-Datei ist zu groß.");
            using (var input = entry.Open())
            using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10 * 1024 * 1024 }))
                return XDocument.Load(reader);
        }
    }

    internal static class BcfExport
    {
        public static void Write(Stream output, IList<Issue> issues)
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                AddXml(archive, "bcf.version", new XElement("Version", new XAttribute("VersionId", "2.1")));
                foreach (var issue in issues)
                {
                    string folder = issue.Id.ToString("D") + "/";
                    archive.CreateEntry(folder);
                    var topic = new XElement("Topic",
                        new XAttribute("Guid", issue.Id.ToString("D")),
                        new XAttribute("TopicStatus", BcfStatus(issue.Status)),
                        new XElement("Title", issue.Title),
                        new XElement("CreationDate", XmlConvert.ToString(issue.CreatedUtc, XmlDateTimeSerializationMode.Utc)),
                        new XElement("CreationAuthor", String.IsNullOrWhiteSpace(issue.Author) ? "BCF Manager" : issue.Author),
                        String.IsNullOrWhiteSpace(issue.AssignedTo) ? null : new XElement("AssignedTo", issue.AssignedTo),
                        new XElement("Description", issue.Description));
                    var markup = new XElement("Markup", topic);

                    bool hasSnapshot = issue.Snapshot != null && issue.Snapshot.Length > 0;
                    var comments = issue.Comments ?? new List<IssueComment>();
                    foreach (var comment in comments)
                    {
                        var node = new XElement("Comment", new XAttribute("Guid", comment.Id.ToString("D")),
                            new XElement("Date", XmlConvert.ToString(comment.CreatedUtc, XmlDateTimeSerializationMode.Utc)),
                            new XElement("Author", String.IsNullOrWhiteSpace(comment.Author) ? "BCF Manager" : comment.Author),
                            new XElement("Comment", String.IsNullOrWhiteSpace(comment.Text) ? "Ansicht" : comment.Text));
                        if ((hasSnapshot || issue.Camera != null) && comment.ViewpointId == issue.SnapshotId)
                            node.Add(new XElement("Viewpoint", new XAttribute("Guid", issue.SnapshotId.ToString("D"))));
                        if (comment.ModifiedUtc != DateTime.MinValue)
                            node.Add(new XElement("ModifiedDate", XmlConvert.ToString(comment.ModifiedUtc, XmlDateTimeSerializationMode.Utc)));
                        if (!String.IsNullOrWhiteSpace(comment.ModifiedAuthor)) node.Add(new XElement("ModifiedAuthor", comment.ModifiedAuthor));
                        markup.Add(node);
                    }
                    if (hasSnapshot || issue.Camera != null)
                    {
                        if (!comments.Any(c => c.ViewpointId == issue.SnapshotId))
                        markup.Add(new XElement("Comment", new XAttribute("Guid", Guid.NewGuid().ToString("D")),
                            new XElement("Date", XmlConvert.ToString(issue.CreatedUtc, XmlDateTimeSerializationMode.Utc)),
                            new XElement("Author", String.IsNullOrWhiteSpace(issue.Author) ? "BCF Manager" : issue.Author),
                            new XElement("Comment", String.IsNullOrWhiteSpace(issue.Description) ? issue.Title : issue.Description),
                            new XElement("Viewpoint", new XAttribute("Guid", issue.SnapshotId.ToString("D")))));
                        var viewpoint = new XElement("Viewpoints", new XAttribute("Guid", issue.SnapshotId.ToString("D")));
                        if (issue.Camera != null)
                        {
                            viewpoint.Add(new XElement("Viewpoint", "viewpoint.bcfv"));
                            AddXml(archive, folder + "viewpoint.bcfv", CameraXml(issue.Camera, issue.SnapshotId));
                        }
                        if (hasSnapshot) viewpoint.Add(new XElement("Snapshot", "snapshot.png"));
                        markup.Add(viewpoint);
                    }
                    if (hasSnapshot)
                    {
                        using (var stream = archive.CreateEntry(folder + "snapshot.png").Open())
                            stream.Write(issue.Snapshot, 0, issue.Snapshot.Length);
                    }

                    AddXml(archive, folder + "markup.bcf", markup);
                }
            }
        }

        private static XElement CameraXml(CameraView camera, Guid id)
        {
            double x = camera.TargetX - camera.X, y = camera.TargetY - camera.Y, z = camera.TargetZ - camera.Z;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length < 0.000001 || Double.IsNaN(length) || Double.IsInfinity(length)) throw new InvalidDataException("Ungültige Kamerarichtung.");
            x /= length; y /= length; z /= length;
            double horizontal = Math.Sqrt(x * x + y * y);
            double ux, uy, uz;
            if (horizontal < 0.000001) { ux = 0; uy = 1; uz = 0; }
            else { ux = -x * z / horizontal; uy = -y * z / horizontal; uz = horizontal; }
            if (camera.HasUpVector) { ux = camera.UpX; uy = camera.UpY; uz = camera.UpZ; }
            return new XElement("VisualizationInfo", new XAttribute("Guid", id.ToString("D")),
                new XElement(camera.IsOrthographic ? "OrthogonalCamera" : "PerspectiveCamera",
                    Point("CameraViewPoint", camera.X, camera.Y, camera.Z),
                    Point("CameraDirection", x, y, z),
                    Point("CameraUpVector", ux, uy, uz),
                    new XElement(camera.IsOrthographic ? "ViewToWorldScale" : "FieldOfView", XmlConvert.ToString(camera.IsOrthographic ? camera.ViewToWorldScale : camera.HorizontalFov))));
        }

        private static XElement Point(string name, double x, double y, double z)
        {
            return new XElement(name, new XElement("X", XmlConvert.ToString(x)), new XElement("Y", XmlConvert.ToString(y)), new XElement("Z", XmlConvert.ToString(z)));
        }

        private static string BcfStatus(string status)
        {
            if (status == "Erledigt") return "Closed";
            if (status == "In Bearbeitung") return "In Progress";
            if (status == "Offen") return "Open";
            return status;
        }

        private static void AddXml(ZipArchive archive, string name, XElement content)
        {
            using (var stream = archive.CreateEntry(name).Open())
                new XDocument(content).Save(stream);
        }
    }

    internal static class PdfExport
    {
        private const int Width = 1240;
        private const int Height = 1754;
        private const int Margin = 104;
        private const int BodyWidth = Width - 2 * Margin;
        private const int BodyBottom = 1576;
        private static readonly Color Ink = Color.FromArgb(24, 42, 57);
        private static readonly Color Secondary = Color.FromArgb(80, 101, 117);

        public static void Write(Stream output, IList<Issue> issues)
        {
            var pages = new List<byte[]>();
            using (var headline = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var label = new Font("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var body = new Font("Segoe UI", 25, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var small = new Font("Segoe UI", 21, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var caption = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var ink = new SolidBrush(Ink))
            using (var secondary = new SolidBrush(Secondary))
            using (var accent = new SolidBrush(Color.FromArgb(36, 100, 167)))
            using (var pale = new SolidBrush(Color.FromArgb(244, 247, 251)))
            using (var rule = new Pen(Color.FromArgb(211, 222, 231), 2))
            using (var format = new StringFormat(StringFormat.GenericDefault) { FormatFlags = StringFormatFlags.LineLimit })
            {
                for (int index = 0; index < issues.Count; index++)
                {
                    var issue = issues[index];
                    string remaining = issue.Description ?? "";
                    bool firstPage = true;
                    do
                    {
                        using (var page = new Bitmap(Width * 2, Height * 2, PixelFormat.Format24bppRgb))
                        using (var graphics = Graphics.FromImage(page))
                        {
                            graphics.Clear(Color.White);
                            graphics.ScaleTransform(2f, 2f);
                            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.FillRectangle(accent, 0, 0, Width, 12);
                            graphics.DrawString("BIM Issue", label, ink, Margin, 61);
                            using (var right = new StringFormat { Alignment = StringAlignment.Far })
                                graphics.DrawString(String.Format("ISSUE {0:D2} / {1:D2}", index + 1, issues.Count), caption, secondary, new RectangleF(Margin, 67, BodyWidth, 32), right);
                            graphics.DrawLine(rule, Margin, 126, Width - Margin, 126);

                            float y = 160;
                            string title = firstPage ? issue.Title : issue.Title + " (Fortsetzung)";
                            SizeF titleSize = graphics.MeasureString(title, headline, BodyWidth - 56);
                            float titleHeight = Math.Max(76, titleSize.Height + 36);
                            graphics.FillRectangle(pale, Margin, y, BodyWidth, titleHeight);
                            graphics.FillRectangle(accent, Margin, y, 6, titleHeight);
                            graphics.DrawString(title, headline, ink, new RectangleF(Margin + 24, y + 18, BodyWidth - 48, titleSize.Height + 10));
                            y += titleHeight + 28;

                            if (firstPage)
                            {
                                float column = BodyWidth / 4f;
                                DrawMeta(graphics, "STATUS", issue.Status, Margin, y, column - 12, caption, small, secondary, ink);
                                DrawMeta(graphics, "ERSTELLT AM", issue.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), Margin + column, y, column - 12, caption, small, secondary, ink);
                                DrawMeta(graphics, "ERSTELLT VON", issue.Author, Margin + column * 2, y, column - 12, caption, small, secondary, ink);
                                DrawMeta(graphics, "ZUGEWIESEN", issue.AssignedTo, Margin + column * 3, y, column - 12, caption, small, secondary, ink);
                                y += 70;
                                graphics.DrawLine(rule, Margin, y, Width - Margin, y);
                                y += 28;
                                using (Bitmap snapshot = MainForm.ReadSnapshot(issue.Snapshot))
                                {
                                    if (snapshot != null)
                                    {
                                        graphics.DrawString("ANSICHT", caption, secondary, Margin, y);
                                        y += 34;
                                        double maxImageHeight = Math.Max(60, Math.Min(480, BodyBottom - y - 190));
                                        double scale = Math.Min((double)BodyWidth / snapshot.Width, maxImageHeight / snapshot.Height);
                                        int width = Math.Max(1, (int)Math.Round(snapshot.Width * scale));
                                        int height = Math.Max(1, (int)Math.Round(snapshot.Height * scale));
                                        graphics.DrawImage(snapshot, new Rectangle(Margin + (BodyWidth - width) / 2, (int)y, width, height));
                                        y += height + 34;
                                    }
                                }
                            }

                            if (remaining.Length > 0)
                            {
                                graphics.DrawLine(rule, Margin, y, Width - Margin, y);
                                y += 26;
                                graphics.DrawString("BESCHREIBUNG", caption, secondary, Margin, y);
                                y += 38;
                                var box = new RectangleF(Margin, y, BodyWidth, BodyBottom - y);
                                int fitted, lines;
                                graphics.MeasureString(remaining, body, box.Size, format, out fitted, out lines);
                                if (fitted <= 0) throw new InvalidOperationException("Der PDF-Text passt nicht in den vorgesehenen Bereich.");
                                if (fitted < remaining.Length && !Char.IsWhiteSpace(remaining[fitted]) && !Char.IsWhiteSpace(remaining[fitted - 1]))
                                {
                                    int wordBreak = fitted;
                                    while (wordBreak > 0 && !Char.IsWhiteSpace(remaining[wordBreak - 1])) wordBreak--;
                                    if (wordBreak > 0) fitted = wordBreak;
                                }
                                graphics.DrawString(remaining.Substring(0, fitted), body, ink, box, format);
                                remaining = remaining.Substring(fitted).TrimStart('\r', '\n');
                            }

                            graphics.DrawLine(rule, Margin, 1620, Width - Margin, 1620);
                            graphics.DrawString("BIM Issue  ·  Issue " + (index + 1), small, secondary, Margin, 1642);
                            using (var right = new StringFormat { Alignment = StringAlignment.Far })
                                graphics.DrawString("Seite " + (pages.Count + 1), small, secondary, new RectangleF(Margin, 1642, BodyWidth, 36), right);
                            using (var jpeg = new MemoryStream())
                            {
                                var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                                using (var parameters = new EncoderParameters(1))
                                using (var quality = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 98L))
                                {
                                    parameters.Param[0] = quality;
                                    page.Save(jpeg, encoder, parameters);
                                }
                                pages.Add(jpeg.ToArray());
                            }
                        }

                        firstPage = false;
                    } while (remaining.Length > 0);
                }
            }

            WritePdf(output, pages);
        }

        private static void DrawMeta(Graphics graphics, string heading, string value, float x, float y, float width, Font caption, Font body, Brush headingBrush, Brush valueBrush)
        {
            graphics.DrawString(heading, caption, headingBrush, x, y);
            string display = String.IsNullOrWhiteSpace(value) ? "—" : value;
            float size = body.Size;
            while (size > 14)
            {
                using (var measureFont = new Font(body.FontFamily, size, body.Style, GraphicsUnit.Pixel))
                    if (graphics.MeasureString(display, measureFont).Width <= width) break;
                size--;
            }
            using (var fitted = new Font(body.FontFamily, size, body.Style, GraphicsUnit.Pixel))
            using (var textFormat = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                graphics.DrawString(display, fitted, valueBrush, new RectangleF(x, y + 23, width, 34), textFormat);
        }

        private static void WritePdf(Stream output, IList<byte[]> pages)
        {
            // The report is one JPEG per A4 page so the portable build needs no PDF runtime.
            var offsets = new List<long> { 0 };
            WriteAscii(output, "%PDF-1.4\n");
            StartObject(output, offsets);
            WriteAscii(output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            StartObject(output, offsets);
            WriteAscii(output, "<< /Type /Pages /Count " + pages.Count + " /Kids [");
            for (int i = 0; i < pages.Count; i++) WriteAscii(output, (3 + i * 3) + " 0 R ");
            WriteAscii(output, "] >>\nendobj\n");

            for (int i = 0; i < pages.Count; i++)
            {
                int page = 3 + i * 3;
                StartObject(output, offsets);
                WriteAscii(output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /XObject << /Report "
                    + (page + 2) + " 0 R >> >> /Contents " + (page + 1) + " 0 R >>\nendobj\n");

                StartObject(output, offsets);
                byte[] operations = Encoding.ASCII.GetBytes("q\n595.28 0 0 841.89 0 0 cm\n/Report Do\nQ\n");
                WriteStream(output, "", operations);

                StartObject(output, offsets);
                WriteStream(output, "/Type /XObject /Subtype /Image /Width " + (Width * 2) + " /Height " + (Height * 2)
                    + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode ", pages[i]);
            }

            long xref = output.Position;
            WriteAscii(output, "xref\n0 " + offsets.Count + "\n0000000000 65535 f \n");
            for (int i = 1; i < offsets.Count; i++)
                WriteAscii(output, offsets[i].ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
            WriteAscii(output, "trailer\n<< /Size " + offsets.Count + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        }

        private static void StartObject(Stream stream, List<long> offsets)
        {
            offsets.Add(stream.Position);
            WriteAscii(stream, (offsets.Count - 1) + " 0 obj\n");
        }

        private static void WriteStream(Stream stream, string attributes, byte[] data)
        {
            WriteAscii(stream, "<< " + attributes + "/Length " + data.Length + " >>\nstream\n");
            stream.Write(data, 0, data.Length);
            WriteAscii(stream, "\nendstream\nendobj\n");
        }

        private static void WriteAscii(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    internal static class ExportSmokeTest
    {
        public static void Run(string directory)
        {
            var expectedView = new CameraView { X = 4938.65, Y = 9977.89, Z = 153.97, TargetX = 5048.43, TargetY = 9979.53, TargetZ = 60.37 };
            var sameDirection = new CameraView { X = expectedView.X, Y = expectedView.Y, Z = expectedView.Z, TargetX = expectedView.X + (expectedView.TargetX - expectedView.X) * 0.2, TargetY = expectedView.Y + (expectedView.TargetY - expectedView.Y) * 0.2, TargetZ = expectedView.Z + (expectedView.TargetZ - expectedView.Z) * 0.2 };
            var reportedView = new CameraView { X = expectedView.X, Y = expectedView.Y, Z = expectedView.Z, TargetX = 4944.08, TargetY = 9978.78, TargetZ = 153.97 };
            if (!FreedomCamera.MatchesPositionAndDirection(expectedView, sameDirection) || FreedomCamera.MatchesPositionAndDirection(expectedView, reportedView))
                throw new InvalidOperationException("Camera viewpoint direction verification failed.");
            if (FreedomCamera.ParseOcrNumber("0") != 0 || FreedomCamera.ParseOcrNumber("-120") != -120 ||
                Math.Abs(FreedomCamera.ParseOcrNumber("4993,45") - 4993.45) > 0.000001 ||
                Math.Abs(FreedomCamera.ParseOcrNumber("48.12345678") - 48.12345678) > 0.000000001)
                throw new InvalidOperationException("Camera numeric reader regression check failed.");
            foreach (string invalid in new[] { "", "-", "1.2.3", "1 23", "NaN" })
            {
                bool rejected = false;
                try { FreedomCamera.ParseOcrNumber(invalid); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Camera numeric reader accepted invalid text.");
            }
            var rolledCamera = FreedomCamera.WithRoll(new CameraView { TargetY = 1, HorizontalFov = 50 }, 90);
            if (Math.Abs(rolledCamera.UpX + 1) > 0.000001 || Math.Abs(rolledCamera.UpZ) > 0.000001 || Math.Abs(FreedomCamera.CameraRoll(rolledCamera) - 90) > 0.000001)
                throw new InvalidOperationException("Camera roll regression check failed.");
            var negativeRoll = FreedomCamera.WithRoll(new CameraView { TargetX = 3, TargetY = 4, TargetZ = -2, HorizontalFov = 50 }, -37.5);
            if (Math.Abs(FreedomCamera.CameraRoll(negativeRoll) + 37.5) > 0.000001)
                throw new InvalidOperationException("Tilted camera roll regression check failed.");
            Directory.CreateDirectory(directory);
            byte[] snapshot;
            using (var image = new Bitmap(960, 500))
            using (var graphics = Graphics.FromImage(image))
            using (var pipe = new Pen(Color.FromArgb(21, 125, 160), 26))
            {
                graphics.Clear(Color.FromArgb(232, 237, 242));
                graphics.DrawRectangle(Pens.SlateGray, 70, 75, 820, 350);
                graphics.DrawLine(pipe, 105, 230, 850, 230);
                graphics.FillRectangle(Brushes.DarkOrange, 500, 155, 95, 150);
                snapshot = NativeCapture.EncodeSnapshot(image);
            }

            var issues = new List<Issue>
            {
                new Issue { Id = Guid.NewGuid(), SnapshotId = Guid.NewGuid(), Title = "Leitung kollidiert mit Träger", Status = "In Bearbeitung",
                    Description = "Ebene 02, Achse B. Prüfen, ob die Leitung unter dem Träger geführt werden kann.",
                    Author = "Freedom Issues", CreatedUtc = DateTime.UtcNow, Snapshot = snapshot,
                    Camera = new CameraView { X = 1, Y = 2, Z = 3, TargetX = 4, TargetY = 6, TargetZ = 3, HorizontalFov = 50 } },
                new Issue { Id = Guid.NewGuid(), SnapshotId = Guid.NewGuid(), Title = "Durchführung prüfen", Status = "Erledigt",
                    Description = new StringBuilder().Insert(0, "Die Öffnung ist mit der Fachplanung abgestimmt. Bitte Maße, Höhe und Lage prüfen.\r\n", 85).ToString(),
                    Author = "Freedom Issues", CreatedUtc = DateTime.UtcNow }
            };

            var store = new IssueStore(Path.Combine(directory, "smoke.xml"));
            issues[0].Author = "Anna Müller";
            issues[0].AssignedTo = "Samira@example.org";
            var commentId = Guid.NewGuid();
            issues[0].Comments.Add(new IssueComment { Id = commentId, CreatedUtc = DateTime.UtcNow, Author = "Reviewer", Text = "Bitte prüfen.\nZweite Zeile: äöü & <Detail>", ViewpointId = issues[0].SnapshotId, ModifiedUtc = DateTime.UtcNow, ModifiedAuthor = "Editor" });
            issues[1].Comments.Add(new IssueComment { Id = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow, Author = "Reviewer", Text = "Kommentar ohne Bild und Kamera" });
            store.Save(issues);
            store.Save(issues);
            var reloaded = store.Load();
            if (reloaded.Count != 2 || reloaded[0].Id != issues[0].Id || reloaded[0].Snapshot.Length != snapshot.Length)
                throw new InvalidOperationException("Issue persistence check failed.");

            AtomicFile.Write(Path.Combine(directory, "smoke.bcfzip"), stream => BcfExport.Write(stream, reloaded));
            using (var archive = ZipFile.OpenRead(Path.Combine(directory, "smoke.bcfzip")))
            {
                var entry = archive.GetEntry(issues[0].Id.ToString("D") + "/viewpoint.bcfv");
                if (entry == null) throw new InvalidOperationException("Missing camera viewpoint.");
                using (var input = entry.Open())
                {
                    var view = XDocument.Load(input);
                    var direction = view.Root.Element("PerspectiveCamera").Element("CameraDirection");
                    if (Math.Abs(XmlConvert.ToDouble(direction.Element("X").Value) - 0.6) > 0.00001) throw new InvalidOperationException("Incorrect camera direction.");
                }
            }
            AtomicFile.Write(Path.Combine(directory, "smoke.pdf"), stream => PdfExport.Write(stream, reloaded));
            var imported = BcfImport.Read(Path.Combine(directory, "smoke.bcfzip"));
            if (reloaded[0].Author != "Anna Müller" || reloaded[0].AssignedTo != "Samira@example.org" || imported[0].Author != "Anna Müller" || imported[0].AssignedTo != "Samira@example.org" || !String.IsNullOrEmpty(imported[1].AssignedTo))
                throw new InvalidOperationException("Issue author and assignment roundtrip failed.");
            if (reloaded[0].Comments.Count != 1 || imported[0].Comments.Count != 1 || imported[0].Comments[0].Id != commentId ||
                imported[0].Comments[0].Text != issues[0].Comments[0].Text || imported[0].Comments[0].Author != "Reviewer" ||
                imported[0].Comments[0].ViewpointId != issues[0].SnapshotId || imported[0].Comments[0].ModifiedAuthor != "Editor" ||
                imported[1].Comments.Count != 1 || imported[1].Comments[0].Text != issues[1].Comments[0].Text)
                throw new InvalidOperationException("BCF comment roundtrip check failed.");
            AtomicFile.Write(Path.Combine(directory, "comments-roundtrip.bcfzip"), stream => BcfExport.Write(stream, imported));
            if (BcfImport.Read(Path.Combine(directory, "comments-roundtrip.bcfzip")) [0].Comments.Count != 1)
                throw new InvalidOperationException("BCF roundtrip duplicated comments.");
            if (imported.Count != 2 || imported[0].Id != issues[0].Id || imported[0].Snapshot == null || imported[0].Status != "In Bearbeitung" || imported[0].Camera == null || Math.Abs(imported[0].Camera.TargetX - imported[0].Camera.X - 0.6) > 0.00001)
                throw new InvalidOperationException("BCF 2.1 import check failed.");

            string sample30 = Path.Combine(directory, "sample30.bcf");
            AtomicFile.Write(sample30, stream =>
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    using (var entry = archive.CreateEntry("bcf.version").Open())
                        new XDocument(new XElement("Version", new XAttribute("VersionId", "3.0"))).Save(entry);
                    using (var entry = archive.CreateEntry(issues[0].Id.ToString("D") + "/markup.bcf").Open())
                        new XDocument(new XElement("Markup", new XElement("Topic", new XAttribute("Guid", issues[0].Id), new XAttribute("TopicStatus", "Closed"), new XAttribute("TopicType", "Issue"), new XElement("Title", "BCF 3.0"), new XElement("CreationDate", "2026-11-24T10:00:00Z"), new XElement("CreationAuthor", "Tester"),
                            new XElement("Comments", new XElement("Comment", new XAttribute("Guid", commentId), new XElement("Date", "2026-11-24T10:00:00Z"), new XElement("Author", "Tester"), new XElement("Comment", "BCF 3 Kommentar")))))).Save(entry);
                }
            });
            var imported30 = BcfImport.Read(sample30);
            if (imported30.Count != 1 || imported30[0].Status != "Erledigt" || imported30[0].Comments.Count != 1 || imported30[0].Comments[0].Text != "BCF 3 Kommentar") throw new InvalidOperationException("BCF 3.0 import check failed.");
        }
    }
}

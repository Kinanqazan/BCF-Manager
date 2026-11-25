using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace FreedomIssues
{
    // Freedom has no supported plug-in API. This experiment reads its visible camera dialog.
    internal static class FreedomCamera
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public Rect CaretRect;
        }
        private sealed class Field { public int X, Y; public string Text; }
        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxLength);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int maxLength);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);

        private const uint WmGetText = 0x000D;
        private const uint WmClose = 0x0010;

        public static CameraView Capture(IntPtr freedom)
        {
            IntPtr dialog = OpenDialog(freedom);
            try { return ReadCamera(dialog); }
            finally { if (IsWindow(dialog)) PostMessage(dialog, WmClose, IntPtr.Zero, IntPtr.Zero); }
        }

        private static CameraView ReadCamera(IntPtr dialog, bool layoutVerified = false)
        {
            try { return ReadDialog(dialog); }
            catch (InvalidOperationException) { return ReadDialogWithOcr(dialog, layoutVerified); }
        }

        private static IntPtr OpenDialog(IntPtr freedom)
        {
            uint processId;
            GetWindowThreadProcessId(freedom, out processId);
            if (processId == 0) throw new InvalidOperationException("Das Freedom-Fenster wurde nicht gefunden.");
            if (FindDialog(processId) != IntPtr.Zero) throw new InvalidOperationException("Bitte den geöffneten Ansichtspunkt-Dialog in Freedom schließen und erneut versuchen.");
            ActivateFreedom(freedom);
            if (GetForegroundWindow() != freedom)
                throw new InvalidOperationException("Freedom konnte nicht aktiviert werden.");

            // These are the ribbon key tips observed in Navisworks Freedom 2026.
            SendKeys.SendWait("%");
            Thread.Sleep(100);
            SendKeys.SendWait("v");
            Thread.Sleep(100);
            SendKeys.SendWait("e");
            Thread.Sleep(100);
            SendKeys.SendWait("v");
            IntPtr dialog = IntPtr.Zero;
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 3500 && dialog == IntPtr.Zero)
            {
                Application.DoEvents();
                Thread.Sleep(100);
                dialog = FindDialog(processId);
            }
            if (dialog == IntPtr.Zero)
                throw new InvalidOperationException("Der Kamera-Dialog konnte nicht automatisch geöffnet werden. Die Erfassung unterstützt derzeit Freedom 2026 mit deutscher Oberfläche.");

            return dialog;
        }

        public static IntPtr PrepareNavigation(IntPtr freedom, CameraView camera, out double rollDifference)
        {
            if (camera.IsOrthographic) throw new InvalidOperationException("Zur Ansicht unterstützt derzeit Perspektivkameras. Bitte im Modell Perspektive wählen.");
            double dx = camera.TargetX - camera.X, dy = camera.TargetY - camera.Y, dz = camera.TargetZ - camera.Z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < 0.000001 || Double.IsNaN(length) || Double.IsInfinity(length) || Double.IsNaN(camera.HorizontalFov) || camera.HorizontalFov <= 0 || camera.HorizontalFov >= 180)
                throw new InvalidOperationException("Die gespeicherten Kamerawerte sind ungültig.");
            double roll = CameraRoll(camera);

            IntPtr dialog = OpenDialog(freedom);
            bool readyForConfirmation = false;
            try
            {
                // Reading first verifies the dialog layout, units and current camera mode.
                CameraView before = ReadCamera(dialog);
                double[] values = { camera.X, camera.Y, camera.Z, camera.TargetX, camera.TargetY, camera.TargetZ };
                CameraView actual = before;
                if (!MatchesFraming(camera, before))
                {
                    try
                    {
                        SetFraming(dialog, camera, before, values, fast: true);
                        actual = ReadCamera(dialog, layoutVerified: true);
                    }
                    catch (InvalidOperationException)
                    {
                        if (GetForegroundWindow() != dialog) throw;
                        actual = null;
                    }
                    if (actual == null || !MatchesFraming(camera, actual))
                    {
                        bool lensAndPositionMatch = actual != null && MatchesPositionAndLens(camera, actual);
                        if (!lensAndPositionMatch)
                        {
                            SetFraming(dialog, camera, actual, values, fast: false);
                            actual = ReadCamera(dialog, layoutVerified: true);
                        }
                        if (MatchesPositionAndLens(camera, actual) && DirectionDifference(camera, actual) > 0.5)
                        {
                            // Retry from the settled position, then use tilt if the target
                            // height was recalculated during the coordinate edits.
                            SetCoordinates(dialog, 103, values, 3);
                            SetField(dialog, 233, 197, Math.Atan2(dz, Math.Sqrt(dx * dx + dy * dy)) * 180 / Math.PI);
                            actual = ReadCamera(dialog, layoutVerified: true);
                        }
                    }
                }
                if (MatchesFraming(camera, actual) && RollDifference(roll, actual) > 0.1 &&
                    SetField(dialog, 233, 174, roll, requireExactFocus: true))
                {
                    // Freedom can adjust roll when it solves the target and tilt.
                    // Restore roll last and verify that it did not change the framing.
                    actual = ReadCamera(dialog, layoutVerified: true);
                }
                double[] received = { actual.X, actual.Y, actual.Z, actual.TargetX, actual.TargetY, actual.TargetZ };
                if (!MatchesFraming(camera, actual))
                {
                    string[] names = { "Position X", "Position Y", "Position Z", "Ziel X", "Ziel Y", "Ziel Z", "Sichtwinkel" };
                    double[] expected = values.Concat(new[] { camera.HorizontalFov }).ToArray();
                    double[] observed = received.Concat(new[] { actual.HorizontalFov }).ToArray();
                    string details = String.Join(Environment.NewLine, names.Select((name, index) => name + ": Soll " +
                        expected[index].ToString("0.00", CultureInfo.InvariantCulture) + ", gelesen " + observed[index].ToString("0.00", CultureInfo.InvariantCulture))) +
                        Environment.NewLine + "Richtungsabweichung: " + DirectionDifference(camera, actual).ToString("0.0", CultureInfo.InvariantCulture) + "°";
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "last-camera-navigation.txt"), details);
                    throw new InvalidOperationException("Die Kamerawerte konnten nicht sicher gesetzt werden. Die Ansicht wurde nicht übernommen." + Environment.NewLine + Environment.NewLine + details);
                }
                rollDifference = RollDifference(roll, actual);
                if (GetForegroundWindow() != dialog) throw new InvalidOperationException("Der Kamera-Dialog hat den Fokus verloren.");
                readyForConfirmation = true;
                return dialog;
            }
            finally { if (!readyForConfirmation && IsWindow(dialog)) PostMessage(dialog, WmClose, IntPtr.Zero, IntPtr.Zero); }
        }

        internal static bool IsViewDialogOpen(IntPtr dialog)
        {
            return IsWindowVisible(dialog);
        }

        internal static double DirectionDifference(CameraView expected, CameraView actual)
        {
            double ex = expected.TargetX - expected.X, ey = expected.TargetY - expected.Y, ez = expected.TargetZ - expected.Z;
            double ax = actual.TargetX - actual.X, ay = actual.TargetY - actual.Y, az = actual.TargetZ - actual.Z;
            double el = Math.Sqrt(ex * ex + ey * ey + ez * ez), al = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (el < 0.000001 || al < 0.000001 || Double.IsNaN(el) || Double.IsNaN(al) || Double.IsInfinity(el) || Double.IsInfinity(al)) return Double.PositiveInfinity;
            double cosine = (ex * ax + ey * ay + ez * az) / (el * al);
            return Math.Acos(Math.Max(-1, Math.Min(1, cosine))) * 180 / Math.PI;
        }

        internal static bool MatchesPositionAndDirection(CameraView expected, CameraView actual)
        {
            return MatchesPosition(expected, actual) && DirectionDifference(expected, actual) <= 0.5;
        }

        private static bool MatchesFraming(CameraView expected, CameraView actual)
        {
            return MatchesPositionAndLens(expected, actual) && DirectionDifference(expected, actual) <= 0.5;
        }

        private static bool MatchesPositionAndLens(CameraView expected, CameraView actual)
        {
            return MatchesPosition(expected, actual) && Math.Abs(expected.HorizontalFov - actual.HorizontalFov) <= 0.025;
        }

        private static double RollDifference(double expected, CameraView actual)
        {
            return Math.Abs(Math.IEEERemainder(CameraRoll(actual) - expected, 360));
        }

        private static bool MatchesPosition(CameraView expected, CameraView actual)
        {
            return Math.Abs(expected.X - actual.X) <= 0.025 && Math.Abs(expected.Y - actual.Y) <= 0.025 && Math.Abs(expected.Z - actual.Z) <= 0.025;
        }

        private static void SetFraming(IntPtr dialog, CameraView expected, CameraView current, double[] coordinates, bool fast)
        {
            if (current == null || !MatchesPosition(expected, current))
                SetCoordinates(dialog, 80, coordinates, 0, fast);
            if (current == null || Math.Abs(expected.HorizontalFov - current.HorizontalFov) > 0.025)
                SetField(dialog, 233, 151, expected.HorizontalFov, fast: fast);
            // Freedom can recalculate the target after moving the camera or changing the lens.
            SetCoordinates(dialog, 103, coordinates, 3, fast);
        }

        private static void SetCoordinates(IntPtr dialog, int y, double[] values, int start, bool fast = false)
        {
            for (int column = 0; column < 3; column++)
                SetField(dialog, 131 + column * 51, y, values[start + column], fast: fast);
        }

        private static bool SetField(IntPtr dialog, int referenceX, int referenceY, double value, bool requireExactFocus = false, bool fast = false)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidOperationException("Ungültiger Kamerawert.");
            Rect bounds;
            if (!GetWindowRect(dialog, out bounds) || GetForegroundWindow() != dialog) throw new InvalidOperationException("Der Kamera-Dialog hat den Fokus verloren.");
            int x = bounds.Left + (int)(referenceX * (bounds.Right - bounds.Left) / 459.0);
            int y = bounds.Top + (int)(referenceY * (bounds.Bottom - bounds.Top) / 414.0);
            SetCursorPos(x, y);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(fast ? 25 : 60);
            uint processId;
            uint threadId = GetWindowThreadProcessId(dialog, out processId);
            var focus = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            // Freedom's custom numeric editor may retain keyboard focus on a shared parent.
            // Verify dialog ownership here. Read back every value before committing below.
            if (GetForegroundWindow() != dialog)
                throw new InvalidOperationException("Der Kamera-Dialog hat den Eingabefokus verloren. Bitte erneut versuchen.");
            bool hasDialogFocus = GetGUIThreadInfo(threadId, ref focus) && focus.Focus != IntPtr.Zero &&
                (focus.Focus == dialog || IsChild(dialog, focus.Focus));
            if (!requireExactFocus && !hasDialogFocus)
                throw new InvalidOperationException("Der Kamera-Dialog hat den Eingabefokus verloren. Bitte erneut versuchen.");
            if (requireExactFocus)
            {
                // A click on a locked roll box can leave focus on another field.
                Rect focused;
                bool focusedOnField = hasDialogFocus && focus.Focus != dialog && GetWindowRect(focus.Focus, out focused) &&
                    x >= focused.Left && x < focused.Right && y >= focused.Top && y < focused.Bottom &&
                    focused.Bottom - focused.Top <= (bounds.Bottom - bounds.Top) * 30.0 / 414;
                var caret = new ScreenPoint { X = focus.CaretRect.Left, Y = (focus.CaretRect.Top + focus.CaretRect.Bottom) / 2 };
                // Some numeric boxes report the parent as the focus window. The
                // caret, when available, still identifies the specific field.
                bool caretInField = focus.Caret != IntPtr.Zero && ClientToScreen(focus.Caret, ref caret) &&
                    Math.Abs(caret.X - x) <= (bounds.Right - bounds.Left) * 25.0 / 459 &&
                    Math.Abs(caret.Y - y) <= (bounds.Bottom - bounds.Top) * 8.0 / 414;
                if (!focusedOnField && !caretInField && !ShowsTextSelection(bounds, x, y)) return false;
            }
            if (GetForegroundWindow() != dialog) throw new InvalidOperationException("Der Kamera-Dialog hat den Eingabefokus verloren. Bitte erneut versuchen.");
            // Match the dialog's displayed precision so text does not scroll out of
            // the narrow editor before the screenshot-based verification.
            string text = value.ToString("0.00", CultureInfo.CurrentCulture);
            // Commit each field before the next click, so the next editor cannot
            // inherit a change still waiting to be processed by Freedom.
            if (fast)
            {
                SendKeys.SendWait("^a" + text + "{TAB}");
                Thread.Sleep(15);
            }
            else
            {
                SendKeys.SendWait("^a");
                SendKeys.SendWait(text);
                SendKeys.SendWait("{TAB}");
                Thread.Sleep(40);
            }
            return true;
        }

        private static bool ShowsTextSelection(Rect bounds, int x, int y)
        {
            // Freedom can highlight the input's text while exposing only its shared parent.
            // Look inside the box, away from its focus border, for several filled highlight rows.
            int halfWidth = Math.Max(5, (bounds.Right - bounds.Left) * 20 / 459);
            int halfHeight = Math.Max(3, (bounds.Bottom - bounds.Top) * 6 / 414);
            using (var image = new Bitmap(halfWidth * 2, halfHeight * 2))
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.CopyFromScreen(x - halfWidth, y - halfHeight, 0, 0, image.Size);
                Color highlight = SystemColors.Highlight;
                int rows = 0;
                for (int row = 0; row < image.Height; row++)
                {
                    int selected = 0;
                    for (int column = 0; column < image.Width; column++)
                    {
                        Color pixel = image.GetPixel(column, row);
                        bool systemHighlight = Math.Abs(pixel.R - highlight.R) <= 16 &&
                            Math.Abs(pixel.G - highlight.G) <= 16 && Math.Abs(pixel.B - highlight.B) <= 16;
                        bool freedomHighlight = pixel.B > pixel.R + 25 && pixel.B > pixel.G + 12 && pixel.G > 75;
                        if (systemHighlight || freedomHighlight) selected++;
                    }
                    if (selected >= image.Width / 4) rows++;
                }
                return rows >= image.Height / 2;
            }
        }

        private static CameraView ReadDialogWithOcr(IntPtr dialog, bool layoutVerified)
        {
            string ocr = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ocr", "tesseract.exe");
            string data = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "tessdata");
            if (!File.Exists(ocr) || !File.Exists(Path.Combine(data, "eng.traineddata")))
                throw new InvalidOperationException("Die OCR-Komponente fehlt. Bitte den vollständigen Ordner ‚tools‘ neben BCF-Manager.exe kopieren.");
            Rect bounds;
            if (!GetWindowRect(dialog, out bounds) || bounds.Right - bounds.Left < 250 || bounds.Bottom - bounds.Top < 250)
                throw new InvalidOperationException("Die Kamera-Dialoggröße konnte nicht erkannt werden.");
            if (GetForegroundWindow() != dialog) SetForegroundWindow(dialog);
            if (!layoutVerified) Thread.Sleep(250);
            if (GetForegroundWindow() != dialog) throw new InvalidOperationException("Der Kamera-Dialog ist nicht im Vordergrund.");
            // Commit the edit and remove selection/caret highlighting before OCR.
            // This is the blank label area, outside all numeric editors.
            SetCursorPos(bounds.Left + (int)(20 * (bounds.Right - bounds.Left) / 459.0),
                bounds.Top + (int)(60 * (bounds.Bottom - bounds.Top) / 414.0));
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(100);
            if (GetForegroundWindow() != dialog) throw new InvalidOperationException("Der Kamera-Dialog hat den Fokus verloren.");

            using (var image = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, image.Size);
                double[] values;
                if (!layoutVerified) VerifyUnits(image, ocr, data);
                try { values = ReadBatch(image, ocr, data); }
                catch (InvalidOperationException) { values = ReadSeparate(image, ocr, data); }
                var positions = values.Take(6).ToArray();
                double fov = values[6], roll = values[7];
                double distance = Math.Sqrt(Math.Pow(positions[3] - positions[0], 2) + Math.Pow(positions[4] - positions[1], 2) + Math.Pow(positions[5] - positions[2], 2));
                if (distance < 0.01)
                    throw new InvalidOperationException("OCR hat identische Kameraposition und Zielposition gelesen. Bitte den Ansichtspunkt-Dialog prüfen.");
                if (fov <= 0 || fov >= 180 || Math.Abs(roll) > 360)
                    throw new InvalidOperationException("OCR hat unplausible Kamerawinkel gelesen (Sichtwinkel " + fov.ToString("0.##", CultureInfo.CurrentCulture) + "°, Roll " + roll.ToString("0.##", CultureInfo.CurrentCulture) + "°).");
                return WithRoll(new CameraView { X = positions[0], Y = positions[1], Z = positions[2], TargetX = positions[3], TargetY = positions[4], TargetZ = positions[5], HorizontalFov = fov }, roll);
            }
        }

        private static void VerifyUnits(Bitmap image, string executable, string data)
        {
            string units = Recognize(image, new Rectangle(8, 66, 100, 24), executable, data, false);
            if (units.IndexOf("m", StringComparison.OrdinalIgnoreCase) < 0 || units.IndexOf("pos", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Die Einheit konnte nicht sicher als Meter erkannt werden.");
        }

        internal static double[] ReadBatch(Bitmap image, string executable, string data)
        {
            var regions = new List<Rectangle>();
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 3; col++) regions.Add(new Rectangle(106 + col * 51, 69 + row * 23, 51, 21));
            regions.Add(new Rectangle(208, 140, 51, 22));
            regions.Add(new Rectangle(208, 163, 51, 22));
            double sx = image.Width / 459.0, sy = image.Height / 414.0;
            using (var batch = new Bitmap(500, regions.Count * 110 + 20))
            using (var canvas = Graphics.FromImage(batch))
            {
                canvas.Clear(Color.White);
                canvas.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                for (int i = 0; i < regions.Count; i++)
                {
                    var r = regions[i];
                    var area = Rectangle.FromLTRB((int)(r.Left * sx), (int)(r.Top * sy), (int)(r.Right * sx), (int)(r.Bottom * sy));
                    area.Intersect(new Rectangle(0, 0, image.Width, image.Height));
                    if (area.Width < 5 || area.Height < 5 || r.Width * 4 + 20 > batch.Width || r.Height * 4 > 104)
                        throw new InvalidOperationException("Der Kamera-Dialog hat ein unerwartetes Layout.");
                    canvas.DrawImage(image, new Rectangle(10, 10 + i * 110, r.Width * 4, r.Height * 4), area, GraphicsUnit.Pixel);
                }
                string text = RunOcr(batch, executable, data, 6, true);
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
                if (lines.Length != 8)
                    throw new InvalidOperationException("Kamerafelder im OCR-Stapel nicht eindeutig erkannt.");
                var result = new double[8];
                for (int i = 0; i < result.Length; i++)
                {
                    // Decimal precision is displayed by Freedom; never guess a dropped separator.
                    string numberText = System.Text.RegularExpressions.Regex.Replace(lines[i], @"\s*([\.,])\s*", "$1");
                    if (!System.Text.RegularExpressions.Regex.IsMatch(numberText, @"^-?\d+[\.,]\d{1,4}$"))
                        throw new InvalidOperationException("Kamerawert im OCR-Stapel nicht eindeutig erkannt: " + lines[i]);
                    result[i] = ParseOcrNumber(numberText);
                }
                return result;
            }
        }

        private static double[] ReadSeparate(Bitmap image, string ocr, string data)
        {
            var values = new double[8];
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 3; col++)
                    values[row * 3 + col] = ParseOcrNumber(Recognize(image, new Rectangle(106 + col * 51, 69 + row * 23, 51, 21), ocr, data, true));
            values[6] = ParseOcrNumber(Recognize(image, new Rectangle(208, 140, 51, 22), ocr, data, true));
            values[7] = ParseOcrNumber(Recognize(image, new Rectangle(208, 163, 51, 22), ocr, data, true));
            return values;
        }

        private static string Recognize(Bitmap image, Rectangle reference, string executable, string data, bool number)
        {
            double sx = image.Width / 459.0, sy = image.Height / 414.0;
            Rectangle area = Rectangle.FromLTRB((int)(reference.Left * sx), (int)(reference.Top * sy), (int)(reference.Right * sx), (int)(reference.Bottom * sy));
            area.Intersect(new Rectangle(0, 0, image.Width, image.Height));
            if (area.Width < 5 || area.Height < 5) throw new InvalidOperationException("Der Kamera-Dialog hat ein unerwartetes Layout.");
            using (var enlarged = new Bitmap(area.Width * 4 + 24, area.Height * 4 + 24))
            using (var canvas = Graphics.FromImage(enlarged))
            {
                canvas.Clear(Color.White);
                canvas.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                canvas.DrawImage(image, new Rectangle(12, 12, area.Width * 4, area.Height * 4), area, GraphicsUnit.Pixel);
                return RunOcr(enlarged, executable, data, 7, number);
            }
        }

        private static string RunOcr(Bitmap image, string executable, string data, int mode, bool number)
        {
            string path = Path.Combine(Path.GetTempPath(), "bcf-ocr-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                image.Save(path, ImageFormat.Png);
                string args = "\"" + path + "\" stdout --tessdata-dir \"" + data + "\" -l eng --psm " + mode;
                if (number) args += " -c tessedit_char_whitelist=0123456789,.-";
                var start = new ProcessStartInfo(executable, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(start))
                {
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    string errors = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(5000) || process.ExitCode != 0) throw new InvalidOperationException("OCR fehlgeschlagen: " + errors);
                    return output;
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        internal static double ParseOcrNumber(string value)
        {
            value = value.Trim().Replace(',', '.');
            double number;
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^-?\d+(?:\.\d{1,8})?$") || !Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || Double.IsNaN(number) || Double.IsInfinity(number))
                throw new InvalidOperationException("Ein Kamerawert konnte nicht sicher erkannt werden. Bitte Dialogansicht prüfen.");
            return number;
        }

        private static void ActivateFreedom(IntPtr window)
        {
            uint ignored;
            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out ignored);
            uint currentThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
            var wait = Stopwatch.StartNew();
            while (GetForegroundWindow() != window && wait.ElapsedMilliseconds < 1200)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
        }

        private static IntPtr FindDialog(uint processId)
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != processId || !IsWindowVisible(window)) return true;
                var title = new StringBuilder(512);
                GetWindowText(window, title, title.Capacity);
                string name = title.ToString();
                if (name.IndexOf("Ansichtspunkt bearbeiten", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Aktueller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Edit Viewpoint", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0)
                { result = window; return false; }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static CameraView ReadDialog(IntPtr dialog)
        {
            var fields = new List<Field>();
            var labels = new List<string>();
            EnumChildWindows(dialog, delegate(IntPtr child, IntPtr unused)
            {
                if (!IsWindowVisible(child)) return true;
                var type = new StringBuilder(256);
                GetClassName(child, type, type.Capacity);
                var value = new StringBuilder(512);
                IntPtr ignored;
                if (SendMessageTimeout(child, WmGetText, new IntPtr(value.Capacity), value, 2, 300, out ignored) == IntPtr.Zero) return true;
                string className = type.ToString();
                if (className.Equals("Static", StringComparison.OrdinalIgnoreCase)) labels.Add(value.ToString());
                if (!className.Equals("Edit", StringComparison.OrdinalIgnoreCase)) return true;
                Rect bounds;
                if (GetWindowRect(child, out bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                    fields.Add(new Field { X = bounds.Left, Y = (bounds.Top + bounds.Bottom) / 2, Text = value.ToString().Trim() });
                return true;
            }, IntPtr.Zero);

            var positionLabel = labels.FirstOrDefault(label => label.IndexOf("Position", StringComparison.OrdinalIgnoreCase) >= 0 && label.Contains("("));
            if (positionLabel == null) throw new InvalidOperationException("Die Anzeigeeinheit muss per OCR geprüft werden.");
            if (positionLabel != null && positionLabel.IndexOf("(m)", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Bitte die Freedom-Anzeigeeinheit auf Meter stellen.");

            var rows = new List<List<Field>>();
            foreach (var field in fields.OrderBy(f => f.Y).ThenBy(f => f.X))
            {
                var row = rows.FirstOrDefault(r => Math.Abs(r[0].Y - field.Y) <= 4);
                if (row == null) { row = new List<Field>(); rows.Add(row); }
                row.Add(field);
            }
            var triples = rows.Where(row => row.Count >= 3).OrderBy(row => row[0].Y).Take(2).ToList();
            if (triples.Count != 2) throw new InvalidOperationException("Die Kamerafelder konnten nicht automatisch gelesen werden.");
            var first = triples[0].OrderBy(field => field.X).Take(3).ToList();
            var second = triples[1].OrderBy(field => field.X).Take(3).ToList();
            if (first.Where((field, index) => Math.Abs(field.X - second[index].X) > 10).Any())
                throw new InvalidOperationException("Die Kamerafelder konnten nicht eindeutig zugeordnet werden.");

            // The next two camera fields are the vertical and horizontal fields of view.
            var angles = fields.Where(field => field.Y > second[0].Y + 4 && Math.Abs(field.X - first[2].X) <= 15).OrderBy(field => field.Y).Take(3).ToList();
            if (angles.Count < 3) throw new InvalidOperationException("Die Kamerawinkel konnten nicht automatisch gelesen werden.");
            double[] v = first.Concat(second).Concat(angles).Select(field => ParseNumber(field.Text)).ToArray();
            double distance = Math.Sqrt(Math.Pow(v[3] - v[0], 2) + Math.Pow(v[4] - v[1], 2) + Math.Pow(v[5] - v[2], 2));
            if (distance < 0.000001 || v[7] <= 0 || v[7] >= 180)
                throw new InvalidOperationException("Die gelesenen Kamerawerte sind ungültig.");
            return WithRoll(new CameraView { X = v[0], Y = v[1], Z = v[2], TargetX = v[3], TargetY = v[4], TargetZ = v[5], HorizontalFov = v[7] }, v[8]);
        }

        internal static CameraView WithRoll(CameraView camera, double degrees)
        {
            double[] direction, up, right;
            CameraBasis(camera, out direction, out up, out right);
            double radians = degrees * Math.PI / 180;
            camera.HasUpVector = true;
            camera.UpX = up[0] * Math.Cos(radians) - right[0] * Math.Sin(radians);
            camera.UpY = up[1] * Math.Cos(radians) - right[1] * Math.Sin(radians);
            camera.UpZ = up[2] * Math.Cos(radians) - right[2] * Math.Sin(radians);
            return camera;
        }

        internal static double CameraRoll(CameraView camera)
        {
            if (!camera.HasUpVector) return 0;
            double[] direction, up, right;
            CameraBasis(camera, out direction, out up, out right);
            double vertical = camera.UpX * up[0] + camera.UpY * up[1] + camera.UpZ * up[2];
            double lateral = camera.UpX * right[0] + camera.UpY * right[1] + camera.UpZ * right[2];
            if (Double.IsNaN(vertical) || Double.IsInfinity(vertical) || Double.IsNaN(lateral) || Double.IsInfinity(lateral) || Math.Sqrt(vertical * vertical + lateral * lateral) < 0.000001)
                throw new InvalidOperationException("Die gespeicherte Kamera-Hochachse ist ungültig.");
            return Math.Atan2(-lateral, vertical) * 180 / Math.PI;
        }

        private static void CameraBasis(CameraView camera, out double[] direction, out double[] up, out double[] right)
        {
            double x = camera.TargetX - camera.X, y = camera.TargetY - camera.Y, z = camera.TargetZ - camera.Z;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length < 0.000001 || Double.IsNaN(length) || Double.IsInfinity(length)) throw new InvalidOperationException("Ungültige Kamerarichtung.");
            x /= length; y /= length; z /= length;
            double horizontal = Math.Sqrt(x * x + y * y);
            direction = new[] { x, y, z };
            up = horizontal < 0.000001 ? new[] { 0.0, 1.0, 0.0 } : new[] { -x * z / horizontal, -y * z / horizontal, horizontal };
            right = new[] { y * up[2] - z * up[1], z * up[0] - x * up[2], x * up[1] - y * up[0] };
        }

        private static double ParseNumber(string input)
        {
            double number;
            if ((!Double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out number) && !Double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) || Double.IsNaN(number) || Double.IsInfinity(number))
                throw new InvalidOperationException("Ein Kamerawert konnte nicht als Zahl gelesen werden.");
            return number;
        }
    }
}

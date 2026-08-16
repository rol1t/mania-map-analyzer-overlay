using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace ManiaMapAnalyzerOverlay
{
    internal sealed class OverlayStyleDialog : Form
    {
        private readonly ComboBox layoutBox;
        private readonly TrackBar scaleTrack;
        private readonly Label scaleValue;
        private readonly Label description;
        private readonly string customCssPath;
        private readonly bool english;

        public string LayoutMode { get; private set; }
        public int ScalePercent { get; private set; }

        public OverlayStyleDialog(string layoutMode, int scalePercent, string cssPath, bool useEnglish)
        {
            english = useEnglish;
            customCssPath = cssPath;
            Text = Pick("Оформление оверлея", "Overlay appearance");
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(620, 430);
            BackColor = Color.FromArgb(18, 21, 29);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            var title = new Label();
            title.Text = Pick("Вид и размер оверлея", "Overlay layout and size");
            title.Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point);
            title.AutoSize = true;
            title.Location = new Point(22, 18);

            var subtitle = new Label();
            subtitle.Text = Pick("Выберите готовый формат или подключите обычный CSS-файл.", "Choose a preset or use a regular CSS file.");
            subtitle.ForeColor = Color.FromArgb(166, 174, 196);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(25, 55);

            var layoutLabel = CreateCaption(Pick("Формат", "Layout"), 24, 92);
            layoutBox = new ComboBox();
            layoutBox.DropDownStyle = ComboBoxStyle.DropDownList;
            layoutBox.FlatStyle = FlatStyle.Flat;
            layoutBox.BackColor = Color.FromArgb(38, 43, 56);
            layoutBox.ForeColor = Color.White;
            layoutBox.Location = new Point(24, 114);
            layoutBox.Size = new Size(270, 28);
            layoutBox.Items.AddRange(english
                ? new object[] { "Default", "Horizontal", "Companella", "Custom CSS" }
                : new object[] { "По умолчанию", "Горизонтальный", "Companella", "Пользовательский CSS" });
            layoutBox.SelectedIndex = layoutMode == "horizontal" ? 1 : layoutMode == "companella" ? 2 : layoutMode == "custom" ? 3 : 0;

            description = new Label();
            description.Location = new Point(314, 94);
            description.Size = new Size(282, 58);
            description.ForeColor = Color.FromArgb(190, 198, 218);

            var scaleLabel = CreateCaption(Pick("Размер оверлея", "Overlay size"), 24, 169);
            scaleTrack = new TrackBar();
            scaleTrack.Minimum = 50;
            scaleTrack.Maximum = 180;
            scaleTrack.TickFrequency = 10;
            scaleTrack.SmallChange = 5;
            scaleTrack.LargeChange = 10;
            scaleTrack.Value = Math.Max(50, Math.Min(180, scalePercent));
            scaleTrack.Location = new Point(18, 191);
            scaleTrack.Size = new Size(500, 45);
            scaleTrack.BackColor = BackColor;

            scaleValue = new Label();
            scaleValue.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold, GraphicsUnit.Point);
            scaleValue.TextAlign = ContentAlignment.MiddleCenter;
            scaleValue.Location = new Point(525, 191);
            scaleValue.Size = new Size(70, 34);
            scaleValue.BackColor = Color.FromArgb(38, 43, 56);

            var wheelHint = new Label();
            wheelHint.Text = Pick("Ctrl + колесо мыши меняет нативный размер без размытия текста.", "Ctrl + mouse wheel changes the native size without blurring text.");
            wheelHint.AutoSize = true;
            wheelHint.ForeColor = Color.FromArgb(139, 184, 218);
            wheelHint.Location = new Point(25, 237);

            var cssLabel = CreateCaption(Pick("Файл пользовательского стиля", "Custom style file"), 24, 273);
            var cssPathBox = new TextBox();
            cssPathBox.ReadOnly = true;
            cssPathBox.Text = customCssPath;
            cssPathBox.BackColor = Color.FromArgb(31, 35, 46);
            cssPathBox.ForeColor = Color.FromArgb(210, 215, 229);
            cssPathBox.BorderStyle = BorderStyle.FixedSingle;
            cssPathBox.Location = new Point(24, 295);
            cssPathBox.Size = new Size(572, 24);

            var openCssButton = CreateDialogButton(Pick("Открыть CSS", "Open CSS"), 24, 330, 120);
            openCssButton.Click += delegate { OpenCustomCss(); };

            var addonSettingsButton = CreateDialogButton(Pick("Настройки анализатора", "Analyser settings"), 153, 330, 175);
            addonSettingsButton.Click += delegate
            {
                DialogResult = DialogResult.Yes;
                Close();
            };

            var cancelButton = CreateDialogButton(Pick("Отмена", "Cancel"), 399, 376, 92);
            cancelButton.DialogResult = DialogResult.Cancel;
            var applyButton = CreateDialogButton(Pick("Применить", "Apply"), 500, 376, 96);
            applyButton.BackColor = Color.FromArgb(51, 105, 145);
            applyButton.Click += delegate
            {
                LayoutMode = layoutBox.SelectedIndex == 1 ? "horizontal" : layoutBox.SelectedIndex == 2 ? "companella" : layoutBox.SelectedIndex == 3 ? "custom" : "default";
                ScalePercent = scaleTrack.Value;
                DialogResult = DialogResult.OK;
                Close();
            };

            int previousLayoutIndex = layoutBox.SelectedIndex;
            layoutBox.SelectedIndexChanged += delegate
            {
                if (layoutBox.SelectedIndex == 2 && previousLayoutIndex != 2)
                {
                    scaleTrack.Value = 100;
                    UpdateScaleLabel();
                }
                previousLayoutIndex = layoutBox.SelectedIndex;
                UpdateDescription();
            };
            scaleTrack.Scroll += delegate { UpdateScaleLabel(); };

            Controls.Add(title);
            Controls.Add(subtitle);
            Controls.Add(layoutLabel);
            Controls.Add(layoutBox);
            Controls.Add(description);
            Controls.Add(scaleLabel);
            Controls.Add(scaleTrack);
            Controls.Add(scaleValue);
            Controls.Add(wheelHint);
            Controls.Add(cssLabel);
            Controls.Add(cssPathBox);
            Controls.Add(openCssButton);
            Controls.Add(addonSettingsButton);
            Controls.Add(cancelButton);
            Controls.Add(applyButton);

            AcceptButton = applyButton;
            CancelButton = cancelButton;
            UpdateDescription();
            UpdateScaleLabel();
        }

        private string Pick(string russian, string englishText)
        {
            return english ? englishText : russian;
        }

        private Label CreateCaption(string text, int x, int y)
        {
            var label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Location = new Point(x, y);
            label.ForeColor = Color.FromArgb(232, 235, 244);
            label.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            return label;
        }

        private static Button CreateDialogButton(string text, int x, int y, int width)
        {
            var button = new Button();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(width, 34);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Color.FromArgb(45, 50, 64);
            button.ForeColor = Color.White;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private void UpdateDescription()
        {
            if (layoutBox.SelectedIndex == 1)
                description.Text = Pick("Широкая компоновка: оценка слева, полосы и график справа. Удобно размещать сверху или снизу экрана.", "Wide layout: rating on the left, bars and graph on the right. Suitable for the top or bottom of the screen.");
            else if (layoutBox.SelectedIndex == 2)
                description.Text = Pick("Самодостаточная компактная панель на 100%: обложка карты, вертикальные показатели, подробные строки и оценка.", "Compact 100% preset with cover art, vertical metrics, full descriptions and rating.");
            else if (layoutBox.SelectedIndex == 3)
                description.Text = Pick("Стиль берётся из overlay-custom.css. После сохранения файла снова нажмите «Применить».", "Styles are loaded from overlay-custom.css. Save the file and click Apply again.");
            else
                description.Text = Pick("Компактная вертикальная карточка. Подходит для размещения сбоку от игрового поля.", "Compact vertical card for placement beside the playfield.");
        }

        private void UpdateScaleLabel()
        {
            scaleValue.Text = scaleTrack.Value.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void OpenCustomCss()
        {
            try
            {
                if (!File.Exists(customCssPath))
                    throw new FileNotFoundException(Pick("CSS-файл не найден.", "CSS file was not found."), customCssPath);
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = "notepad.exe";
                startInfo.Arguments = "\"" + customCssPath + "\"";
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Pick("Не удалось открыть CSS-файл.\r\n\r\n", "Could not open the CSS file.\r\n\r\n") + ex.Message,
                    Pick("Оформление оверлея", "Overlay appearance"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

}

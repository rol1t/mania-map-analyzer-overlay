using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
namespace ManiaMapAnalyzerOverlay
{
    internal static class CustomCssService
    {
        private const string FileName = "overlay-custom.css";

        internal static string GetPath()
        {
            return Path.Combine(Application.StartupPath, FileName);
        }

        internal static void EnsureExists()
        {
            try
            {
                string path = GetPath();
                if (!File.Exists(path))
                    File.WriteAllText(path, GetTemplate(), new System.Text.UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string GetTemplate()
        {
            return @"/*
  Mania Map Analyzer Overlay custom style / Пользовательский стиль.

  EN: Open Appearance, choose Custom CSS, edit and save this file,
      then click Apply. Updates never overwrite this file.
  RU: Откройте «Оформление», выберите «Пользовательский CSS», измените
      и сохраните файл, затем нажмите «Применить». Обновления его не стирают.

  The size slider is applied on top of this CSS.
  Масштаб из настроек применяется поверх этого CSS.
*/

/* Main colors and transparency / Основные цвета и прозрачность. */
html.mma-layout-custom {
    /* --mma-host-width follows the native size slider / меняется ползунком. */
    --mma-custom-width: var(--mma-host-width, 475px);
    --glass: rgba(18, 22, 38, 0.92);
    --glass-border: rgba(255, 255, 255, 0.14);
    --text-primary: #f5f7ff;
    --text-soft: #a9b1d2;
    --track: rgba(255, 255, 255, 0.10);
    --card-radius: 16px;
}

/* Card size; height follows visible analyser sections / Размер карточки. */
html.mma-layout-custom .dashboard {
    width: var(--mma-custom-width) !important;
    min-width: var(--mma-custom-width) !important;
    max-width: var(--mma-custom-width) !important;
}

html.mma-layout-custom .card.main-card {
    width: 100% !important;
    min-width: 100% !important;
    max-width: 100% !important;
    border-radius: var(--card-radius) !important;
}

/* Examples / Примеры — uncomment the block you need.

html.mma-layout-custom .card.main-card {
    background: rgba(8, 10, 18, 0.82) !important;
    border-color: rgba(255, 80, 140, 0.55) !important;
}

html.mma-layout-custom .star-value {
    color: #ffffff !important;
    background: #ed4f76 !important;
}

html.mma-layout-custom .status {
    color: #7fffc4 !important;
}

*/

/* Narrow layout adaptation / Адаптация для узкого формата. */
@media (max-width: 430px) {
    html.mma-layout-custom .star-value {
        font-size: 40px !important;
    }

    html.mma-layout-custom .star-subtitle {
        font-size: 22px !important;
    }
}
";
        }
    }
}

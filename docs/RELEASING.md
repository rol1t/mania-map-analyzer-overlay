# Releasing / Выпуск версии

## 2.1.0 release notes

- The Avalonia GUI is the only user-facing entry point: it prepares compatible tosu and ManiaMapAnalyser components, verifies SHA-256 hashes, and keeps command files and PowerShell scripts for development/CI only.
- The hidden updater helper applies launcher updates after shutdown while preserving settings and custom CSS.
- The desktop overlay can be resized by dragging its edges or corners while osu! is inactive; `Ctrl` + mouse wheel remains available as an alternative.

1. Run `powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1` on Windows.
2. Launch `artifacts\payload\Mania Map Analyzer Overlay.exe` to verify the GUI bootstrap downloads compatible tosu and analyser components.
3. Test osu!stable, osu!lazer, both UI languages, normal window mode, overlay mode and shutdown.
4. Create the platform application archives and publish them as GitHub Release assets.
5. Publish SHA-256 checksums with every release.

---

1. Запустите `powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1` в Windows.
2. Запустите `artifacts\payload\Mania Map Analyzer Overlay.exe`; установка компонентов выполняется внутри GUI.
3. Проверьте osu!stable, osu!lazer, оба языка, обычное окно, оверлей и завершение tosu при выходе.
4. Создайте архивы приложения для платформ и прикрепите их к GitHub Release.
5. Публикуйте SHA-256 суммы вместе с каждым выпуском.

# Releasing / Выпуск версии

1. Run `powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1` on Windows.
2. Run the updater in the generated folder to download the pinned tosu and analyser versions.
3. Test osu!stable, osu!lazer, both UI languages, normal window mode, overlay mode and shutdown.
4. Create the installer ZIP and publish it as the GitHub Release asset.
5. Publish SHA-256 checksums with every release.

---

1. Запустите `powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1` в Windows.
2. Запустите скрипт обновления в собранной папке для загрузки закреплённых версий tosu и анализатора.
3. Проверьте osu!stable, osu!lazer, оба языка, обычное окно, оверлей и завершение tosu при выходе.
4. Создайте ZIP-архив установщика и прикрепите его к GitHub Release.
5. Публикуйте SHA-256 суммы вместе с каждым выпуском.

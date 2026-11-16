Electrical Tools — сборка для AutoCAD 2027 (.NET 10)

1. AutoCAD: команда NETLOAD -> выберите El.Plugin.dll -> Enter на вопрос об автозагрузке.
2. Перезапустите AutoCAD. Плагин сам скопирует себя в
   %APPDATA%\Autodesk\ApplicationPlugins\ (bundle).
3. Вкладка ленты «Электроавтоматика».

Если AutoCAD ругается на платформу — значит сборка не для вашей версии:
2021–2024 -> El.Plugin.2024.* (net48), 2027 -> этот пакет (net10).
Сборку для 2025/2026 (.NET 8) нужно собирать на машине с установленным AutoCAD 2025+,
см. README репозитория.

История версий: _OMNI_HISTORY рядом с чертежом. Команды: OMNI-SNAP, OMNI-AUTOSNAP,
OMNI-HISTORY, OMNI-ARCHIVE, OMNI-RESTORE.

## ru

- Исправлена синхронизация громкости и настройки автомобильного радио: шаги регулятора обрабатываются хостом по одному, без повторного выполнения состояний наведения мыши. Уменьшение громкости больше не выключает работающую магнитолу у другого игрока.
- Исправлено восстановление кнопок CD-плеера, режима радио/CD и выбранного канала. Выключенная магнитола сохраняет выключенные кнопки у подключившегося игрока.
- Двери Satsuma используют постоянный сетевой идентификатор детали при установке и переустановке. Другой игрок может открывать установленную дверь без перезахода.
- В F10 → «Синхронизация» добавлена кнопка «Синхронизировать всё». Она повторно запрашивает у хоста состояния мира и FSM, деталей, дверей, предметов, жидкостей и музыки прямо в текущем сеансе.
- Изменён игровой сетевой протокол: всем участникам нужна эта версия мода.

## en

- Fixed shared car-radio volume and tuning: the host processes individual wheel steps without replaying mouse-hover states. Turning the volume down no longer switches off another player's receiver.
- Fixed recovery of CD-player buttons, radio/CD mode and the selected station. A powered-off receiver keeps its controls disabled on guests.
- Satsuma doors use persistent part identities through installation and reinstallation, allowing another player to open a newly fitted door without reconnecting.
- Added “Synchronize everything” under F10 → Sync. It requests the host's world and FSM states, parts, doors, objects, fluids and music again within the current session.
- Updated the gameplay network protocol; every participant needs this mod version.

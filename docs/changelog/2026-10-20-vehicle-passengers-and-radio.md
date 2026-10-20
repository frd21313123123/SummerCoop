## ru

- Исправлены конфликты синхронизации машины при посадке пассажира: пассажир не определяется как водитель или игрок, толкающий машину. Объекты персонажа исключены из обработки физики и приборов автомобиля.
- Устаревшие пакеты больше не возвращают прежнего водителя и состояние машины. Последнее положение при выходе водителя передаётся надёжно.
- Приборы и подвижные элементы автомобиля передаёт владелец его физики. Их состояние периодически повторяется для восстановления после потерь пакетов.
- Исправлена повторная передача состояния радио и установка позиции воспроизведения при запуске принятого трека.

## en

- Fixed vehicle synchronization conflicts when a passenger enters: passengers are excluded from driver and hand-push detection. Player objects are excluded from vehicle physics and dashboard processing.
- Stale packets no longer restore an earlier driver or vehicle state. The final pose when leaving the driver's seat is sent reliably.
- Dashboard and moving visual elements follow the vehicle's physics authority, with periodic updates to recover from packet loss.
- Fixed repeated radio state updates and playback seeking when starting a received track.

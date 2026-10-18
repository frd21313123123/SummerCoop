# Проверка игровых привязок MWCoop в SummerCoop

Дата: 18 октября 2026.

Проверены пути к объектам, имена PlayMaker FSM и способы поиска предметов
в исходниках, входящих в `SummerCoop.dll`: `vendor/MWCoop/src/MWCoop` и
`src/MSC`. Это проверка привязок внутри игры, а не внешних веб-ссылок.
Ниже сохранены результаты первоначального аудита 0.4.8. После него
в 0.4.9 исправлены подтверждённые привязки MSC, добавлены обработчики
заказов и удалены обработчики отсутствующих предметов. Текущее поведение
и проверки описаны в [MSC-BINDINGS-049.md](MSC-BINDINGS-049.md).

## Источники и границы проверки

- Просмотрены 197 исходных файлов. Автоматический перечень нашёл 765
  строковых вхождений с путями/именами известных корней мира; 494 совпали
  с именами или путями в локальных данных сцен. Это число вхождений,
  а не число уникальных объектов или успешно проверенных механизмов.
- Использованы `.local/inspection/{mainData,level0,level1,level2}.json`
  (33 480 уникальных путей), индекс 6 425 FSM из
  `.local/msc-regression-inspect-02-regression/host-index.txt` и ранее
  записанные игровые отчёты `.local/shop-final-10-*`.
- Список из 141 несовпадения в поддерживаемых файлах — только кандидаты:
  он содержит префиксы поиска, тестовые сценарии и отключённые ветки MWC.
  Например, `CARPARTS/` как префикс не должен существовать как отдельный
  объект. В таблицы ниже включены результаты ручного просмотра кода.
- Для первоначального аудита использованы прошлые игровые прогоны.
  Последующие проверки исправлений 0.4.9 описаны в отдельном отчёте.
  Само наличие пути не доказывает правильность состояний, действий,
  владения объектом или синхронизации.
- Полный локальный машинный перечень находится в
  `build/link-audit/binding-inventory.json`; скрипт воспроизведения —
  `build/link-audit/audit_bindings.py`. Игровые дампы не добавлены в Git.

## Несовпадения в выполняемых модулях

### Заказы деталей по каталогу — нужна адаптация механизма

`vendor/MWCoop/src/MWCoop/Sync/Calls.cs`, `SpawnerPaths`, `NumbersPath`,
`AmisListPath`, `Resolve` и `Inject` по-прежнему используют:

- `CARPARTS/PARTSYSTEM/OrdersSpawnerYP`;
- `CARPARTS/PARTSYSTEM/OrdersSpawnerAMIS`;
- `CARPARTS/PARTSYSTEM/PhoneNumbers`;
- `CARPARTS/PARTSYSTEM/PostSystem/AMISOrderList`.

В проверенных данных MSC нет `CARPARTS/PARTSYSTEM`. В индексе MSC есть
`Sheets/OrderList::Data`, `Sheets/OrderList/Timer::Delivery`,
`Sheets/Magazine/ButtonOrder::Button`, а также
`STORE/LOD/ActivateStore/PostOffice/PostOrderBuy::Use`.

Сохранённый лог `shop-final-10-host/mwcoop/logs/mwcoop.log` подтверждает
`commandes -/-, catalogue False, annonces 0, boite aux lettres True`.
Почтовый ящик уже привязан к правильному
`STORE/LOD/Post Box/OrderTrigger::Open`, однако это не восстанавливает
отсутствующие создатели заказов. Замены одного пути недостаточно: в коде
ожидаются FSM `Spawn`, состояние `Create product` и списки MWC `Save`/`Order`.
Нужно отдельно сопоставить выбор заказа, оплату, доставку и создание деталей
со штатными действиями MSC, включая одновременные заказы и поздний вход.

### Шлем, кружка и зимняя одежда — часть путей имеет аналоги MSC

В `Sync/Wear.cs`, массив `Paths`, остались:

| Сейчас | Найдено в данных MSC |
| --- | --- |
| `EQUIPMENTS/Helmet/helmet(itemx)` | `ITEMS/helmet(itemx)::Use`, `::Paint` |
| `EQUIPMENTS/coffee cup(itemx)` | `ITEMS/coffee cup(itemx)::Use`, переменные `Coffee`, `Caffeine` |
| `EQUIPMENTS/winter jacket(itemx)` | Подтверждённого штатного аналога нет |
| `EQUIPMENTS/winter coverall(itemx)` | Подтверждённого штатного аналога нет |

`Find` и `SpawnPoint` также ждут `PLAYER/.../Clothing` и `HOMENEW`, которых
в проверенных данных нет. Существующий код `EnsurePairs` вызывается только
при найденных куртке **и** комбинезоне. Поэтому простая замена двух путей
не включит выдачу шлемов и кружек гостям: нужно отделить их от зимней одежды.
Шлем, уже надетый при загрузке, иногда находится через `Helmet::Logic`;
это не делает весь механизм исправным.

### Спальные мешки — оба источника шаблона сна отсутствуют

`Sync/SleepingBags.cs`, метод `Prepare`, ищет сначала
`EQUIPMENTS/sofa(itemx)/Sleep`, затем
`YARD/Building/BEDROOM1/LOD_bedroom1/Sleep 1`.

В данных и индексе MSC присутствуют:

- `ITEMS/sofa(itemx)/Sleep/SleepTrigger::Activate`;
- `YARD/Building/BEDROOM1/LOD_bedroom1/Sleep/SleepTrigger::Activate`.

В логе `shop-final-10-host/mwcoop/logs/mwcoop.log` записано
`sacs de couchage : declencheur de sommeil du jeu introuvable`.
Метод ставит `prepared = true` до поиска; после неудачи шаблон не ищется
повторно в этом уровне. Дополнительно точка выдачи мешков использует
`HOMENEW`, поэтому без неё применяется запасное положение перед хостом.
Для исправления нужны путь `Sleep` MSC, проверка дочерней FSM сна и
проверка повторного поиска после загрузки, складывания и сна двух игроков.

### Пар и утечки автомобилей — остался список машин MWC

`Sync/CarLeaks.cs`, метод `Scan`, привязывает `Systems/Leaks` к `CORRIS`
и ищет `SteamFountain*` только под `CORRIS`, `SORBET`, `BACHGLOTZ`,
`KEKMET`, `GIFU`, `MACHTWAGEN`.

У MSC найдены:

- `SATSUMA(557kg, 248)/CarSimulation/Symptoms/SteamFountain`;
- `RCO_RUSCKO12(270)/LOD/Simulation/SteamFountain`;
- штатный `Systems/Leaks` с шестью объектами утечек.

В сохранённом логе обнаружены только шесть утечек. Указанные паровые
объекты не попадают в обход из-за списка машин. Для общих утечек отсутствующая
`CORRIS` даёт `Car = null`, и `Mine` выбирает хоста независимо от водителя.
Нужно проверить привязку к Satsuma и Ruscko и передачу полномочий между
водителем и хостом; одинаковое имя эффекта само по себе недостаточно.

### Кошелёк и выплаты — часть таблицы MWC не находит источники

В `Sync/Wallet.cs`, `Pays`, `JokkePay`, `ScrapPath` и `LottoPath` остались
`Systems/Expenses`, `Systems/BankAccount`, `JOBS/FACTORY`, `JOBS/ADs`,
`JOBS/TAXIJOB/MACHTWAGEN`, `FleaMarket`, `PERAPORTTI`, `RACES/ICERACE`
и `JOBS/JOKKEHOME/HouseDrunkNew/.../PayMoney`.

Эти конкретные пути отсутствуют в проверенных данных MSC. Для оплаты килью
в индексе есть другой путь:
`JOBS/HouseDrunk/BeerCampOld/BeerCamp/KiljuBuyer/.../PayMoney::Use`.
У длинного пути различается вся иерархия, а положение Jokke меняется
в ходе игры: нужен поиск по действующим местам и сохранённым состояниям,
а не замена единственной строки.

`HookPays` продолжает искать отсутствующие пути каждые две секунды.
При этом личные деньги и обобщённый поиск `PayMoney` работают отдельно:
отсутствие старых путей **не доказывает**, что все выплаты MSC пропадают.
Для зимних работ, пособий, такси и ледовых гонок подходящий аналог
MSC не установлен.

### Полки магазина — в MSC отсутствует таблица Carried

`Sync/Stock.cs`, метод `Scan`, регистрирует магазин только по
`PlayMakerHashTableProxy` с `Stocked`, `Carried` и опциональным `Bought`;
затем ищет у товаров переменные `Name`, `ProductList`, `Inventory`.

Повторный нативный дамп уточнил причину: в MSC есть таблицы `Stocked`,
`Bought`, `Prices` и переменные `Name`, `ProductList`, `Inventory`.
Отсутствует именно `Carried`: корзина хранится в переменных `Q*` кассы.
В 0.4.9 `Stock` принимает такую структуру и синхронизирует `Bought`,
сохраняя локальную `Quantity` каждого покупателя. `MscShop` по-прежнему
отвечает за кассу, числовые списки пакетов и создание купленных предметов.

## Остатки без подтверждённого аналога MSC

- Продажа Rivett: `Sync/CarSale.cs` ищет
  `JOBS/HouseWood1/LOD/CarPos/NPCWood/WoodCaller1` и `WoodJob1Point`.
  У MSC есть другое задание с дровами; аналог покупки Rivett не установлен.
  Модуль вызывается из `Traffic`, поэтому одно отсутствие в основном
  списке `Core` не означает, что он отключён.
- Зимняя одежда, заводская работа, такси MWC, барахолка, ледовые гонки,
  специальные магазины MWC не имеют подтверждённых прямых замен.
  Их нельзя превращать в случайные механики MSC по похожему имени.
- В тестовых сценариях ещё встречаются `SORBET`, `BACHGLOTZ`, `CORRIS`,
  старые пути бардачка и защёлки капота. Это отдельные остатки тестов,
  а не доказательство ошибки обычного крепления каждой детали.

## Что уже адаптировано

- `MscShop`: касса MSC, фабрики `Spawner/CreateItems`,
  `Spawner/CreateSpraycans`, `Spawner/CreateShoppingbag`,
  `Spawner/ProductNameList`; числовые таблицы `Items`/`Spraycans`.
  Старый обработчик `Consume.HookSpawners` с `BagContentsStore` и
  `BagContentsFleetari` явно выходит при `MscRuntime.Enabled`.
- `MscParts`, `MscTuning`, `MscBelt`: `db_ThisPart`/`ThisPart`,
  `Assembly`/`Removal`, `Screw.Stage`, нумерованные расходники.
  Привязки динамических деталей строятся по реальным FSM, а не только
  по названию `SATSUMA`. Ранее проверены покупка, установка, затяжка
  и снятие масляных фильтров; это не проверка всех деталей машины.
- Старый `Home.ResolveKilju` с `EQUIPMENTS/bucket(itemx)` не запускается
  в MSC: используется отдельный `MscBrewing`.
- Правильные привязки телефона, почтового ящика, топлива и текстуры
  автомобиля перечислены в [SHOPPING-048.md](SHOPPING-048.md).
- `MscRuntime.Supports` отключает ряд зимних модулей целиком. Сам факт
  присутствия их строк в DLL не означает выполнение этой логики.

## Лаунчер

Из `launcher/` удалены оставшийся адрес Ko-fi исходного автора,
обработчик/идентификатор кнопки и относящиеся к нему устаревшие подписи
и комментарии. Механизм Discord и страницы API в используемом лаунчере
отсутствовали до этой правки. Архив `vendor/MWCoop/launcher` не компилируется
в действующий EXE; исторические сведения об авторах сохранены.

Локальный EXE пересобран как `0.4.8-local` в канонической папке
`SummerCoop-Launcher/`; новый Release не публиковался. Строковая проверка
EXE не нашла удалённый Ko-fi, адреса исходного MWCoop, Nexus MWC,
приглашение Discord или Steam-ссылки с app ID MWC. SHA-256 соответствует
сопутствующему файлу, пользовательский INI не изменён.

На этом EXE с явным `-LauncherPath` прошли 28 проверок
`test-native-updates.ps1`, 31 проверка `test-steam-binding.ps1` и
18 проверок `test-release-notes.ps1`. Проверки обновления также подтвердили,
что установщик использует две встроенные DLL, а не устаревшие соседние файлы.

# Roadmap: паритет с ChameleonUltraGUI (Flutter)

Чего не хватает Avalonia GUI по сравнению с оригинальным приложением
[GameTec-live/ChameleonUltraGUI](https://github.com/GameTec-live/ChameleonUltraGUI).
Отмечайте `[x]` по мере реализации.

## Статус

Сделано: сохранённые карты (+ импорт Flutter/PM3/Flipper/MCT/bin/eml, экспорт,
назначение в слот), редактор слота с настройками эмулятора MFC/UL, чтение LF
(HID/ioProx/Viking/PAC/Jablotron) и определение типа HF + PRNG, словари ключей,
запись на T55xx и Gen2/CUID magic, hex-редактор дампа, настройки устройства
(long-press, порог, BLE, factory reset), Dashboard (used slots + переключатель
слота), About, бэкап карт/словарей, сбор nonce'ов mfkey32.

Осталось (тяжёлые/зависящие от нативного кода или железа — намеренно не делались
наполовину):
- Восстановление ключей в приложении (darkside/nested/hardnested и вычисление
  ключа mfkey32/64) — нужен порт crapto1/нативная библиотека из `software/src`.
- HF/LF сниффинг: захват и декодирование.
- Прошивка/DFU и проверка обновлений — риск «окирпичить», нужен nRF DFU и
  загрузка релизов; не протестировать без устройства.
- Полная локализация (~20 языков).
- Папки для карт/словарей, QR-импорт/экспорт, сравнение дампов, NDEF-редактор,
  чтение Ultralight с ключом, непрерывное сканирование, override типа, EM4x05.

Проверялось на программном эмуляторе устройства (TCP), не на реальном Chameleon.

## 1. Сохранённые карты (Saved Cards)
- [x] Локальная библиотека карт: имя, цвет, тип тега, UID/SAK/ATQA/ATS, данные дампа
- [x] Сохранение со страницы Read («Save as…», «Save only UID»)
- [x] Сохранение дампа со страницы Dump
- [x] Ручное создание карты (Create card) и её редактирование (Edit card)
- [x] Просмотр карты (View dump)
- [x] **Назначение карты в слот** (тип + UID/дамп/ID → эмулятор → save)
- [x] Удаление с подтверждением
- [x] Импорт: собственный JSON, Proxmark3 JSON, Flipper NFC (`.nfc`), Flipper RFID (`.rfid`), MCT (`.mct`), `.bin` / `.eml`
- [x] Экспорт карты в файл (JSON)
- [ ] Папки (создание, вложенность, цвет, перемещение, экспорт/импорт папки)
- [ ] Экспорт/импорт через QR-коды
- [ ] Сравнение двух дампов (Compare / Difference)

## 2. Словари ключей (Dictionaries)
- [x] Список словарей: создание, редактирование, удаление, импорт/экспорт (папки — ещё нет)
- [ ] Скачивание внешних словарей (Dictionary download)
- [x] Словари используются при чтении дампа (вместе со встроенными ключами)
- [ ] Сохранение найденных ключей (в файл / в существующий словарь / в новый), копирование ключей

## 3. Менеджер слотов
- [x] Смена типа тега слота (HF и LF отдельно)
- [x] Переименование (nick) HF/LF
- [x] Включение/выключение HF/LF в слоте
- [x] Сброс слота к данным по умолчанию и очистка слота
- [x] Назначение сохранённой карты в слот (см. 1)
- [ ] Редактирование данных слота: UID/SAK/ATQA/ATS, LF ID (EM410x, HID Prox, ioProx, PAC, Viking, IDTECK, Jablotron)
- [ ] Экспорт данных слота (в файл, в новую карту, обновить существующую)
- [x] Настройки эмулятора MIFARE Classic: Gen1A, Gen2, «UID/SAK/ATQA from block 0», write mode (Normal/Decline/Deceive/Shadow), detection (сбор nonce'ов), field-off-do-reset, PRNG type
- [x] Настройки эмулятора Ultralight/NTAG: UID magic mode, write mode, version/signature/counters, reset auth count, password detection
- [ ] Просмотр/редактирование дампа, загруженного в слот

## 4. Чтение карт (Read Card)
- [x] LF: HID Prox (Facility Code, Issue Level), ioProx, PAC/Stanley, Viking, Jablotron (EM4x05 — ещё нет)
- [x] HF: определение типа (MFC Mini/1K/2K/4K, Ultralight/NTAG, ISO14443-4) и PRNG type (backdoor — ещё нет)
- [ ] Override card type, Continuous scan
- [ ] Восстановление ключей MFC: darkside, nested, static nested, hardnested, backdoor / static encrypted nested; прогресс, skip/resume, отметки ключей по секторам, дамп частичных данных
- [ ] Чтение Gen1-карт через backdoor
- [ ] MIFARE Ultralight/NTAG: чтение с ключом/без, version, signature, counters
- [ ] Режим Lite: предупреждение, что Lite не читает карты

## 5. Запись карт (Write Card)
- [x] Выбор сохранённой карты для записи
- [x] Magic-карты Gen2 / CUID (Gen1A/Gen3 backdoor — ещё нет)
- [ ] Ultralight, включая lock bytes и counter
- [x] LF на T55xx: EM410x, HID Prox, ioProx, PAC, Viking, IDTECK, Jablotron, Electra
- [ ] Vanity SAK, предупреждения (OTP-карты, совместимость дампа), «Write again»

## 6. Редакторы
- [x] Dump Editor: редактирование дампа как hex (ASCII-вид, ACL/value-block decode, BCC — ещё нет)
- [ ] Редактор Ultralight (lock bytes, password)
- [ ] NDEF Editor

## 7. Инструменты (Tools)
- [x] mfkey32: сбор nonce'ов из detection log и генерация команды mfkey32v2 (крипто-восстановление ключа — внешним инструментом)
- [ ] HF sniffing (кадры, UID/протокол, AIDs, восстановление ключей из auth, загрузка `.trace`)
- [ ] LF sniffing (ADC, осциллограмма, анализ модуляции, декодирование, загрузка `.bin`)
- [ ] T55xx password cleaner
- [ ] Mifare Classic Gen4 (WIP в оригинале)

## 8. Настройки устройства
- [x] Долгое нажатие A/B
- [x] Порог долгого нажатия (long press threshold)
- [x] BLE: PIN, pairing on/off, очистка bonded-устройств
- [x] Factory reset (`WipeFds`)
- [ ] Учёт `GetDeviceCapabilities` (скрывать неподдерживаемое)
- [ ] Предупреждение об устаревшем протоколе/прошивке

## 9. Прошивка и обновления
- [ ] Проверка обновлений (GitHub releases) для Ultra и Lite
- [ ] DFU: вход, прошивка последней версии, прошивка из `.zip`, force flashing
- [ ] Обнаружение устройства в DFU-режиме, страница прогресса прошивки

## 10. Главная (Dashboard)
- [x] Used slots, быстрый переключатель активного слота
- [ ] Индикация demo/эмулированного устройства
- [ ] Быстрый доступ к DFU и настройкам устройства

## 11. Подключение
- [ ] Ручное подключение по имени serial-порта
- [ ] Авто-сканирование и автоподключение к первому устройству
- [ ] BLE-подсказки (пароль 123456, первое подключение, разрешения Android)
- [ ] Эмулированное (demo) устройство

## 12. Настройки приложения
- [ ] Тема (System/Light/Dark) и цветовая схема
- [ ] Локализация
- [ ] Режим боковой панели (Expand / Retract / Auto)
- [x] Экспорт/импорт карт и словарей одним файлом-бэкапом (JSON); QR — ещё нет
- [ ] Логи: production logging, просмотр, копирование, экспорт, очистка
- [ ] Debug-страница / debug mode

## 13. Прочее
- [x] About (версия, ссылки, трейдмарки) — в настройках
- [ ] Changelog
- [ ] Биндинги к нативной библиотеке восстановления ключей (`software/src`) — нужны для 4 (recovery) и 7

## Порядок работ
1. Сохранённые карты + назначение карты в слот (1, частично 3)
2. Полноценный редактор слота (3)
3. Чтение LF-протоколов и Ultralight, словари (2, 4)
4. Биндинги к `software/src` для recovery, mfkey, sniffing (4, 7, 13)
